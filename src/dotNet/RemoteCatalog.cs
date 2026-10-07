using System;
using System.Collections.Generic;
using System.Globalization;
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
        // What the pet CONTAINS, so a not-yet-downloaded card can say more than its size. Counted by
        // New-ContentCatalog with the same two patterns CompanionsPaneControl.GetStats uses on an installed
        // pet, so the two cards never disagree about the same companion. Zero means an older catalog that
        // predates these fields, and the card then shows the size line alone as it always did.
        public int Animations;
        public int Sounds;
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

        /// <summary>The host version this module refuses to run below, straight from its own
        /// <c>ModuleInfo.MinHostVersion</c>. Empty when the catalog predates the field, which is a
        /// supported state: an empty requirement is satisfied by every host, so an old catalog behaves
        /// exactly as it did before.</summary>
        public string MinHostVersion;
    }

    internal sealed class RemoteCatalog
    {
        public readonly List<CatalogCompanion> Pets = new List<CatalogCompanion>();
        public readonly List<CatalogPack> Packs = new List<CatalogPack>();
        public readonly List<CatalogModule> Modules = new List<CatalogModule>();

        /// <summary>
        /// The entries this build REFUSED, each with the rule it broke, in catalog order. A refused entry is in
        /// none of the three lists above, so it is never offered, and the rest of the catalog stays usable
        /// (feature/catalog-insight; the decision is under that heading in docs/DESIGN-REGISTER.md).
        /// </summary>
        public readonly List<CatalogRejection> Rejected = new List<CatalogRejection>();

        /// <summary>When this catalog was parsed, local time: the "read at" a pane shows beside a refusal.</summary>
        public readonly DateTime ReadAt = DateTime.Now;

        /// <summary>
        /// How many entries of one kind (<see cref="CatalogRejection.Module"/> and its siblings) were refused. A
        /// weekly check that looks at a kind with a refused entry did not see the whole answer, so it must not
        /// stamp itself done: a module whose entry was refused would otherwise not be offered for a week after the
        /// catalog was fixed (StartUp's pet and module checks ask this before they stamp).
        /// </summary>
        internal int RefusedCount(string kind)
        {
            return RefusedOf(kind).Count;
        }

        /// <summary>The refused entries of one kind, in catalog order: what a pane about that kind names.</summary>
        internal List<CatalogRejection> RefusedOf(string kind)
        {
            var found = new List<CatalogRejection>();
            foreach (CatalogRejection refused in Rejected)
                if (refused != null && string.Equals(refused.Kind, kind, StringComparison.Ordinal)) found.Add(refused);
            return found;
        }
    }

    /// <summary>
    /// One catalog entry this build refused and the rule it broke, in words a person can act on:
    /// <c>module "agentflow": description is 1049 characters, the limit is 1024</c>. The entry is named by its
    /// id when the id is usable, and by its position in its list when the id itself is the problem. Everything
    /// in <see cref="Rule"/> that came from the catalog has been through <see cref="CatalogText.Echo"/>, because
    /// the catalog is remote data and these words are shown in the app and written to the log.
    /// </summary>
    internal sealed class CatalogRejection
    {
        internal const string Companion = "companion";
        internal const string Pack = "pack";
        internal const string Module = "module";

        /// <summary><see cref="Companion"/>, <see cref="Pack"/> or <see cref="Module"/>.</summary>
        public string Kind;
        /// <summary>Zero-based position in its list; -1 when the whole list was refused.</summary>
        public int Index;
        /// <summary>The entry's id when that id passed its own rules, else "" and the position names it.</summary>
        public string Id;
        /// <summary>The rule broken, as a clause: "description is 1049 characters, the limit is 1024".</summary>
        public string Rule;
        /// <summary>The entry's name and version as the catalog gave them (sanitised, bounded), "" when it gave
        /// none: what the Modules pane's problem panel names beside the id ("AgentFlow 1.5.0"), and the version its
        /// row says is not offered. Never used to decide anything.</summary>
        public string Name = "";
        public string Version = "";

        public override string ToString()
        {
            if (Index < 0) return Kind + "s: " + Rule;
            return string.IsNullOrEmpty(Id)
                ? Kind + " entry " + (Index + 1).ToString(CultureInfo.InvariantCulture) + ": " + Rule
                : Kind + " \"" + Id + "\": " + Rule;
        }
    }

    /// <summary>
    /// The catalog was DOWNLOADED and this build refused all of it: not JSON, not UTF-8, a schema it does not
    /// read, past a bound that applies to the whole file. Kept apart from every other fetch failure so a pane
    /// can say "reached but could not be read, the catalog was published wrong" instead of "couldn't reach the
    /// catalog" (feature/catalog-insight). A type of its own (InvalidDataException, what Parse used to throw, is
    /// sealed); every caller of the fetch catches Exception, so none of them changes.
    /// </summary>
    internal sealed class CatalogRejectedException : Exception
    {
        internal CatalogRejectedException(string reason) : base(reason) { }
        internal CatalogRejectedException(string reason, Exception inner) : base(reason, inner) { }
    }

    /// <summary>
    /// What the app says about the catalog: a failed fetch in its two cases' own words, the entries a read
    /// refused, the log line, and the sanitiser every echo of catalog content goes through. One place, so the
    /// Modules pane, the Companions pane and the log cannot word the same failure three ways.
    /// </summary>
    internal static class CatalogText
    {
        /// <summary>
        /// Untrusted text made safe to show: at most <paramref name="max"/> characters (then an ellipsis), with
        /// control, format (bidi overrides, zero-width), separator, private-use, unassigned and lone surrogate
        /// characters shown as '?', and a double quote as an apostrophe so an echoed value cannot close the
        /// quotes the message put round it.
        /// </summary>
        internal static string Echo(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var text = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (text.Length >= max) { text.Append('…'); break; }
                char c = value[i];
                UnicodeCategory category = char.GetUnicodeCategory(c);
                if (char.IsControl(c) || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator ||
                    category == UnicodeCategory.PrivateUse || category == UnicodeCategory.OtherNotAssigned ||
                    category == UnicodeCategory.Surrogate)
                    text.Append('?');
                else if (c == '"') text.Append('\'');
                else text.Append(c);
            }
            return text.ToString();
        }

        /// <summary>"name is 129 characters, the limit is 128".</summary>
        internal static string TooLong(string field, int length, int limit)
        {
            return field + " is " + Number(length) + " characters, the limit is " + Number(limit);
        }

        /// <summary>"bytes is 0, it must be 1 to 104857600" ("is missing or 0" when 0 is below the range).</summary>
        internal static string OutOfRange(string field, int value, int minimum, int maximum)
        {
            return field + (value == 0 && minimum > 0 ? " is missing or 0" : " is " + Number(value)) +
                   ", it must be " + Number(minimum) + " to " + Number(maximum);
        }

        internal static string Number(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>True when the catalog was reached and this app refused it, as against never reached.</summary>
        internal static bool IsRefusal(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
                if (e is CatalogRejectedException) return true;
            return false;
        }

        /// <summary>
        /// The failure's own words, readable and bounded. A message that only points at its inner exception
        /// ("The SSL connection could not be established, see inner exception.", which is how a TLS failure
        /// reads) takes the inner one's words, so the pane says what was wrong with the certificate rather than
        /// where to look for it. Sanitised, because a server's reason phrase is in an HTTP status message.
        /// </summary>
        internal static string Reason(Exception ex)
        {
            if (ex == null) return "";
            string text = (ex.Message ?? "").Trim();
            Exception inner = ex.InnerException;
            for (int depth = 0; inner != null && depth < 3 &&
                 text.IndexOf("inner exception", StringComparison.OrdinalIgnoreCase) >= 0; depth++)
            {
                int pointer = text.IndexOf(", see inner exception", StringComparison.OrdinalIgnoreCase);
                string head = pointer >= 0 ? text.Substring(0, pointer) : text;
                text = head.TrimEnd('.', ' ') + ": " + (inner.Message ?? "").Trim();
                inner = inner.InnerException;
            }
            return Echo(text, 240).TrimEnd('.', ' ');
        }

        /// <summary>The diagnostic-log form: which case, the reason, and the exception type that carried it.</summary>
        internal static string ForLog(Exception ex)
        {
            return (IsRefusal(ex) ? "catalog refused: " : "catalog unreachable: ") + Reason(ex) +
                   " (" + (ex == null ? "no exception" : ex.GetType().Name) + ")";
        }

        /// <summary>
        /// The line for a fetch that failed, in the two cases' own words. It used to read "Couldn't reach the
        /// catalog" for both, including the catalog that was reached and REJECTED on 2026-10-06 (BUG-014), which
        /// sent every reader to their own connection for a fault in the published file.
        /// </summary>
        internal static string FetchFailed(Exception ex, DateTime when, string retryButton)
        {
            string at = when.ToString("t", CultureInfo.CurrentCulture);
            if (IsRefusal(ex))
                return "✗ The catalog was reached at " + at + " but could not be read: " + Reason(ex) + ". " +
                       "This is a fault in the published catalog, not in your install; try “" + retryButton +
                       "” again later.";
            // THE SERVER ANSWERED with an error status (a 429 rate limit, a 5xx, a 404): the connection works,
            // so "check your connection" would be the wrong advice. HttpRequestException carries the status
            // whenever EnsureSuccessStatusCode raised it.
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                var http = e as System.Net.Http.HttpRequestException;
                if (http != null && http.StatusCode.HasValue)
                    return "✗ Couldn't get the catalog at " + at + ": " + Reason(ex) + ". The server answered, so " +
                           "your connection is working; press “" + retryButton + "” again in a few minutes.";
            }
            return "✗ Couldn't reach the catalog at " + at + ": " + Reason(ex) + ". Check your connection, then " +
                   "press “" + retryButton + "” to try again.";
        }

        /// <summary>
        /// The note for a catalog that was read with entries refused: how many, which (up to
        /// <paramref name="shown"/>, the log carries all of them), and that the fault is the catalog's.
        /// "" when nothing was refused.
        /// </summary>
        internal static string Refused(IList<CatalogRejection> refused, DateTime when, string retryButton, int shown)
        {
            if (refused == null || refused.Count == 0) return "";
            int count = refused.Count;
            var text = new StringBuilder();
            text.Append("⚠ The catalog was read at ").Append(when.ToString("t", CultureInfo.CurrentCulture))
                .Append(", but ").Append(count == 1 ? "1 entry in it was" : Number(count) + " entries in it were")
                .Append(" published wrong and ").Append(count == 1 ? "is" : "are").Append(" not offered: ");
            int listed = Math.Min(count, Math.Max(1, shown));
            for (int i = 0; i < listed; i++)
            {
                if (i > 0) text.Append("; ");
                text.Append(refused[i]);
            }
            if (count > listed)
                text.Append("; and ").Append(Number(count - listed)).Append(" more, listed in the diagnostic log");
            text.Append(". This is a fault in the published catalog, not in your install, and the rest of it is offered as " +
                        "usual; press “").Append(retryButton).Append("” again later to pick ")
                .Append(count == 1 ? "it" : "them").Append(" up once the catalog is fixed.");
            return text.ToString();
        }

        /// <summary>The log line for a read with entries refused: every one of them, on one line.</summary>
        internal static string RefusedForLog(RemoteCatalog catalog)
        {
            var parts = new List<string>();
            if (catalog != null)
                foreach (CatalogRejection refused in catalog.Rejected) parts.Add(refused.ToString());
            return "catalog read with " + Number(parts.Count) + (parts.Count == 1 ? " entry" : " entries") +
                   " refused and not offered: " + string.Join("; ", parts.ToArray());
        }

        // ---- the Modules pane's problem panel (the owner's mockup M2, 2026-10-06) ----

        /// <summary>
        /// The panel for a fetch that failed: a title naming the case, then What failed (or Rule, for a catalog
        /// that was reached and refused), When, and What this means, whose last sentence says whose fault it is.
        /// <paramref name="occasion"/> finishes the When row: "when this pane opened".
        /// </summary>
        internal static CatalogProblem ProblemForFailure(Exception ex, DateTime when, string occasion)
        {
            var problem = new CatalogProblem();
            string whenText = WhenText(when, occasion);
            if (IsRefusal(ex))
            {
                problem.Title = "The module catalog was refused, so nothing can be installed or updated right now";
                problem.Add("Rule", Reason(ex));
                problem.Add("When", whenText);
                problem.Add("What this means", "Published wrong, not your install. Nothing on this PC needs fixing; " +
                                               "the next catalog publish clears it.");
                return problem;
            }
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                var http = e as System.Net.Http.HttpRequestException;
                if (http != null && http.StatusCode.HasValue)
                {
                    problem.Title = "Couldn't get the catalog";
                    problem.Add("What failed", Reason(ex));
                    problem.Add("When", whenText);
                    problem.Add("What this means", "GitHub answered with an error, so your connection works; not the " +
                                                   "catalog and not your install. Nothing was changed; updates are only " +
                                                   "unknown until it answers.");
                    return problem;
                }
            }
            problem.Title = "Couldn't reach the catalog";
            problem.Add("What failed", Reason(ex));
            problem.Add("When", whenText);
            problem.Add("What this means", "Your connection or GitHub, not the catalog and not your install. Nothing " +
                                           "was changed; updates are only unknown until it answers.");
            return problem;
        }

        /// <summary>
        /// The panel for a catalog that was read with entries refused (amber: the rest works), or null when
        /// <paramref name="refused"/> is empty. Each entry is named with its name and version when the catalog gave
        /// them, beside its rule; three are listed and the rest counted. <paramref name="installedVersion"/>
        /// answers the version of an installed module by id (null when not installed), so a single refused module
        /// entry can say what keeps running.
        /// </summary>
        internal static CatalogProblem ProblemForRefusals(IList<CatalogRejection> refused, DateTime when, string occasion,
            Func<string, string> installedVersion)
        {
            if (refused == null || refused.Count == 0) return null;
            var problem = new CatalogProblem { Warning = true };
            int count = refused.Count;
            problem.Title = count == 1
                ? "1 catalog entry was skipped; everything else works"
                : Number(count) + " catalog entries were skipped; everything else works";
            int listed = Math.Min(count, 3);
            for (int i = 0; i < listed; i++)
            {
                problem.Add("Entry", EntryText(refused[i]));
                problem.Add("Rule", refused[i].Rule);
            }
            if (count > listed)
                problem.Add("And", Number(count - listed) + " more, listed in the diagnostic log");
            problem.Add("When", WhenText(when, occasion));
            string meaning = "Published wrong, not your install. ";
            CatalogRejection only = count == 1 ? refused[0] : null;
            string running = only != null && !string.IsNullOrEmpty(only.Id) && installedVersion != null
                ? installedVersion(only.Id) : null;
            if (!string.IsNullOrEmpty(running))
                meaning += (only.Name.Length > 0 ? only.Name : only.Id) + " keeps running v" + running +
                           "; its update appears once the entry is fixed.";
            else if (only != null)
                meaning += "It is not offered until the entry is fixed; everything else in the catalog is.";
            else
                meaning += "These are not offered until their entries are fixed; everything else in the catalog is.";
            problem.Add("What this means", meaning);
            return problem;
        }

        /// <summary>module “agentflow” (AgentFlow 1.5.0); module entry 2; the modules list.</summary>
        internal static string EntryText(CatalogRejection refused)
        {
            if (refused == null) return "";
            if (refused.Index < 0) return "the " + refused.Kind + "s list";
            string detail = (refused.Name + (refused.Version.Length > 0 ? " " + refused.Version : "")).Trim();
            string named = string.IsNullOrEmpty(refused.Id)
                ? refused.Kind + " entry " + Number(refused.Index + 1)
                : refused.Kind + " “" + refused.Id + "”";
            return detail.Length > 0 ? named + " (" + detail + ")" : named;
        }

        private static string WhenText(DateTime when, string occasion)
        {
            return "Today at " + when.ToString("t", CultureInfo.CurrentCulture) +
                   (string.IsNullOrEmpty(occasion) ? "." : ", " + occasion + ".");
        }
    }

    /// <summary>
    /// What the Modules pane's problem panel shows (the owner's mockup M2): a title naming the case and labelled
    /// rows. Pure data, built by <see cref="CatalogText"/> so its words are tested without a window, and written
    /// out the same way for Copy details and the log.
    /// </summary>
    internal sealed class CatalogProblem
    {
        /// <summary>Amber (entries skipped, the rest works) rather than red (nothing could be read).</summary>
        internal bool Warning;
        internal string Title = "";
        internal readonly List<KeyValuePair<string, string>> Rows = new List<KeyValuePair<string, string>>();

        internal void Add(string label, string value)
        {
            Rows.Add(new KeyValuePair<string, string>(label, value ?? ""));
        }

        /// <summary>The text Copy details puts on the clipboard: the title, then one "Label: value" line per row.</summary>
        internal string ForCopy()
        {
            var text = new StringBuilder(Title);
            foreach (KeyValuePair<string, string> row in Rows)
                text.Append(Environment.NewLine).Append(row.Key).Append(": ").Append(row.Value);
            return text.ToString();
        }

        /// <summary>The same words on one line, for the diagnostic log.</summary>
        internal string ForLog()
        {
            return ForCopy().Replace(Environment.NewLine, " | ");
        }
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
        // Ceiling for a catalog's animation/sound counts. The largest shipped companion carries 1133
        // animations, so this is roughly 9x the real maximum: generous enough never to refuse a real pet,
        // tight enough that a malformed entry is refused rather than rendered.
        private const int MaximumCountedItems = 10000;
        internal const int MaximumModuleBytes = 100 * 1024 * 1024;   // generous but bounded module zip size

        public static async Task<RemoteCatalog> FetchAsync(CancellationToken cancellationToken)
        {
            byte[] bytes = await FetchBytesAsync(cancellationToken).ConfigureAwait(false);
            return ParseBytes(bytes);
        }

        /// <summary>One download of catalog.json, URL-validated, bounded, uncached.</summary>
        private static async Task<byte[]> FetchBytesAsync(CancellationToken cancellationToken)
        {
            Uri uri;
            string urlError;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    CatalogUrl, Owner, Repository, out uri, out urlError))
                throw new InvalidDataException("Catalog URL is invalid: " + urlError);

            try
            {
                return await SecureDownload.DownloadBytesAsync(
                    uri, MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
            }
            // The size cap is the one InvalidDataException the download raises, and it means the catalog was
            // REACHED and is bigger than this app takes: a refusal in the catalog's words, not "couldn't reach".
            catch (InvalidDataException ex)
            {
                throw new CatalogRejectedException(
                    "catalog.json is larger than the " + CatalogText.Number(MaximumCatalogBytes) +
                    " bytes this app downloads", ex);
            }
        }

        // ---- short-lived shared copy ---------------------------------------------------------------------
        // Opening Preferences, then Modules, then Pets used to download catalog.json three times, and now
        // that both panes refresh themselves on open it would be worse. One in-memory copy, reused for a
        // short window, collapses that to one.
        //
        // Deliberately in memory and short-lived rather than a file cache. This only has to span a few
        // seconds of one user clicking through panes; persisting it would add a TTL, a corrupt-file path and
        // a stale-across-sessions failure mode to save a fetch nobody is waiting on.
        //
        // The copy is the RAW BYTES, with the parse hung off them (F286). The launch's three due checks --
        // the app version, the pet freshness, the module scan -- fire within seconds of each other and each
        // downloaded the file for itself, and the app-version check must keep parsing ONLY its block (a pet
        // entry gaining a field must not break it), so it is the bytes the three can share, not the parse.
        private static readonly object SharedLock = new object();
        private static byte[] sharedBytes;
        private static RemoteCatalog sharedCatalog;
        private static DateTimeOffset sharedFetchedUtc = DateTimeOffset.MinValue;

        internal static readonly TimeSpan SharedLifetime = TimeSpan.FromSeconds(90);

        /// <summary>The raw catalog.json, reusing bytes fetched in the last <see cref="SharedLifetime"/>.
        /// Two callers arriving at once may both fetch; that is accepted rather than locked around the
        /// await, because holding a lock across a network call to save one redundant request is the
        /// worse trade.</summary>
        private static async Task<byte[]> FetchSharedBytesAsync(CancellationToken cancellationToken)
        {
            lock (SharedLock)
            {
                if (sharedBytes != null &&
                    DateTimeOffset.UtcNow - sharedFetchedUtc < SharedLifetime)
                    return sharedBytes;
            }
            byte[] fetched = await FetchBytesAsync(cancellationToken).ConfigureAwait(false);
            lock (SharedLock)
            {
                sharedBytes = fetched;
                sharedCatalog = null;   // parsed on first demand, from these bytes
                sharedFetchedUtc = DateTimeOffset.UtcNow;
            }
            return fetched;
        }

        /// <summary>
        /// The catalog, reusing a copy fetched in the last <see cref="SharedLifetime"/> if there is one.
        /// On a warm cache this returns from inside the lock with no await executed, and the panes are
        /// written for exactly that (they fetch from Loaded, never from a constructor).
        /// </summary>
        public static async Task<RemoteCatalog> FetchSharedAsync(CancellationToken cancellationToken)
        {
            lock (SharedLock)
            {
                if (sharedCatalog != null &&
                    DateTimeOffset.UtcNow - sharedFetchedUtc < SharedLifetime)
                    return sharedCatalog;
            }
            byte[] bytes = await FetchSharedBytesAsync(cancellationToken).ConfigureAwait(false);
            // A parse that THROWS is never kept: sharedCatalog stays null, so each caller in the window re-reads
            // the same bytes and gets the same refusal, and the next fetch after the window (or any "check now")
            // reads the catalog afresh. Nothing here stamps a check as done; the callers stamp, on success only.
            RemoteCatalog parsed = ParseBytes(bytes);
            lock (SharedLock)
            {
                // Only if these are still the bytes on hand: a refresh that landed meanwhile owns the slot.
                if (ReferenceEquals(sharedBytes, bytes)) sharedCatalog = parsed;
            }
            // Once per read, not once per pane: every entry refused, with its rule, so the log says what the
            // panes summarise (feature/catalog-insight).
            if (parsed.Rejected.Count > 0)
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.warning, "[catalog] " + CatalogText.RefusedForLog(parsed));
            return parsed;
        }

        /// <summary>
        /// A user-initiated "check now": drop the shared copy AND refill it with what the check finds, so the
        /// other pane and the launch checks reuse the answer instead of fetching again (F286). Invalidating
        /// alone made the button honest and the next pane redundant.
        /// </summary>
        public static async Task<RemoteCatalog> RefreshSharedAsync(CancellationToken cancellationToken)
        {
            InvalidateShared();
            return await FetchSharedAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Drop the shared copy, so the next caller fetches. For a user-initiated "check now",
        /// where reusing a cached answer would make the button look broken.</summary>
        internal static void InvalidateShared()
        {
            lock (SharedLock) { sharedBytes = null; sharedCatalog = null; sharedFetchedUtc = DateTimeOffset.MinValue; }
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

            // The SHARED bytes (F286), parsed here for the one block this check understands: the other two
            // launch checks read the same download, and neither this nor they fetch twice within the window.
            //
            // REFUSING, not "": an unreadable catalog is a failed check, and AppUpdateCheck.MaybeCheckAsync stamps
            // whatever this returns as a completed weekly answer. Returning "" for a catalog published broken told
            // every launch in that window "nothing newer" for a week. An ABSENT app block is still "" and still
            // stamps: that is what an older catalog looks like, and it is an answer (feature/catalog-insight).
            byte[] bytes = await FetchSharedBytesAsync(cancellationToken).ConfigureAwait(false);
            return ParseAppVersion(SecureDownload.DecodeUtf8(bytes), true);
        }

        /// <summary>The "app.version" string, or "" when absent/malformed. Pure, so the parse is testable
        /// without a network.</summary>
        internal static string ParseAppVersion(string json)
        {
            return ParseAppVersion(json, false);
        }

        /// <summary>
        /// As <see cref="ParseAppVersion(string)"/>; with <paramref name="refuseUnreadable"/> a catalog that
        /// cannot be read -- not JSON, not an object, an app block or version of the wrong shape -- is a
        /// <see cref="CatalogRejectedException"/> naming why, instead of "". An absent app block or version is
        /// "" either way. The launch check and --catalog-parse-file pass true; the lenient form is what the
        /// footer's tests have always pinned.
        /// </summary>
        internal static string ParseAppVersion(string json, bool refuseUnreadable)
        {
            string reason = null;
            string found = "";
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json ?? ""))
                {
                    JsonElement app;
                    JsonElement version;
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        reason = "catalog.json is not a JSON object";
                    else if (!doc.RootElement.TryGetProperty("app", out app)) { }
                    else if (app.ValueKind != JsonValueKind.Object)
                        reason = "the app block is not a JSON object";
                    else if (!app.TryGetProperty("version", out version)) { }
                    else if (version.ValueKind != JsonValueKind.String)
                        reason = "app.version is not a string";
                    else
                    {
                        string text = version.GetString() ?? "";
                        if (text.Length > 32) reason = CatalogText.TooLong("app.version", text.Length, 32);
                        else found = text.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                reason = (ex is JsonException ? "catalog.json is not valid JSON (" : "catalog.json could not be read (") +
                         CatalogText.Echo(ex.Message, 160) + ")";
            }
            if (reason == null) return found;
            if (refuseUnreadable) throw new CatalogRejectedException(reason);
            return "";
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

        /// <summary>
        /// Read a downloaded catalog.json the way the app does: within the download cap, strict UTF-8, then
        /// <see cref="Parse"/>. The one door for every reader of fetched bytes AND for --catalog-parse-file,
        /// which is how the gate, CI and the publish scripts run this app's own parser over the repository's
        /// catalog.json before it can reach master (feature/catalog-insight). A file the app could not have
        /// downloaded or decoded is refused here in the same words a fetch would use.
        /// </summary>
        internal static RemoteCatalog ParseBytes(byte[] bytes)
        {
            if (bytes != null && bytes.Length > MaximumCatalogBytes)
                throw new CatalogRejectedException(
                    "catalog.json is " + CatalogText.Number(bytes.Length) + " bytes, more than the " +
                    CatalogText.Number(MaximumCatalogBytes) + " bytes this app downloads");
            string json;
            try { json = SecureDownload.DecodeUtf8(bytes); }
            catch (DecoderFallbackException ex)
            {
                throw new CatalogRejectedException(
                    "catalog.json is not valid UTF-8 (" + CatalogText.Echo(ex.Message, 120) + ")", ex);
            }
            return Parse(json);
        }

        /// <summary>
        /// Parse catalog.json. Two kinds of failure, kept apart on purpose (feature/catalog-insight).
        ///
        /// STRUCTURAL: not JSON, not an object, a schema version this build does not read, a root licence past
        /// its bound, a list longer than <see cref="MaximumEntries"/>. That refuses the whole catalog with a
        /// <see cref="CatalogRejectedException"/> naming the rule, because nothing in such a file can be trusted
        /// to mean what this build thinks it means.
        ///
        /// ONE ENTRY breaking a rule refuses that entry alone. It goes into <see cref="RemoteCatalog.Rejected"/>
        /// with its id (or its position, when the id is the problem) and the rule, and into none of the lists,
        /// so it is never offered. This used to throw for the WHOLE catalog, and every catalog feature shares
        /// this one parse: on 2026-10-06 one module description 25 characters over its bound took module
        /// updates, pack downloads and the companion gallery away from every installed app, behind a message
        /// that named neither the entry nor the rule (BUG-014). The throw was never an integrity property. The
        /// catalog is HTTPS-trusted from the project repository and every asset it lists is SHA-256-verified on
        /// download; whoever can write catalog.json can write a VALID entry, so refusing the valid neighbours of
        /// an invalid one defends against nothing. TryParsePermissions below records the same lesson one level
        /// down, and the register's entry under feature/catalog-insight records this one.
        ///
        /// The ACCEPT SET is unchanged: each entry's rules are the clauses of the single condition this used
        /// to carry, in that order, so an entry accepted before is accepted now and one refused before is
        /// refused now. Only the consequence moved, from the catalog to the entry.
        /// </summary>
        internal static RemoteCatalog Parse(string json)
        {
            var catalog = new RemoteCatalog();
            JsonNode root;
            try { root = JsonNode.Parse(json ?? ""); }
            catch (JsonException ex)
            {
                throw new CatalogRejectedException(
                    "catalog.json is not valid JSON (" + CatalogText.Echo(ex.Message, 160) + ")", ex);
            }
            if (!(root is JsonObject))
                throw new CatalogRejectedException("catalog.json is not a JSON object");

            JsonArray pets;
            JsonArray packs;
            JsonArray modules;
            string defaultPackLicense;
            try
            {
                int? schema = JsonRead.IntOrNull(root["version"]);
                if (schema != 1)
                    throw new CatalogRejectedException(schema.HasValue
                        ? "catalog.json is schema version " + CatalogText.Number(schema.Value) + ", and this app reads version 1"
                        : "catalog.json declares no schema version, and this app reads version 1");

                // One licence for the whole pack library, declared once at the root. It used to be stamped
                // onto all 158 pack entries: 158 copies of a single fact, because every pack carries the
                // same terms. A pack may still declare its own, so a differently-licensed pack needs no
                // schema change.
                defaultPackLicense = JsonRead.Str(root["packLicense"]).Trim();
                if (defaultPackLicense.Length > 256)
                    throw new CatalogRejectedException(CatalogText.TooLong("packLicense", defaultPackLicense.Length, 256));

                pets = Section(root, "companions", CatalogRejection.Companion, catalog);
                packs = Section(root, "packs", CatalogRejection.Pack, catalog);
                modules = Section(root, "modules", CatalogRejection.Module, catalog);
                RequireListBound(pets, "companions");
                RequireListBound(packs, "packs");
                RequireListBound(modules, "modules");
            }
            catch (CatalogRejectedException) { throw; }
            catch (Exception ex)
            {
                // A root that parsed as an object and still cannot be read (a duplicated key, say).
                throw new CatalogRejectedException(
                    "catalog.json could not be read (" + CatalogText.Echo(ex.Message, 160) + ")", ex);
            }

            var petIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (pets != null)
                for (int index = 0; index < pets.Count; index++)
                {
                    CatalogCompanion pet = null;
                    string named = "";
                    string rule;
                    try
                    {
                        JsonObject token = pets[index] as JsonObject;
                        if (token == null) rule = "is not a JSON object";
                        else
                        {
                            pet = new CatalogCompanion
                            {
                                Id = JsonRead.Str(token["id"]).Trim(),
                                Name = JsonRead.Str(token["name"]).Trim(),
                                Author = JsonRead.Str(token["author"]).Trim(),
                                Url = JsonRead.Str(token["url"]).Trim(),
                                Sha256 = JsonRead.Str(token["sha256"]).Trim().ToLowerInvariant(),
                                Bytes = JsonRead.IntOrNull(token["bytes"]) ?? 0,
                                // Absent in a catalog written before these fields existed, which is a supported
                                // state: the card then shows the download size alone, exactly as it used to.
                                Animations = JsonRead.IntOrNull(token["animations"]) ?? 0,
                                Sounds = JsonRead.IntOrNull(token["sounds"]) ?? 0
                            };
                            rule = IdRule(pet.Id, petIds);
                            if (rule == null) { named = pet.Id; rule = PetRule(pet); }
                        }
                    }
                    catch (Exception ex) { rule = "could not be read (" + CatalogText.Echo(ex.Message, 120) + ")"; }
                    if (rule != null)
                    {
                        Refuse(catalog, CatalogRejection.Companion, index, named, rule, pet != null ? pet.Name : "", "");
                        continue;
                    }
                    catalog.Pets.Add(pet);
                }

            var packIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (packs != null)
                for (int index = 0; index < packs.Count; index++)
                {
                    CatalogPack pack = null;
                    string named = "";
                    string rule;
                    try
                    {
                        JsonObject token = packs[index] as JsonObject;
                        if (token == null) rule = "is not a JSON object";
                        else
                        {
                            pack = new CatalogPack
                            {
                                Id = JsonRead.Str(token["id"]).Trim(),
                                Name = JsonRead.Str(token["name"]).Trim(),
                                Group = JsonRead.Str(token["group"]).Trim(),
                                Description = JsonRead.Str(token["desc"]).Trim(),
                                License = token["license"] != null
                                    ? JsonRead.Str(token["license"]).Trim()
                                    : defaultPackLicense,
                                Url = JsonRead.Str(token["url"]).Trim(),
                                Sha256 = JsonRead.Str(token["sha256"]).Trim().ToLowerInvariant(),
                                Bytes = JsonRead.IntOrNull(token["bytes"]) ?? 0,
                                Count = JsonRead.IntOrNull(token["count"]) ?? 0,
                                DataSchema = JsonRead.IntOrNull(token["dataSchema"]) ?? 0
                            };
                            rule = IdRule(pack.Id, packIds);
                            if (rule == null) { named = pack.Id; rule = PackRule(pack); }
                        }
                    }
                    catch (Exception ex) { rule = "could not be read (" + CatalogText.Echo(ex.Message, 120) + ")"; }
                    if (rule != null)
                    {
                        Refuse(catalog, CatalogRejection.Pack, index, named, rule, pack != null ? pack.Name : "", "");
                        continue;
                    }
                    catalog.Packs.Add(pack);
                }

            // (see TryParsePermissions below for why an unknown flag name is dropped rather than fatal)
            var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (modules != null)
                for (int index = 0; index < modules.Count; index++)
                {
                    CatalogModule module = null;
                    string named = "";
                    string rule;
                    try
                    {
                        JsonObject token = modules[index] as JsonObject;
                        if (token == null) rule = "is not a JSON object";
                        else
                        {
                            string permissionText = JsonRead.Str(token["permissions"]).Trim();
                            ModulePermissions permissions;
                            bool permissionsValid = TryParsePermissions(permissionText, out permissions);
                            module = new CatalogModule
                            {
                                Id = JsonRead.Str(token["id"]).Trim(),
                                Name = JsonRead.Str(token["name"]).Trim(),
                                Description = JsonRead.Str(token["desc"]).Trim(),
                                Version = JsonRead.Str(token["version"]).Trim(),
                                Url = JsonRead.Str(token["url"]).Trim(),
                                Sha256 = JsonRead.Str(token["sha256"]).Trim().ToLowerInvariant(),
                                Bytes = JsonRead.IntOrNull(token["bytes"]) ?? 0,
                                Permissions = permissions,
                                MinHostVersion = JsonRead.Str(token["minHostVersion"]).Trim()
                            };
                            rule = IdRule(module.Id, moduleIds);
                            if (rule == null) { named = module.Id; rule = ModuleRule(module, permissionsValid, permissionText); }
                        }
                    }
                    catch (Exception ex) { rule = "could not be read (" + CatalogText.Echo(ex.Message, 120) + ")"; }
                    if (rule != null)
                    {
                        Refuse(catalog, CatalogRejection.Module, index, named, rule,
                            module != null ? module.Name : "", module != null ? module.Version : "");
                        continue;
                    }
                    catalog.Modules.Add(module);
                }

            return catalog;
        }

        /// <summary>
        /// The list under <paramref name="key"/>: null when it is absent (an older or smaller catalog), and null
        /// with a refusal of the whole list recorded when it is there but is not a list. That second case used to
        /// read as an empty list in silence, so a catalog whose "modules" had become an object offered no module
        /// and said nothing; now it says so, and the other two lists are unaffected.
        /// </summary>
        private static JsonArray Section(JsonNode root, string key, string kind, RemoteCatalog catalog)
        {
            JsonNode node = root[key];
            if (node == null) return null;
            JsonArray list = node as JsonArray;
            if (list == null)
                catalog.Rejected.Add(new CatalogRejection
                {
                    Kind = kind,
                    Index = -1,
                    Id = "",
                    Rule = "the list is not a JSON array, so no " + kind + " is offered",
                });
            return list;
        }

        /// <summary>A list longer than <see cref="MaximumEntries"/> is a bound on the whole document, not an
        /// entry's fault, so it still refuses the catalog.</summary>
        private static void RequireListBound(JsonArray list, string key)
        {
            if (list != null && list.Count > MaximumEntries)
                throw new CatalogRejectedException(
                    "the catalog lists " + CatalogText.Number(list.Count) + " " + key + ", and the limit is " +
                    CatalogText.Number(MaximumEntries));
        }

        private static void Refuse(RemoteCatalog catalog, string kind, int index, string id, string rule,
            string name, string version)
        {
            catalog.Rejected.Add(new CatalogRejection
            {
                Kind = kind, Index = index, Id = id ?? "", Rule = rule,
                Name = CatalogText.Echo(name, 64), Version = CatalogText.Echo(version, 32),
            });
        }

        /// <summary>
        /// The id half every entry kind shares, in the old condition's order: safe, then unique. The rule broken,
        /// or null. An id that fails either names the entry by its position (the id is the problem), with the id
        /// itself echoed through <see cref="CatalogText.Echo"/>. A refused entry's id still counts as seen, as it
        /// did when the uniqueness check ran ahead of the rest, so a second entry with that id reads as the
        /// duplicate it is rather than quietly standing in for the first.
        /// </summary>
        private static string IdRule(string id, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(id)) return "has no id";
            if (!SecureDownload.IsSafeId(id))
                return "id \"" + CatalogText.Echo(id, 40) + "\" is not a valid id (1 to 64 letters, digits, '.', '_' " +
                       "or '-', not starting or ending with a symbol, and not a Windows device name such as CON)";
            if (!seen.Add(id)) return "id \"" + id + "\" is already used by an earlier entry";
            return null;
        }

        private static string PetRule(CatalogCompanion pet)
        {
            if (string.IsNullOrWhiteSpace(pet.Name)) return "has no name";
            if (pet.Name.Length > 128) return CatalogText.TooLong("name", pet.Name.Length, 128);
            if (pet.Author.Length > 128) return CatalogText.TooLong("author", pet.Author.Length, 128);
            if (pet.Bytes < 1 || pet.Bytes > CompanionXmlValidator.MaximumXmlBytes)
                return CatalogText.OutOfRange("bytes", pet.Bytes, 1, CompanionXmlValidator.MaximumXmlBytes);
            // Bounded like every other number here. These only ever render as text, so the risk is a nonsense
            // card rather than anything unsafe -- but an entry this file cannot believe is an entry it rejects,
            // and the largest shipped pet carries 1133.
            if (pet.Animations < 0 || pet.Animations > MaximumCountedItems)
                return CatalogText.OutOfRange("animations", pet.Animations, 0, MaximumCountedItems);
            if (pet.Sounds < 0 || pet.Sounds > MaximumCountedItems)
                return CatalogText.OutOfRange("sounds", pet.Sounds, 0, MaximumCountedItems);
            if (!IsSha256(pet.Sha256)) return "sha256 is not 64 hex characters";
            if (!IsPetAssetUrl(pet.Url, pet.Id))
                return "url is not this repository's Companions/" + pet.Id + "/animations.xml on raw.githubusercontent.com";
            return null;
        }

        private static string PackRule(CatalogPack pack)
        {
            if (string.IsNullOrWhiteSpace(pack.Name)) return "has no name";
            if (pack.Name.Length > 128) return CatalogText.TooLong("name", pack.Name.Length, 128);
            if (pack.Group.Length > 128) return CatalogText.TooLong("group", pack.Group.Length, 128);
            if (pack.Description.Length > 1024) return CatalogText.TooLong("description", pack.Description.Length, 1024);
            if (pack.License.Length > 256) return CatalogText.TooLong("license", pack.License.Length, 256);
            if (pack.DataSchema != 1 && pack.DataSchema != 2)
                return (pack.DataSchema == 0 ? "dataSchema is missing or 0" : "dataSchema is " + CatalogText.Number(pack.DataSchema)) +
                       ", and this app reads 1 or 2";
            if (!IsSha256(pack.Sha256)) return "sha256 is not 64 hex characters";
            if (!IsPackAssetUrl(pack.Url, pack.Id))
                return "url is not this repository's packs/" + pack.Id + ".txt on raw.githubusercontent.com";
            // Reuse the same runtime-loadability bounds the embedded catalog enforces. The policy decides; the
            // words name whichever of its two bounds the entry is outside.
            string packError;
            if (!FortunePackLoadPolicy.TryValidatePackMetadata(pack.Bytes, pack.Count, out packError))
                return pack.Bytes < 1 || pack.Bytes > FortunePackLoadPolicy.MaximumFileBytes
                    ? CatalogText.OutOfRange("bytes", pack.Bytes, 1, FortunePackLoadPolicy.MaximumFileBytes)
                    : CatalogText.OutOfRange("count", pack.Count, 1, FortunePackLoadPolicy.MaximumEntries);
            return null;
        }

        private static string ModuleRule(CatalogModule module, bool permissionsValid, string permissionText)
        {
            if (string.IsNullOrWhiteSpace(module.Name)) return "has no name";
            if (module.Name.Length > 128) return CatalogText.TooLong("name", module.Name.Length, 128);
            if (module.Description.Length > 1024)
                return CatalogText.TooLong("description", module.Description.Length, 1024);
            if (string.IsNullOrWhiteSpace(module.Version)) return "has no version";
            if (module.Version.Length > 32) return CatalogText.TooLong("version", module.Version.Length, 32);
            // Bounded like Version beside it. Absent is fine (every host satisfies ""), nonsense is not.
            if (module.MinHostVersion.Length > 32)
                return CatalogText.TooLong("minHostVersion", module.MinHostVersion.Length, 32);
            if (!permissionsValid)
                return string.IsNullOrEmpty(permissionText)
                    ? "permissions is empty (a module that needs none lists None)"
                    : "permissions \"" + CatalogText.Echo(permissionText, 80) + "\" has an empty item";
            if (module.Bytes < 1 || module.Bytes > MaximumModuleBytes)
                return CatalogText.OutOfRange("bytes", module.Bytes, 1, MaximumModuleBytes);
            if (!IsSha256(module.Sha256)) return "sha256 is not 64 hex characters";
            if (!IsModuleAssetUrl(module.Url, module.Id))
                return "url is not this repository's modules-dist/" + module.Id + ".zip on raw.githubusercontent.com";
            return null;
        }

        /// <summary>
        /// Parse a comma-separated permission list, DROPPING names this build does not know instead of
        /// rejecting the entry.
        ///
        /// This used to be a single Enum.TryParse over the whole string, and a miss failed the entire
        /// catalog -- not the entry -- because every catalog feature shares one fetch. So the first release
        /// to add a permission name silently took the Modules pane, the weekly update check, fortune-pack
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
            // owner / repo / <ref> / Companions / <id> / animations.xml  (the folder was Pets before the 1.0.0 rename)
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
            // The vocabulary every graded marker speaks (PASS:/FAIL: lines, a column-0 RESULT= line), so
            // tests/mutate-selftest-guards.py can score a mutation of this parser naming the check that fired.
            // This report used to write "CATALOG FAIL ..." lines and a closing catalog_parse= line, which no
            // harness could grade: a parser guard here could only be mutation-tested by hand (F288 was).
            void Check(string label, bool condition)
            {
                report.AppendLine((condition ? "PASS: " : "FAIL: ") + label);
                if (!condition) ok = false;
            }

            string validPet = "{ \"id\": \"fox\", \"name\": \"Fox\", \"author\": \"Michelle\", \"url\": \"" + PetUrlBase +
                "fox/animations.xml\", \"sha256\": \"" + SampleSha + "\", \"bytes\": 33556 }";
            string validPack = "{ \"id\": \"tech\", \"name\": \"Tech\", \"desc\": \"quips\", \"url\": \"" + PackUrlBase +
                "tech.txt\", \"sha256\": \"" + SampleSha + "\", \"bytes\": 308767, \"count\": 620, \"dataSchema\": 2 }";
            string validModule = "{ \"id\": \"fortunes\", \"name\": \"Fortunes\", \"desc\": \"Offline smart fortunes\", " +
                "\"version\": \"1.2.1\", \"url\": \"" + ModuleUrlBase + "fortunes.zip\", \"sha256\": \"" + SampleSha +
                "\", \"bytes\": 2048, \"permissions\": \"Speech, Storage\" }";
            Func<string, string, string, string> catalogOf = delegate (string companions, string packs, string modules)
            {
                return "{ \"version\": 1, \"packLicense\": \"NOASSERTION\", \"companions\": [ " + companions +
                       " ], \"packs\": [ " + packs + " ], \"modules\": [ " + modules + " ] }";
            };
            Func<string, string, string> moduleWithDescription = delegate (string id, string description)
            {
                return "{ \"id\": \"" + id + "\", \"name\": \"AgentFlow\", \"desc\": \"" + description +
                       "\", \"version\": \"1.5.0\", \"url\": \"" + ModuleUrlBase + id + ".zip\", \"sha256\": \"" +
                       SampleSha + "\", \"bytes\": 2048, \"permissions\": \"Storage\" }";
            };

            string validJson =
                "{ \"version\": 1, \"packLicense\": \"NOASSERTION\", " +
                "\"companions\": [ " + validPet + " ], " +
                // Two packs on purpose: the first inherits the root licence, the second declares its
                // own. Both paths ship, so both are parsed here rather than only the common one.
                "\"packs\": [ " + validPack + ", " +
                "{ \"id\": \"perl\", \"name\": \"Perl\", \"desc\": \"quips\", " +
                "\"license\": \"MIT\", \"url\": \"" + PackUrlBase +
                "perl.txt\", \"sha256\": \"" + SampleSha + "\", \"bytes\": 1024, " +
                "\"count\": 12, \"dataSchema\": 2 } ], " +
                "\"modules\": [ " + validModule + " ] }";
            // The catalog may list up to MaximumEntries packs, and a user can install all of them, so the
            // runtime file cap must not be lower -- when it was (512 listed vs 128 loadable), the overflow
            // was dropped silently on load and those packs just never spoke.
            // (Read into locals: comparing two consts folds to a constant and trips "unreachable code".)
            //
            // SCOPE: since F124 there is ONE FortunePackLoadPolicy. The Fortunes module compiles the host's
            // src\dotNet\Ai\FortunePackLoadPolicy.cs by source link rather than carrying a copy, so the
            // constant read here IS the cap that governs loading, and this check compares it against the
            // catalog entry cap directly. The source invariant in tests\runtime-hardening-selftest.ps1
            // asserts the link is present and that no copy has grown back inside the module (F124; it used
            // to compare two copies as numbers, which is what the F287 line describes). Until RA-244's
            // sweep this comment still said the governing cap was a module copy this process cannot see.
            int loadableFileCap = FortunePackLoadPolicy.MaximumFiles;
            int catalogEntryCap = MaximumEntries;
            Check("the pack file cap covers the catalog entry cap (" + loadableFileCap + " files, " +
                  catalogEntryCap + " entries)", loadableFileCap >= catalogEntryCap);

            try
            {
                RemoteCatalog catalog = Parse(validJson);
                Check("a valid catalog parses whole: one companion, two packs, one module with Speech and Storage, nothing refused (" +
                      catalog.Pets.Count + "/" + catalog.Packs.Count + "/" + catalog.Modules.Count + ", " +
                      catalog.Rejected.Count + " refused)",
                      catalog.Pets.Count == 1 && catalog.Packs.Count == 2 && catalog.Modules.Count == 1 &&
                      catalog.Rejected.Count == 0 &&
                      catalog.Modules[0].Permissions == (ModulePermissions.Speech | ModulePermissions.Storage));
                // A pack with no licence of its own inherits the root one; a pack that declares one
                // keeps it. Both halves are asserted, because getting the fallback backwards would
                // relicense the entire library from a single line of JSON without failing anything.
                Check("a pack with no licence inherits the root one, and one that declares its own keeps it (got '" +
                      (catalog.Packs.Count > 0 ? catalog.Packs[0].License : "") + "' and '" +
                      (catalog.Packs.Count > 1 ? catalog.Packs[1].License : "") + "')",
                      catalog.Packs.Count == 2 && catalog.Packs[0].License == "NOASSERTION" && catalog.Packs[1].License == "MIT");
            }
            catch (Exception ex)
            {
                Check("a valid catalog parses whole: it threw " + ex.GetType().Name + ": " + ex.Message, false);
            }

            // ONE BAD ENTRY IS REFUSED ALONE (feature/catalog-insight). Each case is a catalog holding one valid
            // companion, one valid pack and one valid module, plus the one entry under test. The entry must be
            // refused with the rule it broke, named the way a person reads it, and the three valid entries must
            // survive: these used to be "reject-case N was accepted" checks against a parse that threw for the
            // whole catalog, which could not tell which rule fired or what else was lost.
            //
            // The rule TEXT is what isolates each parser clause. The `../etc` pack used to be labelled "unsafe
            // id" while its URL check rejected it first, so deleting the parser's IsSafeId term left every
            // reject case green (F288); its rule now names the id, so that deletion turns it into a url refusal
            // and fails here, and `con` (an id the URL check accepts) still isolates the device-name rule.
            var refusals = new[]
            {
                new[] { "a companion with a malformed sha256", CatalogRejection.Companion,
                    "{ \"id\": \"fox2\", \"name\": \"Fox\", \"url\": \"" + PetUrlBase + "fox2/animations.xml\", " +
                    "\"sha256\": \"notahash\", \"bytes\": 33556 }",
                    "companion \"fox2\": sha256 is not 64 hex characters" },
                new[] { "a companion hosted off the repository", CatalogRejection.Companion,
                    "{ \"id\": \"fox2\", \"name\": \"Fox\", \"url\": \"https://evil.example.com/x/animations.xml\", " +
                    "\"sha256\": \"" + SampleSha + "\", \"bytes\": 10 }",
                    "companion \"fox2\": url is not this repository's Companions/fox2/animations.xml on raw.githubusercontent.com" },
                new[] { "a companion whose url names another id", CatalogRejection.Companion,
                    "{ \"id\": \"fox2\", \"name\": \"Fox\", \"url\": \"" + PetUrlBase + "notfox/animations.xml\", " +
                    "\"sha256\": \"" + SampleSha + "\", \"bytes\": 10 }",
                    "companion \"fox2\": url is not this repository's Companions/fox2/animations.xml on raw.githubusercontent.com" },
                new[] { "a companion name past its bound", CatalogRejection.Companion,
                    "{ \"id\": \"fox2\", \"name\": \"" + new string('n', 129) + "\", \"url\": \"" + PetUrlBase +
                    "fox2/animations.xml\", \"sha256\": \"" + SampleSha + "\", \"bytes\": 10 }",
                    "companion \"fox2\": name is 129 characters, the limit is 128" },
                new[] { "a pack whose id is a path", CatalogRejection.Pack,
                    "{ \"id\": \"../etc\", \"name\": \"x\", \"url\": \"" + PackUrlBase + "x.txt\", \"sha256\": \"" +
                    SampleSha + "\", \"bytes\": 10, \"count\": 1, \"dataSchema\": 2 }",
                    "pack entry 2: id \"../etc\" is not a valid id" },
                new[] { "a pack whose id is a Windows device name", CatalogRejection.Pack,
                    "{ \"id\": \"con\", \"name\": \"x\", \"url\": \"" + PackUrlBase + "con.txt\", \"sha256\": \"" +
                    SampleSha + "\", \"bytes\": 10, \"count\": 1, \"dataSchema\": 2 }",
                    "pack entry 2: id \"con\" is not a valid id" },
                new[] { "a pack with no byte count", CatalogRejection.Pack,
                    "{ \"id\": \"tech2\", \"name\": \"x\", \"url\": \"" + PackUrlBase + "tech2.txt\", \"sha256\": \"" +
                    SampleSha + "\", \"count\": 1, \"dataSchema\": 2 }",
                    "pack \"tech2\": bytes is missing or 0, it must be 1 to " + FortunePackLoadPolicy.MaximumFileBytes },
                // An EMPTY permission list is still malformed. An unrecognised NAME is not -- see below.
                new[] { "a module with an empty permission list", CatalogRejection.Module,
                    "{ \"id\": \"x\", \"name\": \"X\", \"version\": \"1.0\", \"url\": \"" + ModuleUrlBase + "x.zip\", " +
                    "\"sha256\": \"" + SampleSha + "\", \"bytes\": 10, \"permissions\": \"\" }",
                    "module \"x\": permissions is empty (a module that needs none lists None)" },
                new[] { "a module with a malformed permission list", CatalogRejection.Module,
                    "{ \"id\": \"x\", \"name\": \"X\", \"version\": \"1.0\", \"url\": \"" + ModuleUrlBase + "x.zip\", " +
                    "\"sha256\": \"" + SampleSha + "\", \"bytes\": 10, \"permissions\": \"Speech,,Storage\" }",
                    "module \"x\": permissions \"Speech,,Storage\" has an empty item" },
                // BUG-014, the outage itself: agentflow 1.5.0 published a 1049-character description.
                new[] { "the outage: a module description 25 characters past its bound", CatalogRejection.Module,
                    moduleWithDescription("agentflow", new string('d', 1049)),
                    "module \"agentflow\": description is 1049 characters, the limit is 1024" },
                new[] { "a module entry that is not an object, named by its position", CatalogRejection.Module,
                    "5",
                    "module entry 2: is not a JSON object" },
                new[] { "a module id used twice, named by the second one's position", CatalogRejection.Module,
                    validModule,
                    "module entry 2: id \"fortunes\" is already used by an earlier entry" },
            };
            foreach (string[] refusal in refusals)
            {
                string name = refusal[0], kind = refusal[1], entry = refusal[2], expected = refusal[3];
                string json = catalogOf(
                    kind == CatalogRejection.Companion ? validPet + ", " + entry : validPet,
                    kind == CatalogRejection.Pack ? validPack + ", " + entry : validPack,
                    kind == CatalogRejection.Module ? validModule + ", " + entry : validModule);
                try
                {
                    RemoteCatalog parsed = Parse(json);
                    string said = parsed.Rejected.Count == 1 ? parsed.Rejected[0].ToString() : parsed.Rejected.Count + " refused";
                    Check("refused alone, naming the entry and the rule: " + name + " (" + said + ")",
                          parsed.Rejected.Count == 1 && parsed.Rejected[0].Kind == kind &&
                          said.StartsWith(expected, StringComparison.Ordinal) &&
                          parsed.Pets.Count == 1 && parsed.Packs.Count == 1 && parsed.Modules.Count == 1 &&
                          parsed.Pets[0].Id == "fox" && parsed.Packs[0].Id == "tech" && parsed.Modules[0].Id == "fortunes");
                }
                catch (Exception ex)
                {
                    Check("refused alone, naming the entry and the rule: " + name + " (the whole catalog threw " +
                          ex.GetType().Name + ": " + ex.Message + ")", false);
                }
            }

            // ...and the bound itself, from the other side: 1024 is accepted, so the outage check above is about
            // the limit and not about a description of any length.
            try
            {
                RemoteCatalog atLimit = Parse(catalogOf(validPet, validPack,
                    validModule + ", " + moduleWithDescription("agentflow", new string('d', 1024))));
                Check("WITNESS a module description of exactly 1024 characters is offered (" +
                      atLimit.Modules.Count + " modules, " + atLimit.Rejected.Count + " refused)",
                      atLimit.Modules.Count == 2 && atLimit.Rejected.Count == 0);
                // The weekly checks' stamp decision (StartUp): a refused MODULE entry counts against the module
                // check and against nothing else.
                RemoteCatalog outage = Parse(catalogOf(validPet, validPack,
                    validModule + ", " + moduleWithDescription("agentflow", new string('d', 1049))));
                Check("a refused module entry counts against the module check and not the companion or pack checks (module " +
                      outage.RefusedCount(CatalogRejection.Module) + ", companion " + outage.RefusedCount(CatalogRejection.Companion) +
                      ", pack " + outage.RefusedCount(CatalogRejection.Pack) + ")",
                      outage.RefusedCount(CatalogRejection.Module) == 1 && outage.RefusedCount(CatalogRejection.Companion) == 0 &&
                      outage.RefusedCount(CatalogRejection.Pack) == 0);
                // The panel names the refused entry with what the catalog called it, and the row says which version
                // is not offered, so the refusal keeps the entry's name and version (sanitised).
                CatalogRejection outageEntry = outage.Rejected.Count == 1 ? outage.Rejected[0] : null;
                Check("a refused module entry keeps its name and version for the panel (" +
                      (outageEntry == null ? "none" : outageEntry.Name + " " + outageEntry.Version) + ")",
                      outageEntry != null && outageEntry.Name == "AgentFlow" && outageEntry.Version == "1.5.0");
            }
            catch (Exception ex)
            {
                Check("WITNESS a module description of exactly 1024 characters is offered: it threw " + ex.Message, false);
            }

            // What a refusal ECHOES from the catalog is sanitised: a bidi override, a bell and a quote in an id
            // reach the message as '?', '?' and an apostrophe, never as themselves.
            try
            {
                RemoteCatalog hostile = Parse(catalogOf(validPet, validPack,
                    validModule + ", { \"id\": \"evil\\u202Eid\\u0007\\\"x\", \"name\": \"X\", \"version\": \"1.0\", " +
                    "\"url\": \"" + ModuleUrlBase + "x.zip\", \"sha256\": \"" + SampleSha + "\", \"bytes\": 10, " +
                    "\"permissions\": \"Storage\" }"));
                string said = hostile.Rejected.Count == 1 ? hostile.Rejected[0].ToString() : "";
                Check("an id echoed into a refusal is sanitised: no bidi override, control character or bare quote (" +
                      Echo(said) + ")",
                      said.StartsWith("module entry 2: id \"evil?id?'x\" is not a valid id", StringComparison.Ordinal) &&
                      said.IndexOf('‮') < 0 && said.IndexOf('\u0007') < 0);
            }
            catch (Exception ex)
            {
                Check("an id echoed into a refusal is sanitised: it threw " + ex.Message, false);
            }

            // A list that is not a list is refused as a list, and the other two lists are unaffected. It used to
            // read as an empty list in silence.
            try
            {
                RemoteCatalog noList = Parse("{ \"version\": 1, \"companions\": [ " + validPet + " ], \"packs\": [ " +
                    validPack + " ], \"modules\": { \"id\": \"fortunes\" } }");
                string said = noList.Rejected.Count == 1 ? noList.Rejected[0].ToString() : noList.Rejected.Count + " refused";
                Check("a modules list that is not an array is refused as a list, and the companions and packs stay (" + said + ")",
                      said == "modules: the list is not a JSON array, so no module is offered" &&
                      noList.Pets.Count == 1 && noList.Packs.Count == 1 && noList.Modules.Count == 0 &&
                      noList.RefusedCount(CatalogRejection.Module) == 1);
            }
            catch (Exception ex)
            {
                Check("a modules list that is not an array is refused as a list: the whole catalog threw " + ex.Message, false);
            }

            // STRUCTURAL failures still refuse the whole catalog, as a CatalogRejectedException naming the rule, so
            // a pane can say "reached but could not be read" rather than "couldn't reach".
            var tooMany = new StringBuilder();
            for (int i = 0; i <= MaximumEntries; i++) tooMany.Append(i == 0 ? "0" : ", 0");
            var structural = new[]
            {
                new[] { "a schema version this build does not read", "{ \"version\": 2, \"companions\": [], \"packs\": [] }",
                    "catalog.json is schema version 2, and this app reads version 1" },
                new[] { "a body that is not JSON", "<html>rate limited</html>", "catalog.json is not valid JSON (" },
                new[] { "a body that is JSON but not an object", "[ 1, 2 ]", "catalog.json is not a JSON object" },
                new[] { "a root licence past its bound",
                    "{ \"version\": 1, \"packLicense\": \"" + new string('l', 257) + "\", \"packs\": [] }",
                    "packLicense is 257 characters, the limit is 256" },
                new[] { "a list longer than the entry bound", "{ \"version\": 1, \"modules\": [ " + tooMany + " ] }",
                    "the catalog lists " + (MaximumEntries + 1) + " modules, and the limit is " + MaximumEntries },
            };
            foreach (string[] whole in structural)
            {
                string thrown = "(nothing thrown)";
                bool rejected = false;
                try { Parse(whole[1]); }
                catch (CatalogRejectedException ex) { rejected = ex.Message.StartsWith(whole[2], StringComparison.Ordinal); thrown = ex.Message; }
                catch (Exception ex) { thrown = ex.GetType().Name + ": " + ex.Message; }
                Check("the whole catalog is refused, as a refusal naming the rule: " + whole[0] + " (" + Echo(thrown) + ")", rejected);
            }
            // ...and the byte-level door every fetched catalog and --catalog-parse-file go through.
            var bytesCases = new[]
            {
                new KeyValuePair<string, byte[]>("catalog.json is not valid UTF-8 (", new byte[] { 0x7B, 0xFF, 0xFE, 0x7D }),
                new KeyValuePair<string, byte[]>("catalog.json is " + (MaximumCatalogBytes + 1) + " bytes, more than the " +
                    MaximumCatalogBytes + " bytes this app downloads", new byte[MaximumCatalogBytes + 1]),
            };
            foreach (KeyValuePair<string, byte[]> bytesCase in bytesCases)
            {
                string thrown = "(nothing thrown)";
                bool rejected = false;
                try { ParseBytes(bytesCase.Value); }
                catch (CatalogRejectedException ex) { rejected = ex.Message.StartsWith(bytesCase.Key, StringComparison.Ordinal); thrown = ex.Message; }
                catch (Exception ex) { thrown = ex.GetType().Name + ": " + ex.Message; }
                Check("the byte-level read refuses what the app could not download or decode (" + Echo(thrown) + ")", rejected);
            }

            // THE LAUNCH APP-VERSION CHECK'S READ. AppUpdateCheck.MaybeCheckAsync stamps whatever
            // FetchAppVersionAsync returns as the week's answer, so an unreadable catalog must be a refusal
            // there and never "" ("nothing newer"), or a catalog published broken would hide a release for a
            // week. An ABSENT app block is an answer (an older catalog) and stays "".
            var unreadableAppBlocks = new[] { "<html>rate limited</html>", "[ 1 ]", "{ \"app\": 5 }", "{ \"app\": { \"version\": 19.8 } }" };
            foreach (string unreadable in unreadableAppBlocks)
            {
                string thrown = "(returned \"" + Echo(ParseAppVersion(unreadable, false)) + "\" without refusing)";
                bool refusedRead = false;
                try { ParseAppVersion(unreadable, true); }
                catch (CatalogRejectedException ex) { refusedRead = true; thrown = ex.Message; }
                catch (Exception ex) { thrown = ex.GetType().Name + ": " + ex.Message; }
                Check("the launch app-version read refuses an unreadable catalog rather than reading it as nothing newer (" +
                      Echo(thrown) + ")", refusedRead);
            }
            string absentBlock = "(threw)";
            string presentBlock = "(threw)";
            try { absentBlock = ParseAppVersion("{ \"version\": 1, \"modules\": [] }", true); } catch { }
            try { presentBlock = ParseAppVersion("{ \"app\": { \"version\": \" 1.3.0 \" } }", true); } catch { }
            Check("WITNESS the launch app-version read answers \"\" for a catalog with no app block and the version for one with it (\"" +
                  absentBlock + "\", \"" + presentBlock + "\")", absentBlock == "" && presentBlock == "1.3.0");

            // A permission name this build does not know must NOT fail the catalog. It used to, and because
            // every catalog feature shares one fetch, the first release to add a flag silently took the
            // Modules pane, the weekly update check, pack browsing and the Pets gallery away from every
            // older host. The unknown flag is dropped; the known ones survive; MinHostVersion is what
            // actually refuses the module.
            try
            {
                string forwardCompatible =
                    "{ \"version\": 1, \"companions\": [], \"packs\": [], \"modules\": [ { \"id\": \"x\", " +
                    "\"name\": \"X\", \"version\": \"1.0\", \"url\": \"" + ModuleUrlBase +
                    "x.zip\", \"sha256\": \"" + SampleSha +
                    "\", \"bytes\": 10, \"permissions\": \"Speech, FromAFutureHost, Storage\" } ] }";
                RemoteCatalog forward = Parse(forwardCompatible);
                Check("an unknown permission name is dropped and the module kept with its known flags",
                      forward.Modules.Count == 1 && forward.Rejected.Count == 0 &&
                      forward.Modules[0].Permissions == (ModulePermissions.Speech | ModulePermissions.Storage));
            }
            catch (Exception ex)
            {
                Check("an unknown permission name is dropped and the module kept: the whole catalog threw " + ex.Message, false);
            }

            // THE WORDS (CatalogText): the two failure cases worded apart, the inner reason surfaced, the refusal
            // note naming its entries and whose fault it is.
            DateTime at = new DateTime(2026, 10, 6, 14, 32, 0);
            var refusedWhole = new CatalogRejectedException("catalog.json is not valid JSON (x)");
            var unreachable = new System.Net.Http.HttpRequestException("No such host is known. (raw.githubusercontent.com:443)");
            string refusedLine = CatalogText.FetchFailed(refusedWhole, at, "Check for modules online");
            string unreachableLine = CatalogText.FetchFailed(unreachable, at, "Check for modules online");
            Check("a catalog that was reached and refused says so, and says the fault is the published catalog's (" + refusedLine + ")",
                  refusedLine.StartsWith("✗ The catalog was reached at ", StringComparison.Ordinal) &&
                  refusedLine.Contains("but could not be read: catalog.json is not valid JSON (x). ") &&
                  refusedLine.Contains("not in your install"));
            Check("WITNESS a catalog that could not be reached says that, and points at the connection (" + unreachableLine + ")",
                  unreachableLine.StartsWith("✗ Couldn't reach the catalog at ", StringComparison.Ordinal) &&
                  unreachableLine.Contains(": No such host is known. (raw.githubusercontent.com:443). Check your connection") &&
                  !unreachableLine.Contains("was reached"));
            string answeredLine = CatalogText.FetchFailed(new System.Net.Http.HttpRequestException(
                "Response status code does not indicate success: 429 (Too Many Requests).", null,
                System.Net.HttpStatusCode.TooManyRequests), at, "Check for modules online");
            Check("a server that answered with an error status is not sent to check the connection (" + answeredLine + ")",
                  answeredLine.StartsWith("✗ Couldn't get the catalog at ", StringComparison.Ordinal) &&
                  answeredLine.Contains(": Response status code does not indicate success: 429 (Too Many Requests). The server answered") &&
                  !answeredLine.Contains("Check your connection"));
            Check("a refusal is still told apart when it arrives wrapped",
                  CatalogText.IsRefusal(new InvalidOperationException("outer", refusedWhole)) &&
                  !CatalogText.IsRefusal(new TimeoutException("Catalog download exceeded its end-to-end deadline.")) &&
                  !CatalogText.IsRefusal(new InvalidDataException("Downloaded content failed SHA-256 verification.")));
            string tls = CatalogText.Reason(new System.Net.Http.HttpRequestException(
                "The SSL connection could not be established, see inner exception.",
                new IOException("The remote certificate is invalid because of errors in the certificate chain")));
            Check("a failure that only points at its inner exception takes the inner one's words (" + tls + ")",
                  tls == "The SSL connection could not be established: The remote certificate is invalid because of errors in the certificate chain");
            var five = new List<CatalogRejection>();
            for (int i = 0; i < 5; i++)
                five.Add(new CatalogRejection { Kind = CatalogRejection.Pack, Index = i, Id = "p" + i, Rule = "has no name" });
            string oneNote = CatalogText.Refused(five.GetRange(0, 1), at, "Check for modules online", 3);
            string fiveNote = CatalogText.Refused(five, at, "Check for modules online", 3);
            Check("the refusal note names the entry and the rule, and says it is the catalog's fault and not the install's (" + oneNote + ")",
                  oneNote.StartsWith("⚠ The catalog was read at ", StringComparison.Ordinal) &&
                  oneNote.Contains(", but 1 entry in it was published wrong and is not offered: pack \"p0\": has no name. ") &&
                  oneNote.Contains("not in your install"));
            Check("the refusal note lists three entries and counts the rest (" + fiveNote + ")",
                  fiveNote.Contains("5 entries in it were") && fiveNote.Contains("pack \"p2\": has no name; and 2 more") &&
                  !fiveNote.Contains("\"p3\""));
            // THE MODULES PANE'S PROBLEM PANEL (the owner's mockup M2): the case in its title, the rows, whose fault.
            Func<string, string> installed = delegate (string id) { return id == "agentflow" ? "1.4.2" : null; };
            var agentflowRefused = new CatalogRejection
            {
                Kind = CatalogRejection.Module, Index = 2, Id = "agentflow", Name = "AgentFlow", Version = "1.5.0",
                Rule = "description is 1049 characters, the limit is 1024",
            };
            CatalogProblem oneSkipped = CatalogText.ProblemForRefusals(
                new List<CatalogRejection> { agentflowRefused }, at, "when this pane opened", installed);
            string oneSkippedText = oneSkipped == null ? "(no panel)" : oneSkipped.ForCopy();
            Check("the panel for one skipped module names it with its name and version, the rule, when, and what keeps running (" +
                  Echo(oneSkippedText) + ")",
                  oneSkipped != null && oneSkipped.Warning &&
                  oneSkippedText.StartsWith("1 catalog entry was skipped; everything else works" + Environment.NewLine +
                      "Entry: module “agentflow” (AgentFlow 1.5.0)" + Environment.NewLine +
                      "Rule: description is 1049 characters, the limit is 1024" + Environment.NewLine + "When: Today at ",
                      StringComparison.Ordinal) &&
                  oneSkippedText.EndsWith(", when this pane opened." + Environment.NewLine +
                      "What this means: Published wrong, not your install. AgentFlow keeps running v1.4.2; its update appears once the entry is fixed.",
                      StringComparison.Ordinal));
            CatalogProblem notInstalled = CatalogText.ProblemForRefusals(
                new List<CatalogRejection> { agentflowRefused }, at, "when this pane opened", delegate { return null; });
            Check("the panel for a skipped module that is not installed says it is not offered until fixed (" +
                  Echo(notInstalled == null ? "" : notInstalled.ForLog()) + ")",
                  notInstalled != null && notInstalled.ForCopy().EndsWith(
                      "What this means: Published wrong, not your install. It is not offered until the entry is fixed; everything else in the catalog is.",
                      StringComparison.Ordinal));
            CatalogProblem fiveSkipped = CatalogText.ProblemForRefusals(five, at, "when this pane opened", installed);
            string fiveText = fiveSkipped == null ? "" : fiveSkipped.ForCopy();
            Check("the panel for five skipped entries lists three, counts the rest, and says none of it is the install's fault (" +
                  Echo(fiveSkipped == null ? "" : fiveSkipped.ForLog()) + ")",
                  fiveSkipped != null && fiveSkipped.Title == "5 catalog entries were skipped; everything else works" &&
                  fiveText.Contains("Entry: pack “p2”" + Environment.NewLine + "Rule: has no name" + Environment.NewLine +
                                    "And: 2 more, listed in the diagnostic log") &&
                  !fiveText.Contains("“p3”") && fiveText.Contains("Published wrong, not your install."));
            Check("WITNESS no refused entry, no panel",
                  CatalogText.ProblemForRefusals(new List<CatalogRejection>(), at, "x", installed) == null);
            CatalogProblem wholeRefused = CatalogText.ProblemForFailure(refusedWhole, at, "when this pane opened");
            CatalogProblem answered = CatalogText.ProblemForFailure(new System.Net.Http.HttpRequestException(
                "Response status code does not indicate success: 503 (Service Unavailable).", null,
                System.Net.HttpStatusCode.ServiceUnavailable), at, "when this pane opened");
            CatalogProblem notReached = CatalogText.ProblemForFailure(unreachable, at, "when this pane opened");
            Check("the panel for a catalog reached and refused is red, says nothing can be installed or updated, gives the rule and whose fault (" +
                  Echo(wholeRefused.ForLog()) + ")",
                  !wholeRefused.Warning &&
                  wholeRefused.Title == "The module catalog was refused, so nothing can be installed or updated right now" &&
                  wholeRefused.ForCopy().Contains(Environment.NewLine + "Rule: catalog.json is not valid JSON (x)" + Environment.NewLine) &&
                  wholeRefused.ForCopy().EndsWith("What this means: Published wrong, not your install. Nothing on this PC needs fixing; the next catalog publish clears it.",
                      StringComparison.Ordinal));
            Check("the panel for a server error status says GitHub answered, and not the connection (" + Echo(answered.ForLog()) + ")",
                  answered.Title == "Couldn't get the catalog" &&
                  answered.ForCopy().Contains("What failed: Response status code does not indicate success: 503 (Service Unavailable)") &&
                  answered.ForCopy().Contains("GitHub answered with an error, so your connection works"));
            Check("WITNESS the panel for a catalog never reached says so, and blames the connection or GitHub, not the catalog (" +
                  Echo(notReached.ForLog()) + ")",
                  !notReached.Warning && notReached.Title == "Couldn't reach the catalog" &&
                  notReached.ForCopy().Contains("What failed: No such host is known. (raw.githubusercontent.com:443)") &&
                  notReached.ForCopy().Contains("Your connection or GitHub, not the catalog and not your install.") &&
                  !notReached.ForCopy().Contains("Published wrong"));
            Check("an entry named by its position or a whole list reads as such in the panel (" +
                  CatalogText.EntryText(new CatalogRejection { Kind = CatalogRejection.Module, Index = 1, Id = "", Rule = "r" }) + "; " +
                  CatalogText.EntryText(new CatalogRejection { Kind = CatalogRejection.Module, Index = -1, Id = "", Rule = "r" }) + ")",
                  CatalogText.EntryText(new CatalogRejection { Kind = CatalogRejection.Module, Index = 1, Id = "", Rule = "r" }) == "module entry 2" &&
                  CatalogText.EntryText(new CatalogRejection { Kind = CatalogRejection.Module, Index = -1, Id = "", Rule = "r" }) == "the modules list");

            string echoed = CatalogText.Echo("a‮b​c\u0007\"" + new string('z', 50), 10);
            Check("Echo shows bidi, zero-width and control characters as '?', a quote as an apostrophe, and stops at its bound (" +
                  echoed + ")",
                  echoed == "a?b?c?'zzz…");

            // THE LOCAL ENUMERATION, whose two entry points must agree.
            //
            // CompanionCatalog.EnumerateLocalIds skips the per-pet header read that EnumerateLocal
            // does -- a file open and a 32768-char decoded read each -- for the four callers that
            // discard every DisplayName. The safety argument is that the ID SET is unchanged, so it is
            // asserted rather than taken on trust.
            //
            // ON A SYNTHETIC CORPUS, because the installed one is EMPTY here: the `companions/`
            // directory beside the exe is created by the packaging step, so a dev build and CI both
            // have none. The first version of this check compared the two against that empty corpus,
            // agreed vacuously, and still reported PASS when the fast path was given a deliberately
            // divergent traversal. It is also the only way to cover an id present in BOTH roots,
            // which never occurs in the shipped corpus and is exactly where the two resolution orders
            // are known to disagree.
            string enumRoot = null;
            try
            {
                enumRoot = Path.Combine(Path.GetTempPath(),
                    "dp-enum-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string bundled = Path.Combine(enumRoot, "bundled");
                string library = Path.Combine(enumRoot, "library");
                foreach (string pair in new string[] { "bundled|alpha", "bundled|shared",
                                                       "library|zulu", "library|shared" })
                {
                    string[] parts = pair.Split('|');
                    string dir = Path.Combine(parts[0] == "bundled" ? bundled : library, parts[1]);
                    Directory.CreateDirectory(dir);
                    // A header whose petname differs per root, so a precedence change is visible in
                    // the FULL enumeration as well as in the id list.
                    File.WriteAllText(Path.Combine(dir, "animations.xml"),
                        "<?xml version=\"1.0\"?><animations><header><petname>" +
                        parts[0] + "-" + parts[1] + "</petname></header></animations>");
                }
                // A directory with no animations.xml must be ignored by BOTH paths.
                Directory.CreateDirectory(Path.Combine(library, "empty"));

                var full = new List<string>();
                string sharedName = null;
                foreach (CompanionCatalog.CompanionInfo info in
                         CompanionCatalog.EnumerateFrom(bundled, library, true))
                {
                    if (info.IsBuiltIn || string.IsNullOrEmpty(info.Id)) continue;
                    full.Add(info.Id);
                    if (string.Equals(info.Id, "shared", StringComparison.Ordinal)) sharedName = info.DisplayName;
                }
                List<string> idsOnly = CompanionCatalog.EnumerateIdsFrom(bundled, library);

                bool same = full.Count == idsOnly.Count;
                if (same)
                    for (int i = 0; i < full.Count; i++)
                        if (!string.Equals(full[i], idsOnly[i], StringComparison.Ordinal)) { same = false; break; }
                Check("the two local enumerations agree: [" + string.Join(",", full.ToArray()) + "] vs [" +
                      string.Join(",", idsOnly.ToArray()) + "]", same);
                // STATES THE DISCREPANCY, not a cause. This used to assert one explanation ("a
                // directory with no animations.xml must be ignored") and two of the three ways to
                // reach it have nothing to do with that: a flipped traversal order yields 2 and a
                // dropped seen-dedup yields 4. Listing what was found sends the reader to the
                // right place without guessing.
                Check("the synthetic corpus yields exactly alpha, shared, zulu (got " + full.Count + ": [" +
                      string.Join(",", full.ToArray()) + "]; if not, candidates are the bundled/library walk order, " +
                      "the seen-dedup, the animations.xml existence test, or the 256-pet cap)", full.Count == 3);
                // WHICH ROOT WINS for a duplicated id, pinned rather than left to drift. Bundled
                // first, because AddFrom walks bundled-then-library with a `seen` set.
                Check("for an id in both roots the BUNDLED copy wins (got '" + sharedName + "')",
                      sharedName == null || sharedName.IndexOf("bundled", StringComparison.Ordinal) >= 0);
            }
            catch (Exception ex)
            {
                Check("the two local enumerations agree: the comparison threw " + ex.Message, false);
            }
            finally
            {
                try { if (enumRoot != null && Directory.Exists(enumRoot)) Directory.Delete(enumRoot, true); }
                catch { }
            }

            report.AppendLine("catalog_parse=" + (ok ? "PASS" : "FAIL"));
            report.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "dp-catalog-selftest.txt");
                File.WriteAllText(path, report.ToString());
            }
            catch { }
            // To stdout as well, the hardening self-test's shape (F348): a red run's FAIL lines then reach the
            // runner's log, not only %TEMP%.
            Console.Out.Write(report.ToString());
            return ok;
        }

        /// <summary>A refusal or exception text shown inside a self-test label: one line, bounded.</summary>
        private static string Echo(string text)
        {
            return CatalogText.Echo((text ?? "").Replace("\r", " ").Replace("\n", " "), 200);
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
