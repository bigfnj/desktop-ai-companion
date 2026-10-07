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
        /// The human releases LIST, not the API endpoint the installer uses, and not /latest.
        ///
        /// A person needs to see the asset list and pick the build for their machine; the API URL
        /// would hand them JSON. Separate constant rather than deriving one from the other,
        /// because they are different things that happen to share a repository.
        ///
        /// NOT releases/latest, for the reason ReleaseApiUrl's comment gives: GitHub's "latest" is the
        /// newest non-prerelease tag, upstream marks its bXXXX builds -- the ones carrying the Windows
        /// zips -- as pre-releases, so /latest lands on an asset-less vX.Y.Z entry, permanently. This
        /// constant was written two days after that comment and pointed at /latest anyway, verified as
        /// "answers 200", which /latest does. So the one button built for people whose in-app download
        /// endpoint protection cuts off sent every one of them to a page with only source archives and
        /// reported "opened ... Save both" (F183). The list page shows the pre-release rows beneath the
        /// entry GitHub marks Latest, and the pane's success text now says which row to take.
        /// </summary>
        public const string ReleasesPageUrl = "https://github.com/ggml-org/whisper.cpp/releases";

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

        /// <summary>What "Set up Whisper for me" does next, given what TryDetect found and what the "Model to
        /// download" dropdown asks for.</summary>
        internal enum SetupStep
        {
            /// <summary>Nothing detected: fetch the CLI and the chosen model.</summary>
            InstallEverything,
            /// <summary>The detected pair already carries the chosen model: adopt it, download nothing.</summary>
            AdoptDetected,
            /// <summary>An exe was detected but its model is not the one chosen: fetch the chosen model beside the
            /// module's install, keeping the detected exe, and verify the pair.</summary>
            FetchChosenModel,
        }

        /// <summary>
        /// The decision "Set up Whisper for me" makes after detection, pure so the self-test can pin it. The label
        /// says the dropdown is "used by Set up Whisper for me", and until 2026-09-30 it was not once any exe plus
        /// any model had been detected: the early "already installed" return came before the choice was read, so a
        /// user who had installed base.en and later picked small.en pressed the button and was told base.en was
        /// installed, for ever (RA-158). The detected model is compared to the choice by FILE NAME, which is how
        /// the model ids are spelled ("ggml-base.en.bin").
        /// </summary>
        internal static SetupStep PlanSetup(bool detected, string detectedModelPath, string chosenModelId)
        {
            if (!detected) return SetupStep.InstallEverything;
            string chosen = ResolveModelId(chosenModelId);
            string found = Path.GetFileName(detectedModelPath ?? "");
            return string.Equals(found, chosen, StringComparison.OrdinalIgnoreCase)
                ? SetupStep.AdoptDetected
                : SetupStep.FetchChosenModel;
        }

        /// <summary>What "Set up and check Whisper"'s "Refresh local models" does next, given whether the two paths ON
        /// SCREEN exist and whether detection found a whole pair (2.0.0).</summary>
        internal enum RefreshStep
        {
            /// <summary>Both files on screen exist: keep them, write nothing (Apply does that), and name the other
            /// models detection found so the user can Browse to one.</summary>
            KeepOnScreen,
            /// <summary>Either file on screen is missing and detection found a pair: adopt the whole detected pair.</summary>
            AdoptDetected,
            /// <summary>Either file on screen is missing and detection found nothing: say which file is missing.</summary>
            NothingFound,
        }

        /// <summary>
        /// The decision "Refresh local models" makes, pure, in <see cref="PlanSetup"/>'s style, so the self-test pins it
        /// directly. It used to be "Find an installed Whisper", which adopted whatever detection found and so replaced a
        /// working pair the user had pointed at by hand with a different one; a pair whose two files exist is kept now,
        /// and detection is consulted only to fill in a pair that is missing.
        /// </summary>
        internal static RefreshStep PlanRefresh(bool exeOnScreenExists, bool modelOnScreenExists, bool detectedPair)
        {
            if (exeOnScreenExists && modelOnScreenExists) return RefreshStep.KeepOnScreen;
            return detectedPair ? RefreshStep.AdoptDetected : RefreshStep.NothingFound;
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
            return TryDetectIn(ProbeRoots(moduleDataDirectory), out exePath, out modelPath);
        }

        /// <summary>
        /// The file a failed run check leaves in its install root, and the reason detection then passes that
        /// root by (RA-167). An install whose whisper-cli exits non-zero -- a missing runtime, an unsupported
        /// CPU, model bytes that passed the size gate and are still wrong -- used to stay under InstallRoot
        /// exactly as it was, and the next "Set up Whisper for me" press or the next session's pane Load
        /// found exe plus model there and adopted the pair unverified; the failure result's paths were
        /// written and read by nothing. The files are KEPT (a 466 MB model is not re-downloaded on a guess,
        /// and the fault may be the runtime rather than the bytes); what changes is that nothing uses them
        /// until a later check passes and <see cref="ClearUnverified"/> removes this file.
        /// </summary>
        internal const string UnverifiedMarkerName = "check-failed.txt";

        /// <summary>TryDetect over an explicit list of roots, so the self-test can hand it a scratch root and
        /// none of the machine-dependent ones. A root carrying <see cref="UnverifiedMarkerName"/> is skipped
        /// whole, exe and model both.</summary>
        internal static bool TryDetectIn(IEnumerable<string> roots, out string exePath, out string modelPath)
        {
            exePath = null;
            modelPath = null;
            if (roots == null) return false;
            foreach (string root in roots)
            {
                if (IsMarkedUnverified(root)) continue;
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

        internal static bool IsMarkedUnverified(string root)
        {
            try { return !string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, UnverifiedMarkerName)); }
            catch { return false; }
        }

        /// <summary>Record that the install under <paramref name="root"/> failed its run check, with the reason
        /// and the time, so a person opening the folder sees why nothing uses it.</summary>
        internal static void MarkUnverified(string root, string detail)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root)) return;
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, UnverifiedMarkerName),
                    "The whisper.cpp install in this folder failed its run check on " +
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + ":" +
                    Environment.NewLine + (detail ?? "") + Environment.NewLine +
                    "Remembrance will not use it until \"Set up Whisper for me\" passes the check; delete this folder to start over." +
                    Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        internal static void ClearUnverified(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            TryDelete(Path.Combine(root, UnverifiedMarkerName));
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

        /// <summary>The smallest .bin detection takes for a real ggml model; a stray small .bin is not one (the smallest
        /// model offered, tiny.en, is about 75 MB). The Whisper Validate judges a model file by this same
        /// rule, so the two cannot disagree about what a model is.</summary>
        internal const long MinimumGgmlBytes = 10L * 1024 * 1024;

        /// <summary>The largest *.bin under the root. Size is the right tie-breaker: whisper models are big,
        /// and a bigger one is the better model when several are present.</summary>
        public static string FindModel(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            try
            {
                return Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .Where(f => f.Length > MinimumGgmlBytes)   // skip stray small .bin files
                    .OrderByDescending(f => f.Length)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Every file under <paramref name="roots"/> that detection would take for a ggml model (FindModel's
        /// size rule, a root carrying the check-failed marker passed over as TryDetectIn passes it over), largest first
        /// and each once: what "Refresh local models" names as "also found", so the user can Browse to one.</summary>
        internal static List<string> FindModels(IEnumerable<string> roots)
        {
            var found = new List<FileInfo>();
            if (roots != null)
                foreach (string root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root) || IsMarkedUnverified(root) || !Directory.Exists(root)) continue;
                    try
                    {
                        foreach (string path in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories))
                        {
                            var info = new FileInfo(path);
                            if (info.Length <= MinimumGgmlBytes) continue;
                            if (found.Any(f => string.Equals(f.FullName, info.FullName, StringComparison.OrdinalIgnoreCase))) continue;
                            found.Add(info);
                        }
                    }
                    catch { }
                }
            return found.OrderByDescending(f => f.Length).Select(f => f.FullName).ToList();
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
        /// Ok=false with a message a user can act on, because choosing the two files by hand remains the fallback.
        /// <paramref name="knownExe"/> is a whisper-cli detection already found elsewhere (the DevToolbox root),
        /// kept instead of fetching the zip when only the MODEL the dropdown asks for is missing (RA-158).
        /// </summary>
        public static async Task<InstallResult> InstallAsync(string root, string modelId, Action<string> report,
            CancellationToken cancellationToken, string knownExe = null)
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
                    string exe = !string.IsNullOrWhiteSpace(knownExe) && File.Exists(knownExe)
                        ? knownExe
                        : FindExecutable(root);
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
                        // Left in place but MARKED, so neither the next press nor the next session's Load
                        // adopts a pair the check has just refused (RA-167); see UnverifiedMarkerName.
                        MarkUnverified(root, detail);
                        result.ExePath = exe;
                        result.ModelPath = modelPath;
                        result.Message = "Installed under " + root + ", but the check did not pass: " + detail +
                                         " Nothing will use it until a later \"Set up Whisper for me\" passes the check;" +
                                         " delete that folder to start over.";
                        return result;
                    }
                    ClearUnverified(root);

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
                       "Try again later, or install Whisper yourself and " + ChooseFilesByHand + ".";
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
                        int? state = InterpretNetworkProtectionValue(k.GetValue("EnableNetworkProtection"));
                        if (state.HasValue) return state;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>
        /// The pure half of <see cref="NetworkProtectionState"/>: what one registry value MEANS. 0, 1 or 2 for
        /// those numbers, as a DWORD, a QWORD or their decimal string; null for absent, out of range, a byte[]
        /// or anything else, so a value this code does not understand reads as "unknown" rather than as a
        /// policy state DescribeFetchFailure would then blame Defender for. Split out so the self-test can
        /// drive every branch: the registry read itself is machine-dependent and its old check ("answers
        /// without throwing") could only fail on a value outside 0..2 already sitting in the registry (RA-163).
        /// </summary>
        internal static int? InterpretNetworkProtectionValue(object registryValue)
        {
            if (registryValue == null) return null;
            long n;
            if (registryValue is int) n = (int)registryValue;
            else if (registryValue is long) n = (long)registryValue;
            else if (registryValue is string)
            {
                if (!long.TryParse(((string)registryValue).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n))
                    return null;
            }
            else return null;
            return n >= 0 && n <= 2 ? (int?)(int)n : null;
        }

        /// <summary>
        /// How a user points the module at Whisper files they fetched themselves, said the same way by every answer that
        /// sends them there: the Browse button ("…") inside the whisper-cli path and the Whisper model file, the host 1.4.0
        /// path fields of the Transcription card. Those fields replaced the "Browse for whisper-cli…" and "Browse for a
        /// model…" buttons these answers used to name (remembrance 2.1.0, lane feature/layout-remembrance), and an answer
        /// that names a button the pane no longer has is the dead end the 1.0.7 message was written to remove.
        /// </summary>
        internal const string ChooseFilesByHand =
            "choose the two files with the \"…\" buttons on the whisper-cli path and the Whisper model file, in the Transcription card";

        /// <summary>
        /// The failure, plus what to DO about it when the cause is recognisable.
        ///
        /// A dead end that reports a TLS exception chain is honest and useless. When the shape
        /// says "something local killed this", the next step is not a retry, it is the Browse inside
        /// the two Whisper path fields on this pane: fetch the files in a browser, which endpoint
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
                  + "then " + ChooseFilesByHand + ".";
            return text;
        }

        /// <summary>
        /// How long one request may take to ANSWER (headers back), and how long a download may go without
        /// delivering a byte. Bounded separately from HttpClient.Timeout, because that is set to 60 minutes
        /// for the model bytes and, with ResponseHeadersRead, stops governing once the headers arrive anyway.
        /// A proxy that accepts the TCP connection and never answers used to hold "looking up the latest
        /// whisper.cpp release..." on the status line for the full hour, button disabled, nothing to cancel
        /// (F184). Fields rather than consts so the self-test can shorten them.
        /// </summary>
        internal static TimeSpan LookupBound = TimeSpan.FromSeconds(30);
        internal static TimeSpan ReadIdleBound = TimeSpan.FromSeconds(60);

        /// <summary>The release-list lookup, bounded by <see cref="LookupBound"/>. Internal so the self-test
        /// can hand it a client that never answers and watch it give up.</summary>
        internal static async Task<AssetLookup> ResolveAssetAsync(HttpClient http, CancellationToken cancellationToken)
        {
            string json;
            try
            {
                using (CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    bounded.CancelAfter(LookupBound);
                    using (HttpResponseMessage response = await http.GetAsync(ReleaseApiUrl, bounded.Token).ConfigureAwait(false))
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
                        json = await response.Content.ReadAsStringAsync(bounded.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The bound fired, not the user: the caller's token is untouched. Said in those words,
                // because a black-holed proxy and a cancelled action need opposite advice.
                return new AssetLookup
                {
                    Failure = "GitHub did not answer within " +
                              LookupBound.TotalSeconds.ToString(CultureInfo.InvariantCulture) +
                              " s: the connection opened and nothing came back, which is what a proxy that " +
                              "swallows api.github.com looks like. Install Whisper yourself and " + ChooseFilesByHand + ".",
                };
            }
            catch (OperationCanceledException)
            {
                // The CALLER's cancellation -- Shutdown mid-lookup -- is the caller's to report: InstallAsync
                // says "Setup was cancelled." Until 2026-09-30 it fell through to the catch below and the
                // diagnostic log read "whisper setup failed: Could not reach GitHub: A task was canceled.",
                // an app exit dressed up as a network fault (RA-168).
                throw;
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
                              "carries a Windows x64 build. Install Whisper yourself and " + ChooseFilesByHand + ".",
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

        // No single-release parser. ParseReleaseJson was the releases/latest era's entry point and, once the fetch
        // moved to the release LIST, only the self-test read it, so the suite's one digest assertion witnessed a
        // path production had left (F185). The list parser is the one path and the one the self-test reads.

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

        /// <summary>
        /// Fetch <paramref name="url"/> into <paramref name="destination"/> through a <c>.part</c> file that
        /// becomes the destination only once every byte has arrived. Internal so the self-test can drive it
        /// against a scripted handler; the two callers are InstallAsync's CLI and model steps.
        /// </summary>
        internal static async Task DownloadAsync(HttpClient http, string url, string destination,
            Action<string> report, CancellationToken cancellationToken)
        {
            string temporary = destination + ".part";
            TryDelete(temporary);
            string label = Path.GetFileName(destination);
            // The .part is deleted on EVERY way out except the rename that makes it the destination: the idle
            // bound firing, the CALLER's token (Shutdown cancels _installCts mid-install, F184), a refused
            // status, a write that fails. Until 2026-09-30 only the bound's own catch below deleted it, so an
            // install the user closed the app on left one stale partial download -- hundreds of MB for a
            // model -- until the next attempt's TryDelete at the top collected it (N-remembrance-02).
            bool completed = false;
            try
            {
                // One linked source for the whole download, re-armed before every read: the bound is on
                // SILENCE -- no headers, then no bytes -- never on the whole file, which is hundreds of MB on
                // a slow link and must be allowed to take as long as it takes (F184).
                using (CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    try
                    {
                        idle.CancelAfter(LookupBound);
                        using (HttpResponseMessage response = await http
                            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            long? total = response.Content.Headers.ContentLength;

                            using (Stream source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false))
                            using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                            {
                                var buffer = new byte[1 << 16];
                                long written = 0;
                                int lastReported = -1;
                                while (true)
                                {
                                    idle.CancelAfter(ReadIdleBound);
                                    int read = await source.ReadAsync(buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false);
                                    if (read <= 0) break;
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
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The bound, not the caller: say so. The finally below removes the partial.
                        throw new TimeoutException("The download of " + label + " stalled: no answer within " +
                            LookupBound.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s, or no data for " +
                            ReadIdleBound.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s. Try again, or " +
                            "fetch it in a browser and " + ChooseFilesByHand + ".");
                    }
                }
                TryDelete(destination);
                File.Move(temporary, destination);
                completed = true;
            }
            finally
            {
                if (!completed) TryDelete(temporary);
            }
        }

        // ---- verification --------------------------------------------------------------------------

        /// <summary>
        /// Run the real CLI against the real model on generated silence: one second for the install check, or the
        /// <paramref name="clipSamples"/> (at 16 kHz mono) a caller asks for. The Whisper Validate asks
        /// for two seconds, the length its answer names (2.0.0); the installer's own call passes nothing, so it is the
        /// check it always was.
        ///
        /// Exit code 0 is the assertion, NOT transcript content: silence legitimately transcribes to nothing,
        /// so requiring text would fail a working install. What this proves is the part that actually breaks
        /// -- that the exe resolves its DLLs and that the model file loads.
        /// </summary>
        public static bool TryVerify(string exePath, string modelPath, out string detail, int clipSamples = 16000)
        {
            detail = "";
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) { detail = "whisper-cli was not found."; return false; }
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) { detail = "the model file was not found."; return false; }

            string scratch = Path.Combine(Path.GetTempPath(), "dp-whisper-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(scratch);
                string wav = Path.Combine(scratch, "check.wav");
                byte[] silence = ModuleKit.WavAudio.FromPcm(new short[clipSamples], 16000, 1);
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
                    // Both pipes drained CONCURRENTLY, then the wait -- same defect and same fix as
                    // Transcriber.RunWhisper. Blocking on stderr first meant stdout could fill its 4 KB
                    // pipe and stop the child forever, and because a blocking read only returns at EOF,
                    // WaitForExit was always called on a finished process: the five-minute cap below could
                    // never fire.
                    System.Threading.Tasks.Task<string> errorText = process.StandardError.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> outputText = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(5 * 60 * 1000))
                    {
                        try { process.Kill(true); } catch { }
                        detail = "it did not finish within five minutes.";
                        return false;
                    }
                    // WaitForExit(int) does not guarantee the redirected readers have drained.
                    process.WaitForExit();
                    string standardError = "";
                    try { standardError = errorText.GetAwaiter().GetResult(); } catch { }
                    try { outputText.GetAwaiter().GetResult(); } catch { }
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
