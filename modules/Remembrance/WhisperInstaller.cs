using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Finds, or fetches, the local Whisper this module transcribes with.
    ///
    /// This exists because the setup friction was the real blocker to anyone else testing Remembrance: the
    /// module took two file paths and offered no way to obtain what they point at, so a tester had to go
    /// install a C++ binary and a 141 MB model by hand before the module did anything at all.
    ///
    /// DETECT before download, always. whisper.cpp is not redistributed by this repo (see
    /// THIRD_PARTY_NOTICES.md); everything here is fetched from upstream, on an explicit user action, into
    /// the module's own storage. Nothing is installed machine-wide, registered, or put on PATH.
    ///
    /// Mirrors scripts-utilities\scripts\install-whisper.ps1 so the two agree on where things live and which
    /// asset is wanted, including its finding that the Hugging Face model URL 302-redirects to an LFS CDN.
    /// </summary>
    internal static class WhisperInstaller
    {
        /// <summary>The GGML models offered. English-only variants: this module transcribes meetings, and the
        /// .en models are smaller and sharper than the multilingual ones at the same size.</summary>
        public static readonly ModelChoice[] Models = new[]
        {
            new ModelChoice("ggml-tiny.en.bin",  "tiny.en (~75 MB, fastest, least accurate)",   40L * 1024 * 1024),
            new ModelChoice("ggml-base.en.bin",  "base.en (~142 MB, recommended)",              90L * 1024 * 1024),
            new ModelChoice("ggml-small.en.bin", "small.en (~466 MB, slowest, most accurate)", 300L * 1024 * 1024),
        };

        public const string DefaultModelId = "ggml-base.en.bin";

        /// <summary>
        /// The release LIST, not releases/latest, and under the repo's current owner.
        ///
        /// Two upstream changes broke the old URL, and the second is the one that matters.
        /// ggerganov/whisper.cpp moved to ggml-org/whisper.cpp, which still 301s and so was
        /// survivable. What is not survivable is that upstream now tags TWO kinds of release: the
        /// semantic ones (v1.9.3, v1.9.4) carry no binaries at all, and the Windows zips are
        /// published on the rolling build tags (b5130). GitHub calls the newest semantic tag
        /// "latest", so releases/latest answers 200 with an empty assets array, for ever.
        ///
        /// Walking the list and taking the newest release that actually carries a Windows x64 zip
        /// is indifferent to which tagging scheme upstream uses, so it survives them changing their
        /// mind again.
        /// </summary>
        private const string ReleaseApiUrl =
            "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=20";
        private const string ModelUrlPrefix = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

        /// <summary>
        /// The human releases PAGE, not the API endpoint the installer uses.
        ///
        /// A person needs to see the asset list and pick the build for their machine; the API URL
        /// would hand them JSON. Separate constant rather than deriving one from the other,
        /// because they are different things that happen to share a repository.
        /// </summary>
        public const string ReleasesPageUrl = "https://github.com/ggml-org/whisper.cpp/releases/latest";

        // GitHub rejects API requests with no User-Agent.
        private const string UserAgent = "DesktopAICompanion-Remembrance";

        internal sealed class ModelChoice
        {
            public readonly string Id;
            public readonly string Display;
            public readonly long MinimumBytes;
            public ModelChoice(string id, string display, long minimumBytes)
            {
                Id = id; Display = display; MinimumBytes = minimumBytes;
            }
        }

        // ---- pure helpers (self-testable, no network, no disk) --------------------------------------

        public static bool IsSupportedModel(string modelId)
        {
            return !string.IsNullOrWhiteSpace(modelId) &&
                   Models.Any(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        }

        public static string ResolveModelId(string modelId)
        {
            return IsSupportedModel(modelId) ? modelId : DefaultModelId;
        }

        public static string ModelUrl(string modelId)
        {
            return ModelUrlPrefix + ResolveModelId(modelId);
        }

        public static long MinimumModelBytes(string modelId)
        {
            string id = ResolveModelId(modelId);
            ModelChoice choice = Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            return choice != null ? choice.MinimumBytes : 40L * 1024 * 1024;
        }

        /// <summary>Pick the Windows x64 CLI asset. Exact name first, then any bin-x64 zip, matching the
        /// install script: whisper.cpp has renamed this asset before.</summary>
        public static string PickAssetName(IEnumerable<string> assetNames)
        {
            if (assetNames == null) return null;
            List<string> names = assetNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            string exact = names.FirstOrDefault(n => string.Equals(n, "whisper-bin-x64.zip", StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            return names.FirstOrDefault(n =>
                n.IndexOf("bin-x64", StringComparison.OrdinalIgnoreCase) >= 0 &&
                n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The release API reports digests as "sha256:&lt;hex&gt;". Returns null when absent or not
        /// sha256, which means "no digest to verify against" rather than "verification failed".</summary>
        public static string ParseSha256(string digest)
        {
            if (string.IsNullOrWhiteSpace(digest)) return null;
            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            string hex = digest.Substring(prefix.Length).Trim();
            if (hex.Length != 64) return null;
            foreach (char c in hex)
            {
                bool hexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hexDigit) return null;
            }
            return hex;
        }

        /// <summary>Where an install writes, under the module's own data directory.</summary>
        public static string InstallRoot(string moduleDataDirectory)
        {
            string root = string.IsNullOrWhiteSpace(moduleDataDirectory)
                ? Path.Combine(Path.GetTempPath(), "DesktopAICompanion-Remembrance")
                : moduleDataDirectory;
            return Path.Combine(root, "whisper");
        }

        /// <summary>Directories searched for an existing install, most specific first. The DevToolbox path is
        /// where scripts-utilities\scripts\install-whisper.ps1 puts things, so a box provisioned that way is
        /// detected rather than downloaded again.</summary>
        public static IReadOnlyList<string> ProbeRoots(string moduleDataDirectory)
        {
            var roots = new List<string>();
            string installed = InstallRoot(moduleDataDirectory);
            if (!string.IsNullOrWhiteSpace(installed)) roots.Add(installed);

            string toolbox = Environment.GetEnvironmentVariable("CODEX_TOOLBOX");
            if (string.IsNullOrWhiteSpace(toolbox))
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData)) toolbox = Path.Combine(localAppData, "DevToolbox");
            }
            if (!string.IsNullOrWhiteSpace(toolbox)) roots.Add(Path.Combine(toolbox, "whisper"));
            return roots;
        }

        // ---- detection -----------------------------------------------------------------------------

        /// <summary>Find an existing whisper-cli + model. Returns false without touching settings when
        /// nothing is found, so a caller can offer the download instead.</summary>
        public static bool TryDetect(string moduleDataDirectory, out string exePath, out string modelPath)
        {
            exePath = null;
            modelPath = null;
            foreach (string root in ProbeRoots(moduleDataDirectory))
            {
                string exe = FindExecutable(root);
                string model = FindModel(root);
                if (exe != null && model != null)
                {
                    exePath = exe;
                    modelPath = model;
                    return true;
                }
                // Remember a partial find, so "exe here, model there" still beats reporting nothing.
                if (exe != null && exePath == null) exePath = exe;
                if (model != null && modelPath == null) modelPath = model;
            }
            return exePath != null && modelPath != null;
        }

        /// <summary>whisper-cli.exe, else main.exe (the pre-rename name), searched recursively because the
        /// release zip nests them under bin\Release\.</summary>
        public static string FindExecutable(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            foreach (string name in new[] { "whisper-cli.exe", "main.exe" })
            {
                try
                {
                    string hit = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
                catch { }
            }
            return null;
        }

        /// <summary>The largest *.bin under the root. Size is the right tie-breaker: whisper models are big,
        /// and a bigger one is the better model when several are present.</summary>
        public static string FindModel(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            try
            {
                return Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .Where(f => f.Length > 10L * 1024 * 1024)   // skip stray small .bin files
                    .OrderByDescending(f => f.Length)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        // ---- install -------------------------------------------------------------------------------

        public sealed class InstallResult
        {
            public bool Ok;
            public string ExePath;
            public string ModelPath;
            public string Message;
        }

        /// <summary>
        /// Fetch the CLI and the model into <paramref name="root"/>. Reports progress through
        /// <paramref name="report"/> (called off the UI thread). Never throws: a failure comes back as
        /// Ok=false with a message a user can act on, because the manual Browse actions remain the fallback.
        /// </summary>
        public static async Task<InstallResult> InstallAsync(string root, string modelId, Action<string> report,
            CancellationToken cancellationToken)
        {
            var result = new InstallResult { Ok = false };
            Action<string> say = report ?? delegate { };
            string model = ResolveModelId(modelId);

            try
            {
                Directory.CreateDirectory(root);
                string binDirectory = Path.Combine(root, "bin");
                string modelDirectory = Path.Combine(root, "models");
                Directory.CreateDirectory(binDirectory);
                Directory.CreateDirectory(modelDirectory);

                using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
                using (var http = new HttpClient(handler))
                {
                    // The model is hundreds of MB on a slow link; the default 100s would abort it.
                    http.Timeout = TimeSpan.FromMinutes(60);
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

                    // ---- 1. the CLI ----
                    string exe = FindExecutable(root);
                    if (exe != null)
                    {
                        say("whisper-cli already present, keeping it");
                    }
                    else
                    {
                        say("looking up the latest whisper.cpp release...");
                        AssetLookup lookup = await ResolveAssetAsync(http, cancellationToken).ConfigureAwait(false);
                        if (lookup.Asset == null)
                        {
                            result.Message = lookup.Failure;
                            return result;
                        }
                        ReleaseAsset asset = lookup.Asset;
                        string assetName = asset.Name;

                        string zipPath = Path.Combine(root, assetName);
                        say("downloading " + assetName + "...");
                        await DownloadAsync(http, asset.Url, zipPath, say, cancellationToken).ConfigureAwait(false);

                        string expected = ParseSha256(asset.Digest);
                        if (expected != null)
                        {
                            say("verifying " + assetName + "...");
                            string actual = Sha256File(zipPath);
                            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                            {
                                TryDelete(zipPath);
                                result.Message = "The downloaded " + assetName + " failed SHA-256 verification; it was deleted.";
                                return result;
                            }
                        }

                        say("extracting...");
                        // ExtractToDirectory refuses entries that escape the destination, so a hostile zip
                        // cannot write outside binDirectory.
                        ZipFile.ExtractToDirectory(zipPath, binDirectory, true);
                        TryDelete(zipPath);

                        exe = FindExecutable(root);
                        if (exe == null)
                        {
                            result.Message = "Extracted " + assetName + " but found no whisper-cli.exe or main.exe inside it.";
                            return result;
                        }
                    }

                    // ---- 2. the model ----
                    string modelPath = Path.Combine(modelDirectory, model);
                    long minimum = MinimumModelBytes(model);
                    if (File.Exists(modelPath) && new FileInfo(modelPath).Length >= minimum)
                    {
                        say(model + " already present, keeping it");
                    }
                    else
                    {
                        say("downloading " + model + " (this is the large one)...");
                        await DownloadAsync(http, ModelUrl(model), modelPath, say, cancellationToken).ConfigureAwait(false);
                        if (!File.Exists(modelPath) || new FileInfo(modelPath).Length < minimum)
                        {
                            TryDelete(modelPath);
                            result.Message = "The " + model + " download finished but the file is too small to be that model.";
                            return result;
                        }
                    }

                    // ---- 3. prove it actually runs ----
                    say("checking that Whisper runs...");
                    string detail;
                    if (!TryVerify(exe, modelPath, out detail))
                    {
                        result.ExePath = exe;
                        result.ModelPath = modelPath;
                        result.Message = "Installed, but the check did not pass: " + detail;
                        return result;
                    }

                    result.Ok = true;
                    result.ExePath = exe;
                    result.ModelPath = modelPath;
                    result.Message = "Whisper is ready.";
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                result.Message = "Setup was cancelled.";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = DescribeFetchFailure("", ex);
                return result;
            }
        }

        internal sealed class ReleaseAsset
        {
            public string Name;
            public string Url;
            public string Digest;
        }

        /// <summary>The chosen asset, or the reason there is not one. An async method cannot take
        /// `out` parameters, hence the small return type.</summary>
        public sealed class AssetLookup
        {
            public ReleaseAsset Asset;
            public string Failure;
        }

        /// <summary>
        /// Why the release lookup failed, in the words of what actually happened.
        ///
        /// This used to be one hardcoded sentence blaming rate limiting, emitted for every failure
        /// mode there is. On 2026-09-20 it sent a user to wait out a throttle that was not happening
        /// -- the box had 57 of its 60 anonymous requests left, GitHub had answered 200, and the real
        /// fault was an empty assets array. A message that cannot distinguish its causes is not a
        /// diagnosis, it is a guess with a confident voice.
        /// </summary>
        internal static string DescribeHttpFailure(int status, string rateLimitRemaining)
        {
            if ((status == 403 || status == 429) &&
                string.Equals((rateLimitRemaining ?? "").Trim(), "0", StringComparison.Ordinal))
                return "GitHub is rate-limiting this machine (60 requests an hour without a token). " +
                       "Try again later, or install Whisper yourself and use the Browse actions.";
            return "GitHub answered HTTP " + status.ToString(CultureInfo.InvariantCulture) +
                   " for the whisper.cpp release list.";
        }

        /// <summary>
        /// The whole exception chain, because for the failures that matter here the OUTER message
        /// is the useless half.
        ///
        /// .NET reports a TLS failure as "The SSL connection could not be established, see inner
        /// exception" -- a message that names the existence of the information you need and then
        /// withholds it. Reported from a real pane on 2026-09-22: the button said exactly that,
        /// and the cause was unknowable from the UI without attaching a debugger. The inner
        /// exception is usually an AuthenticationException wrapping a Win32Exception whose text
        /// names the actual Schannel fault.
        ///
        /// Capped at four links and de-duplicated, because a chain can repeat the same sentence
        /// and a settings pane is not a stack trace viewer.
        /// </summary>
        internal static string DescribeChain(Exception ex)
        {
            var parts = new List<string>();
            for (Exception e = ex; e != null && parts.Count < 4; e = e.InnerException)
            {
                string message = (e.Message ?? "").Trim();
                if (message.Length == 0) continue;
                if (parts.Contains(message)) continue;
                parts.Add(message);
            }
            return parts.Count == 0 ? "(no detail)" : string.Join(" -> ", parts.ToArray());
        }

        /// <summary>
        /// Was this connection killed LOCALLY rather than failing to open?
        ///
        /// WSAECONNABORTED (10053) and WSAECONNRESET (10054) mean a connection that was already
        /// established went away, which is a different thing from a name that will not resolve or
        /// a port that refuses. When it happens on every attempt from one program while other
        /// programs on the same machine succeed, something on the box is doing it on purpose.
        /// </summary>
        internal static bool IsConnectionAborted(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                var socket = e as SocketException;
                if (socket == null) continue;
                if (socket.SocketErrorCode == SocketError.ConnectionAborted) return true;
                if (socket.SocketErrorCode == SocketError.ConnectionReset) return true;
            }
            return false;
        }

        /// <summary>
        /// Microsoft Defender Network Protection: 0 off, 1 blocking, 2 audit, null unknown.
        ///
        /// Read from the registry rather than by running Get-MpPreference, because a settings pane
        /// must not spawn PowerShell to render an error message. Policy path first: on a managed
        /// machine the GPO value is the one in force and the effective key may disagree.
        ///
        /// MEASURED on two machines 2026-09-22, which is the whole reason this exists. The machine
        /// where the download works reads 0; the machine where it fails reads 1, and on that one a
        /// signed PowerShell reaches the same URL with HTTP 200 while this app's connection is
        /// aborted mid-handshake. Network Protection scores the CALLING PROGRAM, and this app
        /// ships unsigned with no prevalence, asking to download an executable from a release
        /// page. That is the shape the feature exists to stop.
        ///
        /// Reading a machine policy value is not user content and needs no permission bit; it is
        /// the same kind of local configuration read the module already does to find whisper-cli.
        /// </summary>
        internal static int? NetworkProtectionState()
        {
            string[] keys =
            {
                @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Network Protection",
                @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Network Protection",
            };
            foreach (string key in keys)
            {
                try
                {
                    using (Microsoft.Win32.RegistryKey k =
                               Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key))
                    {
                        if (k == null) continue;
                        object value = k.GetValue("EnableNetworkProtection");
                        if (value == null) continue;
                        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>
        /// The failure, plus what to DO about it when the cause is recognisable.
        ///
        /// A dead end that reports a TLS exception chain is honest and useless. When the shape
        /// says "something local killed this", the next step is not a retry, it is the two Browse
        /// buttons that already exist on this pane: fetch the files in a browser, which endpoint
        /// protection trusts, and point the module at them. Transcription is not blocked, only the
        /// automatic download is, and nothing in the old message said so.
        /// </summary>
        internal static string DescribeFetchFailure(string prefix, Exception ex)
        {
            string text = prefix + DescribeChain(ex);
            if (!IsConnectionAborted(ex)) return text;

            text += "  This was cut off locally rather than failing to connect, which usually means "
                  + "endpoint protection stopped it.";
            int? protection = NetworkProtectionState();
            if (protection == 1)
                text += "  Microsoft Defender Network Protection is ON, and it terminates "
                      + "connections made by programs it does not recognise. This app is unsigned, "
                      + "so it will not be recognised.";
            text += "  You do not need the download: fetch whisper.cpp and the model in a browser, "
                  + "then use \"Browse for whisper-cli...\" and \"Browse for a model...\" below.";
            return text;
        }

        private static async Task<AssetLookup> ResolveAssetAsync(HttpClient http, CancellationToken cancellationToken)
        {
            string json;
            try
            {
                using (HttpResponseMessage response = await http.GetAsync(ReleaseApiUrl, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string remaining = null;
                        IEnumerable<string> values;
                        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out values))
                            foreach (string v in values) { remaining = v; break; }
                        return new AssetLookup
                        {
                            Failure = DescribeHttpFailure((int)response.StatusCode, remaining),
                        };
                    }
                    json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                return new AssetLookup
                {
                    Failure = DescribeFetchFailure("Could not reach GitHub: ", ex),
                };
            }

            ReleaseAsset asset = ParseReleaseListJson(json);
            if (asset == null)
                return new AssetLookup
                {
                    Failure = "GitHub answered, but none of the 20 most recent whisper.cpp releases " +
                              "carries a Windows x64 build. Install Whisper yourself and use the " +
                              "Browse actions.",
                };
            return new AssetLookup { Asset = asset };
        }

        /// <summary>
        /// The newest release in the list that actually carries a Windows x64 zip.
        ///
        /// Order is the API's, which is newest first, so the first hit is the newest usable build.
        /// A release with no assets is skipped rather than ending the search: that is the exact shape
        /// upstream ships now, with asset-less v1.9.x tags interleaved among the bXXXX builds.
        /// </summary>
        public static ReleaseAsset ParseReleaseListJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
                    foreach (JsonElement release in document.RootElement.EnumerateArray())
                    {
                        ReleaseAsset hit = AssetFrom(release);
                        if (hit != null) return hit;
                    }
                }
            }
            catch { return null; }
            return null;
        }

        /// <summary>Split out from the fetch so the selection logic is self-testable without a network.</summary>
        public static ReleaseAsset ParseReleaseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    return AssetFrom(document.RootElement);
                }
            }
            catch { return null; }
        }

        /// <summary>The Windows x64 asset of ONE release object, or null if it carries none.</summary>
        private static ReleaseAsset AssetFrom(JsonElement release)
        {
            try
            {
                {
                    if (release.ValueKind != JsonValueKind.Object) return null;
                    JsonElement assets;
                    if (!release.TryGetProperty("assets", out assets) ||
                        assets.ValueKind != JsonValueKind.Array) return null;

                    var byName = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        JsonElement nameElement;
                        if (asset.ValueKind != JsonValueKind.Object) continue;
                        if (!asset.TryGetProperty("name", out nameElement) ||
                            nameElement.ValueKind != JsonValueKind.String) continue;
                        string name = nameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(name) && !byName.ContainsKey(name)) byName[name] = asset;
                    }

                    string chosen = PickAssetName(byName.Keys);
                    if (chosen == null) return null;

                    JsonElement selected = byName[chosen];
                    JsonElement urlElement;
                    if (!selected.TryGetProperty("browser_download_url", out urlElement) ||
                        urlElement.ValueKind != JsonValueKind.String) return null;
                    string url = urlElement.GetString();
                    if (string.IsNullOrWhiteSpace(url)) return null;

                    string digest = null;
                    JsonElement digestElement;
                    if (selected.TryGetProperty("digest", out digestElement) &&
                        digestElement.ValueKind == JsonValueKind.String) digest = digestElement.GetString();

                    return new ReleaseAsset { Name = chosen, Url = url, Digest = digest };
                }
            }
            catch { return null; }
        }

        private static async Task DownloadAsync(HttpClient http, string url, string destination,
            Action<string> report, CancellationToken cancellationToken)
        {
            string temporary = destination + ".part";
            TryDelete(temporary);
            using (HttpResponseMessage response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                string label = Path.GetFileName(destination);

                using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buffer = new byte[1 << 16];
                    long written = 0;
                    int lastReported = -1;
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        written += read;
                        if (total.HasValue && total.Value > 0)
                        {
                            int percent = (int)(written * 100 / total.Value);
                            if (percent >= lastReported + 5)
                            {
                                lastReported = percent;
                                report(label + ": " + percent + "%");
                            }
                        }
                    }
                }
            }
            TryDelete(destination);
            File.Move(temporary, destination);
        }

        // ---- verification --------------------------------------------------------------------------

        /// <summary>
        /// Run the real CLI against the real model on one second of generated silence.
        ///
        /// Exit code 0 is the assertion, NOT transcript content: silence legitimately transcribes to nothing,
        /// so requiring text would fail a working install. What this proves is the part that actually breaks
        /// -- that the exe resolves its DLLs and that the model file loads.
        /// </summary>
        public static bool TryVerify(string exePath, string modelPath, out string detail)
        {
            detail = "";
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) { detail = "whisper-cli was not found."; return false; }
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) { detail = "the model file was not found."; return false; }

            string scratch = Path.Combine(Path.GetTempPath(), "dp-whisper-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(scratch);
                string wav = Path.Combine(scratch, "check.wav");
                byte[] silence = ModuleKit.WavAudio.FromPcm(new short[16000], 16000, 1);
                if (silence == null) { detail = "could not build the check clip."; return false; }
                File.WriteAllBytes(wav, silence);

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                };
                psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(modelPath);
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(wav);
                psi.ArgumentList.Add("-otxt");
                psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(Path.Combine(scratch, "check"));

                using (Process process = Process.Start(psi))
                {
                    if (process == null) { detail = "the process did not start."; return false; }
                    string standardError = process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(5 * 60 * 1000))
                    {
                        try { process.Kill(); } catch { }
                        detail = "it did not finish within five minutes.";
                        return false;
                    }
                    if (process.ExitCode != 0)
                    {
                        string tail = (standardError ?? "").Trim();
                        if (tail.Length > 200) tail = tail.Substring(tail.Length - 200);
                        detail = "whisper-cli exited " + process.ExitCode +
                                 (tail.Length > 0 ? (" -- " + tail) : "");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex) { detail = DescribeChain(ex); return false; }
            finally
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
            }
        }

        // ---- small utilities -----------------------------------------------------------------------

        private static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
