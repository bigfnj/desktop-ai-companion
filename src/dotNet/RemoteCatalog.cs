using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopAICompanion.Ai;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion
{
    internal sealed class CatalogCompanion
    {
        public string Id;
        public string Name;
        public string Author;
        public string Url;
        public string Sha256;
        public int Bytes;
    }

    internal sealed class CatalogPack
    {
        public string Id;
        public string Name;
        public string Group;        // collection this pack belongs to (for grouped browsing); may be empty
        public string Description;
        public string License;
        public string Url;
        public string Sha256;
        public int Bytes;
        public int Count;
        public int DataSchema;
    }

    /// <summary>A plugin module offered for install from the catalog. <see cref="Permissions"/> mirrors
    /// the module's own declared <c>ModuleInfo.Permissions</c> so the install prompt can show what a
    /// module will be able to do BEFORE its code is ever downloaded or run.</summary>
    internal sealed class CatalogModule
    {
        public string Id;
        public string Name;
        public string Description;
        public string Version;
        public string Url;
        public string Sha256;
        public int Bytes;
        public ModulePermissions Permissions;
    }

    internal sealed class RemoteCatalog
    {
        public readonly List<CatalogCompanion> Pets = new List<CatalogCompanion>();
        public readonly List<CatalogPack> Packs = new List<CatalogPack>();
        public readonly List<CatalogModule> Modules = new List<CatalogModule>();
    }

    /// <summary>
    /// Runtime-fetched content catalog. HTTPS-trusted: the catalog itself is fetched over TLS from the
    /// project repo, and every asset it lists is downloaded and SHA-256-verified against the catalog
    /// before install (pets also pass <see cref="CompanionXmlValidator"/>; packs pass the fortune importer).
    /// Content added to the repo appears live with no new build. The bundled/offline content is the
    /// fallback; this only reveals what is not already present locally.
    /// </summary>
    internal static class RemoteCatalogClient
    {
        internal const string Owner = "bigfnj";
        internal const string Repository = "desktop-ai-companion";

        // Branch-pinned so content published to the repo is visible without shipping a new app build.
        internal const string CatalogUrl =
            "https://raw.githubusercontent.com/bigfnj/desktop-ai-companion/master/catalog.json";

        private const int MaximumCatalogBytes = 512 * 1024;
        private const int MaximumEntries = 512;
        internal const int MaximumModuleBytes = 100 * 1024 * 1024;   // generous but bounded module zip size

        public static async Task<RemoteCatalog> FetchAsync(CancellationToken cancellationToken)
        {
            Uri uri;
            string urlError;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    CatalogUrl, Owner, Repository, out uri, out urlError))
                throw new InvalidDataException("Catalog URL is invalid: " + urlError);

            byte[] bytes = await SecureDownload.DownloadBytesAsync(
                uri, MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
            return Parse(SecureDownload.DecodeUtf8(bytes));
        }

        // ---- short-lived shared copy ---------------------------------------------------------------------
        // Opening Preferences, then Modules, then Pets used to download catalog.json three times, and now
        // that both panes refresh themselves on open it would be worse. One in-memory copy, reused for a
        // short window, collapses that to one.
        //
        // Deliberately in memory and short-lived rather than a file cache. This only has to span a few
        // seconds of one user clicking through panes; persisting it would add a TTL, a corrupt-file path and
        // a stale-across-sessions failure mode to save a fetch nobody is waiting on.
        private static readonly object SharedLock = new object();
        private static RemoteCatalog sharedCatalog;
        private static DateTimeOffset sharedFetchedUtc = DateTimeOffset.MinValue;

        internal static readonly TimeSpan SharedLifetime = TimeSpan.FromSeconds(90);

        /// <summary>
        /// The catalog, reusing a copy fetched in the last <see cref="SharedLifetime"/> if there is one.
        ///
        /// Two panes opening at once may both fetch; that is accepted rather than locked around the await,
        /// because holding a lock across a network call to save one redundant request is the worse trade.
        /// </summary>
        public static async Task<RemoteCatalog> FetchSharedAsync(CancellationToken cancellationToken)
        {
            lock (SharedLock)
            {
                if (sharedCatalog != null &&
                    DateTimeOffset.UtcNow - sharedFetchedUtc < SharedLifetime)
                    return sharedCatalog;
            }
            RemoteCatalog fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
            lock (SharedLock)
            {
                sharedCatalog = fetched;
                sharedFetchedUtc = DateTimeOffset.UtcNow;
            }
            return fetched;
        }

        /// <summary>Drop the shared copy, so the next caller fetches. For a user-initiated "check now",
        /// where reusing a cached answer would make the button look broken.</summary>
        internal static void InvalidateShared()
        {
            lock (SharedLock) { sharedCatalog = null; sharedFetchedUtc = DateTimeOffset.MinValue; }
        }

        /// <summary>
        /// Read just the published APP version out of the catalog ("app": { "version": "1.9.8" }).
        ///
        /// Separate from <see cref="FetchAsync"/> on purpose: the launch update check wants one string and
        /// must not depend on the whole catalog parsing cleanly, so a pet entry gaining a field it does not
        /// understand cannot break it. Returns "" when the block is absent, which is what an older catalog
        /// looks like and reads as "nothing to report".
        /// </summary>
        public static async Task<string> FetchAppVersionAsync(CancellationToken cancellationToken)
        {
            Uri uri;
            string urlError;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    CatalogUrl, Owner, Repository, out uri, out urlError))
                return "";

            byte[] bytes = await SecureDownload.DownloadBytesAsync(
                uri, MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
            return ParseAppVersion(SecureDownload.DecodeUtf8(bytes));
        }

        /// <summary>The "app.version" string, or "" when absent/malformed. Pure, so the parse is testable
        /// without a network.</summary>
        internal static string ParseAppVersion(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "";
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement app;
                    if (!doc.RootElement.TryGetProperty("app", out app)) return "";
                    if (app.ValueKind != JsonValueKind.Object) return "";
                    JsonElement version;
                    if (!app.TryGetProperty("version", out version)) return "";
                    if (version.ValueKind != JsonValueKind.String) return "";
                    string text = version.GetString() ?? "";
                    return text.Length > 32 ? "" : text.Trim();
                }
            }
            catch { return ""; }
        }

        /// <summary>Download one catalog asset and verify it against the catalog's SHA-256.</summary>
        public static async Task<byte[]> DownloadVerifiedAsync(
            string url,
            string sha256,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            Uri uri;
            string urlError;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    url, Owner, Repository, out uri, out urlError))
                throw new InvalidDataException("Asset URL is invalid: " + urlError);

            byte[] bytes = await SecureDownload.DownloadBytesAsync(
                uri, maximumBytes, cancellationToken).ConfigureAwait(false);
            SecureDownload.RequireSha256(bytes, sha256);
            return bytes;
        }

        internal static RemoteCatalog Parse(string json)
        {
            var catalog = new RemoteCatalog();
            JsonNode root = JsonNode.Parse(json);
            if (root == null || JsonRead.IntOrNull(root["version"]) != 1)
                throw new InvalidDataException("Unsupported catalog version.");

            var pets = root["pets"] as JsonArray;
            var packs = root["packs"] as JsonArray;
            var modules = root["modules"] as JsonArray;
            if ((pets != null && pets.Count > MaximumEntries) ||
                (packs != null && packs.Count > MaximumEntries) ||
                (modules != null && modules.Count > MaximumEntries))
                throw new InvalidDataException("Catalog item count is invalid.");

            var petIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (pets != null)
                foreach (JsonNode token in pets)
                {
                    if (token == null)
                        throw new InvalidDataException("Catalog contains an invalid pet entry.");
                    var pet = new CatalogCompanion
                    {
                        Id = JsonRead.Str(token["id"]).Trim(),
                        Name = JsonRead.Str(token["name"]).Trim(),
                        Author = JsonRead.Str(token["author"]).Trim(),
                        Url = JsonRead.Str(token["url"]).Trim(),
                        Sha256 = JsonRead.Str(token["sha256"]).Trim().ToLowerInvariant(),
                        Bytes = JsonRead.IntOrNull(token["bytes"]) ?? 0
                    };
                    if (!SecureDownload.IsSafeId(pet.Id) || !petIds.Add(pet.Id) ||
                        string.IsNullOrWhiteSpace(pet.Name) || pet.Name.Length > 128 ||
                        pet.Author.Length > 128 ||
                        pet.Bytes < 1 || pet.Bytes > CompanionXmlValidator.MaximumXmlBytes ||
                        !IsSha256(pet.Sha256) ||
                        !IsPetAssetUrl(pet.Url, pet.Id))
                        throw new InvalidDataException("Catalog contains an invalid pet entry.");
                    catalog.Pets.Add(pet);
                }

            var packIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (packs != null)
                foreach (JsonNode token in packs)
                {
                    if (token == null)
                        throw new InvalidDataException("Catalog contains an invalid pack entry.");
                    var pack = new CatalogPack
                    {
                        Id = JsonRead.Str(token["id"]).Trim(),
                        Name = JsonRead.Str(token["name"]).Trim(),
                        Group = JsonRead.Str(token["group"]).Trim(),
                        Description = JsonRead.Str(token["desc"]).Trim(),
                        License = JsonRead.Str(token["license"]).Trim(),
                        Url = JsonRead.Str(token["url"]).Trim(),
                        Sha256 = JsonRead.Str(token["sha256"]).Trim().ToLowerInvariant(),
                        Bytes = JsonRead.IntOrNull(token["bytes"]) ?? 0,
                        Count = JsonRead.IntOrNull(token["count"]) ?? 0,
                        DataSchema = JsonRead.IntOrNull(token["dataSchema"]) ?? 0
                    };
                    if (!SecureDownload.IsSafeId(pack.Id) || !packIds.Add(pack.Id) ||
                        string.IsNullOrWhiteSpace(pack.Name) || pack.Name.Length > 128 ||
                        pack.Group.Length > 128 ||
                        pack.Description.Length > 1024 || pack.License.Length > 256 ||
                        (pack.DataSchema != 1 && pack.DataSchema != 2) ||
                        !IsSha256(pack.Sha256) ||
                        !IsPackAssetUrl(pack.Url, pack.Id))
                        throw new InvalidDataException("Catalog contains an invalid pack entry.");
                    // Reuse the same runtime-loadability bounds the embedded catalog enforces.
                    string packError;
                    if (!FortunePackLoadPolicy.TryValidatePackMetadata(
                            pack.Bytes, pack.Count, out packError))
                        throw new InvalidDataException(
                            "Catalog pack '" + pack.Id + "' " + packError + ".");
                    catalog.Packs.Add(pack);
                }

            // (see TryParsePermissions below for why an unknown flag name is dropped rather than fatal)
            var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (modules != null)
                foreach (JsonNode token in modules)
                {
                    if (token == null)
                        throw new InvalidDataException("Catalog contains an invalid module entry.");
                    ModulePermissions permissions;
                    bool permissionsValid = TryParsePermissions(
                        JsonRead.Str(token["permissions"]).Trim(), out permissions);
                    var module = new CatalogModule
                    {
                        Id = JsonRead.Str(token["id"]).Trim(),
                        Name = JsonRead.Str(token["name"]).Trim(),
                        Description = JsonRead.Str(token["desc"]).Trim(),
                        Version = JsonRead.Str(token["version"]).Trim(),
                        Url = JsonRead.Str(token["url"]).Trim(),
                        Sha256 = JsonRead.Str(token["sha256"]).Trim().ToLowerInvariant(),
                        Bytes = JsonRead.IntOrNull(token["bytes"]) ?? 0,
                        Permissions = permissions
                    };
                    if (!SecureDownload.IsSafeId(module.Id) || !moduleIds.Add(module.Id) ||
                        string.IsNullOrWhiteSpace(module.Name) || module.Name.Length > 128 ||
                        module.Description.Length > 1024 ||
                        string.IsNullOrWhiteSpace(module.Version) || module.Version.Length > 32 ||
                        !permissionsValid ||
                        module.Bytes < 1 || module.Bytes > MaximumModuleBytes ||
                        !IsSha256(module.Sha256) ||
                        !IsModuleAssetUrl(module.Url, module.Id))
                        throw new InvalidDataException("Catalog contains an invalid module entry.");
                    catalog.Modules.Add(module);
                }

            return catalog;
        }

        /// <summary>
        /// Parse a comma-separated permission list, DROPPING names this build does not know instead of
        /// rejecting the entry.
        ///
        /// This used to be a single Enum.TryParse over the whole string, and a miss failed the entire
        /// catalog -- not the entry -- because every catalog feature shares one fetch. So the first release
        /// to add a permission name silently took the Modules pane, the monthly update check, fortune-pack
        /// browsing AND the Pets gallery away from every older host. It had already happened once, unnoticed:
        /// Pets shipped in 1.4.4, so a v1.4.2 host cannot parse today's catalog at all.
        ///
        /// An unrecognised flag means "a capability this build does not know about", which is a normal
        /// consequence of a newer host existing -- not corruption. The module's own MinHostVersion is what
        /// correctly refuses it. An empty or malformed list is still rejected.
        /// </summary>
        internal static bool TryParsePermissions(string text, out ModulePermissions permissions)
        {
            permissions = ModulePermissions.None;
            if (text == null) return false;
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return false;

            foreach (string part in trimmed.Split(','))
            {
                string name = part.Trim();
                if (name.Length == 0) return false;   // "Speech,,Storage" is malformed, not forward-compatible
                ModulePermissions one;
                if (Enum.TryParse(name, true, out one) && Enum.IsDefined(typeof(ModulePermissions), one))
                    permissions |= one;
                // else: a flag from a newer host. Ignore it and keep the entry.
            }
            return true;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool IsPetAssetUrl(string url, string id)
        {
            Uri uri;
            string error;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    url, Owner, Repository, out uri, out error))
                return false;
            string[] p = uri.AbsolutePath.Trim('/').Split('/');
            // owner / repo / <ref> / Pets / <id> / animations.xml
            return p.Length >= 6 &&
                string.Equals(p[p.Length - 3], "Companions", StringComparison.Ordinal) &&
                string.Equals(p[p.Length - 2], id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p[p.Length - 1], "animations.xml", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPackAssetUrl(string url, string id)
        {
            Uri uri;
            string error;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    url, Owner, Repository, out uri, out error))
                return false;
            string[] p = uri.AbsolutePath.Trim('/').Split('/');
            // owner / repo / <ref> / packs / <id>.txt
            return p.Length >= 5 &&
                string.Equals(p[p.Length - 2], "packs", StringComparison.Ordinal) &&
                string.Equals(p[p.Length - 1], id + ".txt", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModuleAssetUrl(string url, string id)
        {
            Uri uri;
            string error;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    url, Owner, Repository, out uri, out error))
                return false;
            string[] p = uri.AbsolutePath.Trim('/').Split('/');
            // owner / repo / <ref> / modules-dist / <id>.zip
            return p.Length >= 5 &&
                string.Equals(p[p.Length - 2], "modules-dist", StringComparison.Ordinal) &&
                string.Equals(p[p.Length - 1], id + ".zip", StringComparison.OrdinalIgnoreCase);
        }

        // ---- diagnostics ----------------------------------------------------

        private const string PetUrlBase =
            "https://raw.githubusercontent.com/bigfnj/desktop-ai-companion/master/Companions/";
        private const string PackUrlBase =
            "https://raw.githubusercontent.com/bigfnj/desktop-ai-companion/master/packs/";
        private static readonly string SampleSha =
            new string('a', 64);
        private const string ModuleUrlBase =
            "https://raw.githubusercontent.com/bigfnj/desktop-ai-companion/master/modules-dist/";

        internal static bool SelfTest()
        {
            var report = new StringBuilder();
            bool ok = true;

            string validJson =
                "{ \"version\": 1, \"pets\": [ { \"id\": \"fox\", \"name\": \"Fox\", " +
                "\"author\": \"Michelle\", \"url\": \"" + PetUrlBase +
                "fox/animations.xml\", \"sha256\": \"" + SampleSha + "\", \"bytes\": 33556 } ], " +
                "\"packs\": [ { \"id\": \"tech\", \"name\": \"Tech\", \"desc\": \"quips\", " +
                "\"license\": \"LicenseRef-DesktopAICompanion-Community\", \"url\": \"" + PackUrlBase +
                "tech.txt\", \"sha256\": \"" + SampleSha + "\", \"bytes\": 308767, " +
                "\"count\": 620, \"dataSchema\": 2 } ], " +
                "\"modules\": [ { \"id\": \"fortunes\", \"name\": \"Fortunes\", " +
                "\"desc\": \"Offline smart fortunes\", \"version\": \"1.2.1\", \"url\": \"" +
                ModuleUrlBase + "fortunes.zip\", \"sha256\": \"" + SampleSha +
                "\", \"bytes\": 2048, \"permissions\": \"Speech, Storage\" } ] }";
            // The catalog may list up to MaximumEntries packs, and a user can install all of them, so the
            // runtime file cap must not be lower -- when it was (512 listed vs 128 loadable), the overflow
            // was dropped silently on load and those packs just never spoke.
            // (Read into locals: comparing two consts folds to a constant and trips "unreachable code".)
            int loadableFileCap = FortunePackLoadPolicy.MaximumFiles;
            int catalogEntryCap = MaximumEntries;
            if (loadableFileCap < catalogEntryCap)
            {
                ok = false;
                report.AppendLine("CATALOG FAIL pack file cap (" + loadableFileCap +
                    ") is below the catalog entry cap (" + catalogEntryCap + ")");
            }

            try
            {
                RemoteCatalog catalog = Parse(validJson);
                if (catalog.Pets.Count != 1 || catalog.Packs.Count != 1 || catalog.Modules.Count != 1 ||
                    catalog.Modules[0].Permissions != (ModulePermissions.Speech | ModulePermissions.Storage))
                {
                    ok = false;
                    report.AppendLine("CATALOG FAIL valid catalog produced wrong counts");
                }
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine("CATALOG FAIL valid catalog rejected: " + ex.Message);
            }

            // Each of these must be rejected.
            var rejects = new[]
            {
                "{ \"version\": 2, \"pets\": [], \"packs\": [] }",                       // bad version
                "{ \"version\": 1, \"pets\": [ { \"id\": \"fox\", \"name\": \"Fox\", " +
                    "\"url\": \"" + PetUrlBase + "fox/animations.xml\", " +
                    "\"sha256\": \"notahash\", \"bytes\": 33556 } ], \"packs\": [] }",   // bad sha
                "{ \"version\": 1, \"pets\": [ { \"id\": \"fox\", \"name\": \"Fox\", " +
                    "\"url\": \"https://evil.example.com/x/animations.xml\", " +
                    "\"sha256\": \"" + SampleSha + "\", \"bytes\": 10 } ], \"packs\": [] }",   // bad host
                "{ \"version\": 1, \"pets\": [ { \"id\": \"fox\", \"name\": \"Fox\", " +
                    "\"url\": \"" + PetUrlBase + "notfox/animations.xml\", \"sha256\": \"" +
                    SampleSha + "\", \"bytes\": 10 } ], \"packs\": [] }",                // id/path mismatch
                "{ \"version\": 1, \"pets\": [], \"packs\": [ { \"id\": \"../etc\", " +
                    "\"name\": \"x\", \"url\": \"" + PackUrlBase + "x.txt\", \"sha256\": \"" +
                    SampleSha + "\", \"bytes\": 10, \"count\": 1, \"dataSchema\": 2 } ] }", // unsafe id
                // An EMPTY permission list is still malformed. An unrecognised NAME is not -- see below.
                "{ \"version\": 1, \"pets\": [], \"packs\": [], \"modules\": [ { \"id\": \"x\", " +
                    "\"name\": \"X\", \"version\": \"1.0\", \"url\": \"" + ModuleUrlBase +
                    "x.zip\", \"sha256\": \"" + SampleSha +
                    "\", \"bytes\": 10, \"permissions\": \"\" } ] }",                 // empty permissions
                "{ \"version\": 1, \"pets\": [], \"packs\": [], \"modules\": [ { \"id\": \"x\", " +
                    "\"name\": \"X\", \"version\": \"1.0\", \"url\": \"" + ModuleUrlBase +
                    "x.zip\", \"sha256\": \"" + SampleSha +
                    "\", \"bytes\": 10, \"permissions\": \"Speech,,Storage\" } ] }"   // malformed list
            };
            for (int i = 0; i < rejects.Length; i++)
            {
                bool rejected = false;
                try { Parse(rejects[i]); }
                catch (InvalidDataException) { rejected = true; }
                catch (System.Text.Json.JsonException) { rejected = true; }
                if (!rejected)
                {
                    ok = false;
                    report.AppendLine("CATALOG FAIL reject-case " + i + " was accepted");
                }
            }

            // A permission name this build does not know must NOT fail the catalog. It used to, and because
            // every catalog feature shares one fetch, the first release to add a flag silently took the
            // Modules pane, the monthly update check, pack browsing and the Pets gallery away from every
            // older host. The unknown flag is dropped; the known ones survive; MinHostVersion is what
            // actually refuses the module.
            try
            {
                string forwardCompatible =
                    "{ \"version\": 1, \"pets\": [], \"packs\": [], \"modules\": [ { \"id\": \"x\", " +
                    "\"name\": \"X\", \"version\": \"1.0\", \"url\": \"" + ModuleUrlBase +
                    "x.zip\", \"sha256\": \"" + SampleSha +
                    "\", \"bytes\": 10, \"permissions\": \"Speech, FromAFutureHost, Storage\" } ] }";
                RemoteCatalog forward = Parse(forwardCompatible);
                if (forward.Modules.Count != 1 ||
                    forward.Modules[0].Permissions != (ModulePermissions.Speech | ModulePermissions.Storage))
                {
                    ok = false;
                    report.AppendLine("CATALOG FAIL unknown permission name was not dropped cleanly");
                }
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine("CATALOG FAIL an unknown permission name broke the whole catalog: " + ex.Message);
            }

            try
            {
                string path = Path.Combine(Path.GetTempPath(), "dp-catalog-selftest.txt");
                report.AppendLine("catalog_parse=" + (ok ? "PASS" : "FAIL"));
                File.WriteAllText(path, report.ToString());
            }
            catch { }
            return ok;
        }

        /// <summary>
        /// Live smoke test (network): fetch the real catalog, then download-and-verify the first pet
        /// and first pack through the actual app code path. Not wired into the offline test suites.
        /// </summary>
        internal static bool OnlineSelfTest()
        {
            var report = new StringBuilder();
            bool ok = true;
            try
            {
                RemoteCatalog catalog = FetchAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
                report.AppendLine("fetched pets=" + catalog.Pets.Count +
                    " packs=" + catalog.Packs.Count);

                if (catalog.Pets.Count > 0)
                {
                    CatalogCompanion pet = catalog.Pets[0];
                    byte[] bytes = DownloadVerifiedAsync(
                        pet.Url, pet.Sha256, CompanionXmlValidator.MaximumXmlBytes,
                        CancellationToken.None).GetAwaiter().GetResult();
                    XmlData.RootNode root;
                    string parseError;
                    bool valid = CompanionXmlValidator.TryParse(
                        SecureDownload.DecodeUtf8(bytes), out root, out parseError);
                    ok = ok && valid;
                    report.AppendLine("pet " + pet.Id + " verified bytes=" + bytes.Length +
                        " valid=" + valid + (valid ? "" : " err=" + parseError));
                }

                if (catalog.Packs.Count > 0)
                {
                    CatalogPack pack = catalog.Packs[0];
                    byte[] bytes = DownloadVerifiedAsync(
                        pack.Url, pack.Sha256, FortunePackLoadPolicy.MaximumFileBytes,
                        CancellationToken.None).GetAwaiter().GetResult();
                    report.AppendLine("pack " + pack.Id + " verified bytes=" + bytes.Length);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine("ONLINE EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "dp-online-selftest.txt"),
                    "online=" + (ok ? "PASS" : "FAIL") + "\r\n" + report);
            }
            catch { }
            return ok;
        }
    }
}
