using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopAICompanion
{
        /// <summary>
        /// What a log line is about. Deliberately a SHORT fixed list rather than one switch per call site:
        /// a checkbox per log statement means every new line needs UI, the pane grows without bound, and
        /// most entries mean nothing to anyone who did not write that line. Six categories cover the
        /// question a user actually asks -- "which part of the app do I care about right now" -- and
        /// per-module filtering is handled separately, keyed on the module id the host already knows.
        /// </summary>
    internal enum LogCategory
    {
        /// <summary>Launch, settings, updates, and anything without a better home.</summary>
        App = 0,
        /// <summary>Companion staging, spawning, reload, catalog freshness.</summary>
        Companions = 1,
        /// <summary>Module load, init, shutdown, and modules' own IHost.Log lines.</summary>
        Modules = 2,
        /// <summary>The notification-area icon and its promotion.</summary>
        Tray = 3,
        /// <summary>Catalog fetches, downloads, update checks.</summary>
        Network = 4,
        /// <summary>Sound output and device selection.</summary>
        Audio = 5,
        /// <summary>Per-frame animation churn. Off by default: it repeats forever and would bury
        /// everything else, but a companion author debugging a skin wants exactly this.</summary>
        Animation = 6,
    }

        /// <summary>
        /// An always-available record of the same stream the debug window shows.
        ///
        /// The debug window (hold SHIFT at launch) has always carried what is needed to explain a startup
        /// fault, and StartUp.AddDebugInfo drops every line when that window is closed -- so the information
        /// existed only if you already knew you were about to hit the bug. That is backwards, and it cost a
        /// full investigation into an intermittent missing tray icon that left no evidence behind.
        ///
        /// ROTATION MATTERS MORE THAN IT LOOKS. A fault like a missing tray icon leaves the app running but
        /// unreachable, so the first thing anyone does is restart it -- destroying the only record unless
        /// the previous run is kept. Hence N files, default 2, meaning "this run and the one before".
        ///
        /// Best-effort throughout: a diagnostic that can break the app is worse than no diagnostic, so every
        /// path swallows its exceptions and a failure to log is simply silence.
        /// </summary>
    internal static class DiagnosticLog
    {
        private const string BaseName = "diagnostics";
        private const int MinimumKilobytes = 16;
        private const int MaximumKilobytes = 65536;   // 64 MB, well past useful; a guard, not a target
        private const int MinimumKeep = 1;
        private const int MaximumKeep = 20;

        private static readonly object Sync = new object();
        private static string _directory;
        private static bool _initialised;

        // Snapshot of the user's choices, refreshed by Configure. Read under Sync on every write, so a
        // change in Preferences takes effect on the next line rather than the next launch.
        private static bool _enabled = true;
        private static long _maxBytes = 512 * 1024;
        private static int _keep = 2;
        private static HashSet<LogCategory> _mutedCategories = new HashSet<LogCategory>();
        private static HashSet<string> _mutedModules =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static string CurrentPath
        {
            get { lock (Sync) { return _directory == null ? "" : Path.Combine(_directory, BaseName + ".log"); } }
        }

        /// <summary>
        /// Rotate and begin a new file. Called once, early, before anything worth recording happens.
        /// </summary>
        internal static void Start()
        {
            lock (Sync)
            {
                if (_initialised) return;
                _initialised = true;
                try
                {
                    string root = AppPaths.DataRoot;
                    if (string.IsNullOrWhiteSpace(root)) return;
                    Directory.CreateDirectory(root);
                    _directory = root;
                    RotateNoLock();
                }
                catch (Exception) { _directory = null; }
            }
            Write(LogCategory.App, "info", null,
                "--- " + SafeProductName() + " " + SafeProductVersion() + " starting, " +
                DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + " ---");
        }

        /// <summary>
        /// Apply the user's settings. Safe to call repeatedly; Preferences calls it on Apply so a change
        /// takes effect immediately rather than at the next launch, which is the whole point when the thing
        /// being diagnosed is a launch.
        /// </summary>
        internal static void Configure(bool enabled, int maxKilobytes, int keep,
                                       string mutedCategories, string mutedModules)
        {
            lock (Sync)
            {
                _enabled = enabled;
                _maxBytes = 1024L * Math.Min(MaximumKilobytes, Math.Max(MinimumKilobytes, maxKilobytes));
                _keep = Math.Min(MaximumKeep, Math.Max(MinimumKeep, keep));
                _mutedCategories = ParseCategories(mutedCategories);
                _mutedModules = ParseIds(mutedModules);
                // Animation churn is muted unless the user asks for it: it repeats every few seconds for as
                // long as the app runs and would fill the cap during ordinary use, pushing the startup
                // record -- the part that explains a launch fault -- out of a file nobody reads until after.
                if (!WasNamed(mutedCategories, LogCategory.Animation) &&
                    !WasUnmuted(mutedCategories, LogCategory.Animation))
                    _mutedCategories.Add(LogCategory.Animation);
            }
        }

        /// <summary>
        /// Whether POLICY would record this line: the master switch, the category, the module. Deliberately
        /// does NOT ask whether a log file is open, because those are different questions and conflating
        /// them makes the answer "no" for reasons the user did not choose -- and makes the filter untestable
        /// anywhere a file has not been created. Write() checks the file separately.
        /// </summary>
        internal static bool IsEnabled(LogCategory category, string moduleId)
        {
            lock (Sync)
            {
                if (!_enabled) return false;
                if (_mutedCategories.Contains(category)) return false;
                if (!string.IsNullOrEmpty(moduleId) && _mutedModules.Contains(moduleId)) return false;
                return true;
            }
        }

        /// <summary>Append one line. Never throws; silence is the failure mode.</summary>
        internal static void Write(LogCategory category, string level, string moduleId, string text)
        {
            lock (Sync)
            {
                // ONE filter, and it is IsEnabled. This method used to repeat the three checks itself, which
                // meant the tests -- which can only reach IsEnabled -- proved nothing about what actually
                // reached the file: deleting the per-module check here left every assertion passing. The lock
                // is re-entrant, so calling IsEnabled from inside it is fine, and a source invariant keeps
                // the copy from growing back.
                if (_directory == null) return;
                if (!IsEnabled(category, moduleId)) return;
                try
                {
                    string path = Path.Combine(_directory, BaseName + ".log");
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length >= _maxBytes) RotateNoLock();

                    // PadRight(9), not (7). The longest DEBUG_TYPE name is "warning", which is exactly 7
                    // characters, so a 7-wide column padded it to nothing and the real log read
                    // "warningApp" with the level welded to the category. Column widths have to exceed
                    // the longest value, not equal it.
                    string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                                  "  " + (level ?? "info").PadRight(9) +
                                  category.ToString().PadRight(11) +
                                  (text ?? "") + Environment.NewLine;
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
                catch (Exception) { /* a diagnostic must never break the thing it observes */ }
            }
        }

        /// <summary>
        /// Shift diagnostics.log -> .1.log -> .2.log ... discarding the oldest. Called at launch and again
        /// whenever the current file reaches the cap, so a long-running session still keeps recent history
        /// instead of going silent the way a hard cap would.
        /// </summary>
        private static void RotateNoLock()
        {
            if (_directory == null) return;
            try
            {
                string Current(int i) { return Path.Combine(_directory, i == 0 ? BaseName + ".log" : BaseName + "." + i + ".log"); }
                // Drop anything beyond what the user asked to keep, including files left by a larger setting.
                for (int i = _keep; i <= MaximumKeep; i++)
                    try { if (File.Exists(Current(i))) File.Delete(Current(i)); } catch (Exception) { }
                for (int i = _keep - 1; i >= 1; i--)
                {
                    try
                    {
                        if (!File.Exists(Current(i - 1))) continue;
                        if (File.Exists(Current(i))) File.Delete(Current(i));
                        File.Move(Current(i - 1), Current(i));
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        private static bool WasNamed(string list, LogCategory c)
        {
            return ParseCategories(list).Contains(c);
        }

        /// <summary>A leading '-' on a category means "explicitly ON", which is how Animation is opted in
        /// without inverting the meaning of the whole muted list.</summary>
        private static bool WasUnmuted(string list, LogCategory c)
        {
            foreach (string part in Split(list))
                if (part.StartsWith("-", StringComparison.Ordinal) &&
                    string.Equals(part.Substring(1), c.ToString(), StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static HashSet<LogCategory> ParseCategories(string list)
        {
            var set = new HashSet<LogCategory>();
            foreach (string part in Split(list))
            {
                if (part.StartsWith("-", StringComparison.Ordinal)) continue;
                foreach (LogCategory c in Enum.GetValues(typeof(LogCategory)))
                    if (string.Equals(part, c.ToString(), StringComparison.OrdinalIgnoreCase)) set.Add(c);
            }
            return set;
        }

        private static HashSet<string> ParseIds(string list)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in Split(list)) set.Add(part);
            return set;
        }

        private static IEnumerable<string> Split(string list)
        {
            if (string.IsNullOrWhiteSpace(list)) yield break;
            foreach (string raw in list.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim();
                if (t.Length > 0) yield return t;
            }
        }

        /// <summary>
        /// Ordered prefix map, checked top to bottom, first match wins. Built by reading the actual
        /// AddDebugInfo literals in this codebase rather than by guessing at shapes -- the first version of
        /// this method was guessed, and on a real startup it put 43 of 53 lines in App, including all 35
        /// sound-staging lines and every animation-graph line. Order matters where prefixes overlap, which
        /// is why this is a list and not a dictionary.
        /// </summary>
        private static readonly KeyValuePair<string, LogCategory>[] Prefixes = new[]
        {
            // Explicit tags first: a caller that already said which subsystem it is wins outright.
            new KeyValuePair<string, LogCategory>("[module]", LogCategory.Modules),
            new KeyValuePair<string, LogCategory>("[companions]", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("[pets]", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("tray icon", LogCategory.Tray),
            new KeyValuePair<string, LogCategory>("module host init failed", LogCategory.Modules),
            new KeyValuePair<string, LogCategory>("drop triggers init failed", LogCategory.Modules),

            // Audio. "adding sound" is the single noisiest line at startup -- 35 of them on this machine --
            // and the original contains-check for "audio"/"sound device" matched none of them.
            new KeyValuePair<string, LogCategory>("adding sound", LogCategory.Audio),
            new KeyValuePair<string, LogCategory>("can't open sound", LogCategory.Audio),

            // Animation churn. This block is load-bearing: Animation is muted by default, so anything that
            // belongs here and is not listed gets logged anyway under App, and the mute silently fails to
            // suppress the thing it exists to suppress.
            //
            // "no animations for this pet" is deliberately NOT here -- see the Companions block. It reads
            // like animation churn and is actually a load failure the user has to be able to see.
            new KeyValuePair<string, LogCategory>("new animation", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("animation is over", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("adding animation", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("unable to add animation", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("no next animation", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("adding spawn", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("adding child", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("removing child", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("border detected", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("gravity detected", LogCategory.Animation),

            // Companion lifecycle. Includes the failures that LOOK like animation churn but must stay
            // visible with Animation muted, because they explain a companion that never appeared.
            new KeyValuePair<string, LogCategory>("no animations for", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("new pet", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("pet '", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("preview pet", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("max pets reached", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("kill one sheep", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("killing all sheeps", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("synchronize sheeps", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("top most all sheeps", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("load new xml", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("spawn probabilities", LogCategory.Companions),
            new KeyValuePair<string, LogCategory>("no eligible positive-probability", LogCategory.Companions),
        };

        /// <summary>
        /// Checked after <see cref="Prefixes"/>, matched ANYWHERE in the line. This exists because several
        /// call sites interpolate a value before the words -- "304 shared frames ready" leads with the
        /// count -- so no prefix can ever match them. That is not hypothetical: "shared frames" was put in
        /// the prefix table first and the assertion for it failed on the very next run.
        /// </summary>
        private static readonly KeyValuePair<string, LogCategory>[] Anywhere = new[]
        {
            new KeyValuePair<string, LogCategory>("shared frames", LogCategory.Animation),
            new KeyValuePair<string, LogCategory>("audio", LogCategory.Audio),
            new KeyValuePair<string, LogCategory>("sound device", LogCategory.Audio),
            new KeyValuePair<string, LogCategory>("catalog", LogCategory.Network),
            new KeyValuePair<string, LogCategory>("download", LogCategory.Network),
        };

        /// <summary>
        /// Which category a legacy AddDebugInfo line belongs to, from its text. The call sites pass no
        /// category and rewriting them all would be churn for no behavioural gain, so the wording the
        /// codebase already uses is read instead. Anything unrecognised is App, which is the safe
        /// direction: an uncategorised line is still recorded.
        /// </summary>
        internal static LogCategory Infer(string text)
        {
            if (string.IsNullOrEmpty(text)) return LogCategory.App;
            foreach (KeyValuePair<string, LogCategory> entry in Prefixes)
                if (text.StartsWith(entry.Key, StringComparison.OrdinalIgnoreCase)) return entry.Value;
            foreach (KeyValuePair<string, LogCategory> entry in Anywhere)
                if (text.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) >= 0) return entry.Value;
            return LogCategory.App;
        }

        private static string SafeProductName()
        {
            try { return System.Windows.Forms.Application.ProductName; } catch (Exception) { return "app"; }
        }

        private static string SafeProductVersion()
        {
            try { return System.Windows.Forms.Application.ProductVersion; } catch (Exception) { return "?"; }
        }
    }
}
