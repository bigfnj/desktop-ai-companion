using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DesktopAICompanion.Ai
{
    /// <summary>One tagged fortune in the version-2 six-column taxonomy.</summary>
    internal struct FortuneEntry
    {
        public string Source;   // origin collection id (built-in file name, or a user file name)
        public string Topic;    // subject: one of FortuneTaxonomy's locked topics
        public string Genre;    // delivery style: one of FortuneTaxonomy's locked genres
        public string Level;    // general | edgy | nsfw
        public string Text;
        public bool   Prof;     // contains profanity
        public bool   Custom;   // loaded from the user's fortunes folder
    }

    /// <summary>A source collection as shown in the picker (aggregate over its entries).</summary>
    internal struct SourceStat
    {
        public string Id;
        public string Topic;
        public int    Count;
        public bool   Custom;
        public bool   HasSpicy;   // has any edgy/nsfw line
    }

    /// <summary>
    /// Locked fortune taxonomy and schema identifiers. Schema v2 is exactly:
    /// source, topic, genre, level, profanity flag, text. Schema v1 is the historical
    /// source/category/level/profanity/text layout and is accepted only by the named
    /// compatibility mapper in <see cref="FortuneProvider"/>.
    /// </summary>
    internal static class FortuneTaxonomy
    {
        public const int LegacySchemaVersion = 1;
        public const int CurrentSchemaVersion = 2;
        public const string TaxonomyVersion = "2026-07-31";

        private static readonly HashSet<string> TopicSet = new HashSet<string>(
            new[] { "tech", "science", "work-money", "love", "family", "faith",
                    "society", "food", "nature", "arts", "health-body", "life" },
            StringComparer.Ordinal);

        private static readonly HashSet<string> GenreSet = new HashSet<string>(
            new[] { "tv-quote", "observation", "joke", "pun", "quip", "aphorism",
                    "wisdom", "fact", "insult", "verse", "dark", "uplifting" },
            StringComparer.Ordinal);

        private static readonly HashSet<string> LevelSet = new HashSet<string>(
            new[] { "general", "edgy", "nsfw" }, StringComparer.Ordinal);

        public static bool IsTopic(string value) { return value != null && TopicSet.Contains(value); }
        public static bool IsGenre(string value) { return value != null && GenreSet.Contains(value); }
        public static bool IsLevel(string value) { return value != null && LevelSet.Contains(value); }

        public static string[] Topics()
        {
            var values = new string[TopicSet.Count];
            TopicSet.CopyTo(values);
            Array.Sort(values, StringComparer.Ordinal);
            return values;
        }
    }

    /// <summary>
    /// Resource bounds shared by custom-file ingestion, trusted-catalog admission, and downloads.
    /// These limits describe what the runtime can actually load from the fortunes directory; the
    /// independent tagged-content parser ceiling remains larger for the embedded corpus.
    /// </summary>
    internal static class FortunePackLoadPolicy
    {
        // Keep in sync with the base copy (src/dotNet/Ai/FortunePackLoadPolicy.cs), which validates catalog
        // entries while this one governs what actually loads off disk. 512 matches the catalog's per-kind
        // entry cap so installing every offered pack can't overflow the loader -- at 128 the full 152-pack
        // catalog silently dropped its last 24 files alphabetically (tv-simpsons among them). The real
        // memory bounds are the byte/entry caps below, which are unchanged.
        public const int MaximumFiles = 512;
        public const int MaximumFileBytes = 4 * 1024 * 1024;
        public const int MaximumTotalBytes = 16 * 1024 * 1024;
        public const int MaximumEntries = 100000;

        public static bool TryValidatePackMetadata(
            int bytes,
            int entries,
            out string error)
        {
            if (bytes < 1 || bytes > MaximumFileBytes)
            {
                error = "pack byte count is outside the runtime per-file limit";
                return false;
            }
            if (entries < 1 || entries > MaximumEntries)
            {
                error = "pack row count is outside the runtime entry limit";
                return false;
            }
            error = null;
            return true;
        }

    }

    /// <summary>
    /// The bundled fortunes (cowsay | fortune, but a sheep). Loads schema-v2 tagged data embedded
    /// in the exe, with an explicit schema-v1 compatibility mapper during migration, plus user
    /// <c>.txt</c> files from the canonical application data root. Filtering is fail-closed:
    /// disabled sources, the content level, and NoProfanity are never relaxed.
    /// </summary>
    internal sealed class FortuneProvider
    {
        private const int MaximumTaggedRows = FortunePackLoadPolicy.MaximumEntries;
        private const int MaximumTaggedLineCharacters = 1024;
        private const int MaximumTaggedContentCharacters = 16 * 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly List<FortuneEntry> _all  = new List<FortuneEntry>();
        private readonly List<string>       _pool = new List<string>();
        private readonly List<FortuneEntry> _poolE = new List<FortuneEntry>();   // filtered entries (for the smart picker)
        private readonly Random _rng = new Random();
        private readonly List<int> _bag = new List<int>();   // shuffle-bag: shuffled pool indices not yet drawn since the last refill
        private int _last = -1;

        private sealed class CustomLoadLimits
        {
            public int Files;
            public int FileBytes;
            public int TotalBytes;
            public int Entries;
        }

        private static readonly CustomLoadLimits DefaultCustomLoadLimits =
            new CustomLoadLimits {
                Files = FortunePackLoadPolicy.MaximumFiles,
                FileBytes = FortunePackLoadPolicy.MaximumFileBytes,
                TotalBytes = FortunePackLoadPolicy.MaximumTotalBytes,
                Entries = FortunePackLoadPolicy.MaximumEntries
            };

        public FortuneProvider(FortuneSettings s)
        {
            LoadStandardCorpus(_all);
            Rebuild(s ?? new FortuneSettings());
        }

        /// <summary>In-memory constructor used by deterministic diagnostics.</summary>
        internal FortuneProvider(IEnumerable<FortuneEntry> entries, FortuneSettings s)
        {
            if (entries != null) _all.AddRange(entries);
            Rebuild(s ?? new FortuneSettings());
        }

        public int Count { get { return _pool.Count; } }

        /// <summary>Folder where users drop their own <c>.txt</c> fortune files.</summary>
        public static string CustomDir
        {
            get { return FortunePaths.FortunesDir; }
        }

        /// <summary>
        /// A random fortune drawn without replacement: every line in the pool is shown once before any
        /// repeats. A shuffle-bag holds a fresh permutation of the pool; Pick() hands out its tail and
        /// reshuffles only once it empties, so the whole corpus is exhausted before a line can recur —
        /// instead of independent draws, which by the birthday paradox repeat far sooner than the pool
        /// size suggests. The bag boundary is guarded so a refill can't repeat the immediately previous
        /// line. "" if the pool is empty.
        /// </summary>
        public string Pick()
        {
            int n = _pool.Count;
            if (n == 0) return "";
            if (n == 1) return _pool[0];
            if (_bag.Count == 0) RefillBag(n);
            int i = _bag[_bag.Count - 1];
            _bag.RemoveAt(_bag.Count - 1);
            _last = i;
            return _pool[i];
        }

        // Refill the shuffle-bag with a fresh Fisher-Yates permutation of [0, n). Pick() draws from the
        // tail, so the element handed out first is _bag[n-1]; when it equals the previous pick we swap it
        // to the front, keeping the "never repeat the immediately previous line" guarantee across the seam
        // between one bag and the next.
        private void RefillBag(int n)
        {
            _bag.Clear();
            for (int k = 0; k < n; k++) _bag.Add(k);
            for (int k = n - 1; k > 0; k--)
            {
                int j = _rng.Next(k + 1);
                int tmp = _bag[k]; _bag[k] = _bag[j]; _bag[j] = tmp;
            }
            if (n >= 2 && _bag[n - 1] == _last)
            {
                int tmp = _bag[n - 1]; _bag[n - 1] = _bag[0]; _bag[0] = tmp;
            }
        }

        // ---- pool building --------------------------------------------------

        /// <summary>
        /// The content tiers a level admits. An unknown persisted value fails CLOSED to tame-only content
        /// rather than accidentally admitting NSFW lines.
        /// </summary>
        internal static HashSet<string> LevelsFor(string contentLevel)
        {
            var levels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            switch (contentLevel)
            {
                case ContentLevels.CleanEdgy:
                    levels.Add("general");
                    levels.Add("edgy");
                    break;
                case ContentLevels.Everything:
                    levels.Add("general");
                    levels.Add("edgy");
                    levels.Add("nsfw");
                    break;
                case ContentLevels.SpicyOnly:
                    levels.Add("edgy");
                    levels.Add("nsfw");
                    break;
                default:
                    levels.Add("general");
                    break;
            }
            return levels;
        }

        private void Rebuild(FortuneSettings s)
        {
            HashSet<string> levels = LevelsFor(s.ContentLevel);

            var disabled = new HashSet<string>(
                s.DisabledSources ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var disabledGenres = new HashSet<string>(
                s.DisabledGenres ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            // Every user selection is a hard constraint. An impossible combination intentionally
            // produces an empty pool.
            Select(levels, s.NoProfanity, disabled, disabledGenres);
            // If empty, Pick() returns "" and the pet stays silent.
        }

        private void Select(HashSet<string> levels, bool noProf, HashSet<string> disabled, HashSet<string> disabledGenres)
        {
            _pool.Clear();
            _poolE.Clear();
            _bag.Clear();
            _last = -1;
            var seenText = new HashSet<string>(StringComparer.Ordinal);
            foreach (FortuneEntry e in _all)
            {
                if (!FortuneTaxonomy.IsLevel(e.Level) || !levels.Contains(e.Level)) continue;
                if (noProf && e.Prof) continue;
                if (disabled != null && disabled.Contains(e.Source)) continue;
                if (disabledGenres != null && disabledGenres.Contains(e.Genre)) continue;
                // Dedupe only after every hard filter so an ineligible earlier occurrence cannot
                // suppress a later eligible one. HashSet.Add preserves first-eligible precedence.
                if (!seenText.Add(e.Text)) continue;
                _pool.Add(e.Text);
                _poolE.Add(e);
            }
        }

        /// <summary>The active (filtered) pool as entries, for the smart/contextual picker.</summary>
        public List<FortuneEntry> PoolEntries() { return _poolE; }

        internal static List<FortuneEntry> EmbeddedEntriesForDiagnostics()
        {
            return new List<FortuneEntry>(EmbeddedCorpus());
        }

        /// <summary>
        /// Diagnostic (`--filter-selftest`): exhaustively prove hard content/source selection and
        /// validate current, legacy, malformed, and mixed-schema tagged data.
        /// </summary>
        public static bool FilterSelfTest()
        {
            string outp = Path.Combine(Path.GetTempPath(), "dp-filter-selftest.txt");
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                var entries = new List<FortuneEntry>();
                AddDiagnosticEntries(entries, "A", "general");
                AddDiagnosticEntries(entries, "B", "edgy");
                AddDiagnosticEntries(entries, "C", "nsfw");

                // Every content level plus an unrecognized one (which must fail closed to tame-only).
                string[] contentLevels =
                {
                    ContentLevels.Clean, ContentLevels.CleanEdgy,
                    ContentLevels.Everything, ContentLevels.SpicyOnly, "invalid"
                };
                int cases = 0;
                int failures = 0;
                for (int levelIndex = 0; levelIndex < contentLevels.Length; levelIndex++)
                for (int profIndex = 0; profIndex < 2; profIndex++)
                for (int disabledIndex = 0; disabledIndex < 3; disabledIndex++)
                for (int disabledGenreIndex = 0; disabledGenreIndex < 3; disabledGenreIndex++)
                {
                    var settings = new FortuneSettings {
                        ContentLevel = contentLevels[levelIndex],
                        NoProfanity = profIndex == 1,
                        DisabledSources = DiagnosticDisabledSources(disabledIndex),
                        DisabledGenres = DiagnosticDisabledGenres(disabledGenreIndex)
                    };
                    var provider = new FortuneProvider(entries, settings);
                    var actual = provider.PoolEntries();
                    var expected = new List<FortuneEntry>();
                    foreach (FortuneEntry entry in entries)
                        if (AllowedBySettings(entry, settings)) expected.Add(entry);

                    cases++;
                    bool same = actual.Count == expected.Count;
                    if (same)
                        for (int i = 0; i < actual.Count; i++)
                            if (!string.Equals(actual[i].Text, expected[i].Text, StringComparison.Ordinal))
                            { same = false; break; }

                    if (!same)
                    {
                        failures++;
                        sb.AppendLine("FILTER FAIL case=" + cases + " expected=" +
                            expected.Count + " actual=" + actual.Count);
                    }
                }

                var emptySettings = new FortuneSettings {
                    ContentLevel = ContentLevels.SpicyOnly,
                    NoProfanity = true,
                    DisabledSources = new List<string> { "A", "B", "C" }
                };
                var empty = new FortuneProvider(entries, emptySettings);
                if (empty.Count != 0 || empty.Pick() != "")
                {
                    failures++;
                    sb.AppendLine("FILTER FAIL impossible constraints did not stay empty");
                }
                sb.AppendLine("filter_cases=" + cases + " failures=" + failures);
                ok = failures == 0;

                ok = RunDeduplicationSelfTest(sb) && ok;
                ok = RunClassifierParitySelfTest(sb) && ok;
                ok = RunParserSelfTest(sb) && ok;
                ok = RunCustomIngestionSelfTest(sb) && ok;
                ok = FortuneFileImporter.RunSelfTest(sb) && ok;
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
            }

            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(outp, sb.ToString()); }
            catch { return false; }
            return ok;
        }

        private static void AddDiagnosticEntries(List<FortuneEntry> entries, string source, string level)
        {
            string[] genres = { "quip", "joke" };
            foreach (string genre in genres)
            {
                entries.Add(new FortuneEntry {
                    Source = source, Topic = "life", Genre = genre, Level = level,
                    Prof = false, Text = source + "-" + level + "-" + genre + "-clean", Custom = false });
                entries.Add(new FortuneEntry {
                    Source = source, Topic = "life", Genre = genre, Level = level,
                    Prof = true, Text = source + "-" + level + "-" + genre + "-profane", Custom = false });
            }
        }

        private static List<string> DiagnosticDisabledSources(int mode)
        {
            if (mode == 1) return new List<string> { "B" };
            if (mode == 2) return new List<string> { "A", "B", "C" };
            return new List<string>();
        }

        private static List<string> DiagnosticDisabledGenres(int mode)
        {
            if (mode == 1) return new List<string> { "joke" };
            if (mode == 2) return new List<string> { "quip", "joke" };
            return new List<string>();
        }

        private static bool AllowedBySettings(FortuneEntry entry, FortuneSettings settings)
        {
            if (settings.NoProfanity && entry.Prof) return false;
            if (settings.DisabledSources != null &&
                settings.DisabledSources.Exists(delegate (string source) {
                    return string.Equals(source, entry.Source, StringComparison.OrdinalIgnoreCase);
                })) return false;
            if (settings.DisabledGenres != null &&
                settings.DisabledGenres.Exists(delegate (string genre) {
                    return string.Equals(genre, entry.Genre, StringComparison.OrdinalIgnoreCase);
                })) return false;

            // Deliberately spelled out per level rather than reusing LevelsFor, so this stays an INDEPENDENT
            // statement of the rule: a bug in LevelsFor must fail the comparison instead of being mirrored.
            switch (settings.ContentLevel)
            {
                case ContentLevels.CleanEdgy:
                    return IsLevel(entry, "general") || IsLevel(entry, "edgy");
                case ContentLevels.Everything:
                    return IsLevel(entry, "general") || IsLevel(entry, "edgy") || IsLevel(entry, "nsfw");
                case ContentLevels.SpicyOnly:
                    return IsLevel(entry, "edgy") || IsLevel(entry, "nsfw");
                default:
                    return IsLevel(entry, "general");   // Clean, and anything unrecognized (fails closed)
            }
        }

        private static bool IsLevel(FortuneEntry entry, string level)
        {
            return string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase);
        }

        private static bool RunDeduplicationSelfTest(StringBuilder sb)
        {
            const string disabledFirstText = "A disabled first copy with an eligible duplicate.";
            const string eligibleFirstText = "A first eligible copy with another eligible duplicate.";
            var entries = new List<FortuneEntry> {
                new FortuneEntry {
                    Source = "disabled-first", Topic = "life", Genre = "quip",
                    Level = "general", Prof = false, Text = disabledFirstText, Custom = false },
                new FortuneEntry {
                    Source = "eligible-later", Topic = "life", Genre = "quip",
                    Level = "general", Prof = false, Text = disabledFirstText, Custom = false },
                new FortuneEntry {
                    Source = "first-eligible", Topic = "life", Genre = "quip",
                    Level = "general", Prof = false, Text = eligibleFirstText, Custom = false },
                new FortuneEntry {
                    Source = "second-eligible", Topic = "life", Genre = "quip",
                    Level = "general", Prof = false, Text = eligibleFirstText, Custom = false }
            };
            var provider = new FortuneProvider(
                entries,
                new FortuneSettings {
                    ContentLevel = ContentLevels.Clean,
                    DisabledSources = new List<string> { "disabled-first" }
                });
            List<FortuneEntry> actual = provider.PoolEntries();
            bool ok = actual.Count == 2 &&
                string.Equals(actual[0].Text, disabledFirstText, StringComparison.Ordinal) &&
                string.Equals(actual[0].Source, "eligible-later", StringComparison.Ordinal) &&
                string.Equals(actual[1].Text, eligibleFirstText, StringComparison.Ordinal) &&
                string.Equals(actual[1].Source, "first-eligible", StringComparison.Ordinal);
            sb.AppendLine("deduplication=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static bool RunClassifierParitySelfTest(StringBuilder sb)
        {
            bool ok = true;
            string[] explicitCases = {
                "They have sex after dinner.",
                "She has sex with a willing adult.",
                "They had sex before breakfast.",
                "The couple is having sex upstairs.",
                "The ad solicits sex from strangers.",
                "This story describes bestiality.",
                "A self-described zoophile wrote it.",
                "The passage promotes zoophilia.",
                "The cocksucker shouted from the doorway.",
                "The report says the victim was raped.",
                "The joke mentions an erection.",
                "He complained about a boner.",
                "This story describes incest.",
                "The article discusses pedophilia.",
                "The passage describes necrophilia.",
                "The offender molested a victim.",
                "They attended an orgy.",
                "The report describes sexual assault."
            };
            foreach (string text in explicitCases)
            {
                string level;
                bool prof;
                FortuneClassifier.Classify(text, "plain-source", out level, out prof);
                if (!prof ||
                    !string.Equals(level, "nsfw", StringComparison.Ordinal))
                {
                    ok = false;
                    sb.AppendLine("CLASSIFIER PARITY FAIL explicit: " + text);
                }
            }

            string[] edgyCases = {
                "That loudmouth is a dickhead.",
                "The heckler called him a dickwad."
            };
            foreach (string text in edgyCases)
            {
                string level;
                bool prof;
                FortuneClassifier.Classify(text, "plain-source", out level, out prof);
                if (!prof ||
                    !string.Equals(level, "edgy", StringComparison.Ordinal))
                {
                    ok = false;
                    sb.AppendLine("CLASSIFIER PARITY FAIL edgy: " + text);
                }
            }

            string[] mixedCaseEdgySources = {
                "Carlin",
                "YO-MAMA"
            };
            foreach (string source in mixedCaseEdgySources)
            {
                string level;
                bool prof;
                FortuneClassifier.Classify(
                    "A clean source fixture with neutral wording.",
                    source,
                    out level,
                    out prof);
                if (prof ||
                    !string.Equals(level, "edgy", StringComparison.Ordinal))
                {
                    ok = false;
                    sb.AppendLine(
                        "CLASSIFIER PARITY FAIL mixed-case source: " + source);
                }
            }

            string[] neutralCases = {
                "Biological sex is one field in this dataset.",
                "The same-sex pairing won the dance contest.",
                "Sex education should be age appropriate.",
                "A sex chromosome carries genetic information.",
                "Dickinson wrote many poems.",
                "A cocktail umbrella decorated the drink.",
                "The rapper released a new track.",
                "Workers erect the frame carefully.",
                "The assessor assessed the property."
            };
            foreach (string text in neutralCases)
            {
                string level;
                bool prof;
                FortuneClassifier.Classify(text, "plain-source", out level, out prof);
                if (prof ||
                    !string.Equals(level, "general", StringComparison.Ordinal))
                {
                    ok = false;
                    sb.AppendLine("CLASSIFIER PARITY FAIL neutral: " + text);
                }
            }

            ok = RunSharedClassifierParityCases(sb) && ok;

            CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
            CultureInfo previousUiCulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                CultureInfo turkish = CultureInfo.GetCultureInfo("tr-TR");
                Thread.CurrentThread.CurrentCulture = turkish;
                Thread.CurrentThread.CurrentUICulture = turkish;

                string level;
                bool prof;
                FortuneClassifier.Classify(
                    "FISTING, PISS, TITS, DICKS, and SHIT",
                    "plain-source",
                    out level,
                    out prof);
                List<FortuneEntry> escalated;
                int schema;
                string error;
                bool taggedEscalated = TryParseTaggedContent(
                    "source-a\tlife\tquip\tgeneral\t0\tFISTING and SHIT",
                    false,
                    true,
                    out escalated,
                    out schema,
                    out error);
                var filtered = taggedEscalated
                    ? new FortuneProvider(
                        escalated,
                        new FortuneSettings
                        {
                            ContentLevel = ContentLevels.Clean,
                            NoProfanity = true
                        })
                    : null;
                if (!FortuneClassifier.UsesCanonicalUnicodeFold ||
                    !prof ||
                    !string.Equals(level, "nsfw", StringComparison.Ordinal) ||
                    !taggedEscalated ||
                    escalated.Count != 1 ||
                    !escalated[0].Prof ||
                    !string.Equals(
                        escalated[0].Level,
                        "nsfw",
                        StringComparison.Ordinal) ||
                    filtered.Count != 0)
                {
                    ok = false;
                    sb.AppendLine(
                        "CLASSIFIER PARITY FAIL Turkish-culture severity floor");
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previousCulture;
                Thread.CurrentThread.CurrentUICulture = previousUiCulture;
            }
            sb.AppendLine("classifier_parity=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static bool RunSharedClassifierParityCases(StringBuilder sb)
        {
            const string ResourceName = "DesktopAICompanion.ClassifierParity.tsv";
            const string Marker = "#!desktop-pet-classifier-parity-v1";
            const int ExpectedCaseCount = 18;
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                ResourceName);
            if (stream == null)
            {
                sb.AppendLine(
                    "CLASSIFIER PARITY FAIL shared fixture resource is missing");
                return false;
            }

            bool ok = true;
            int caseCount = 0;
            try
            {
                using (stream)
                using (var reader = new StreamReader(
                    stream,
                    StrictUtf8,
                    false,
                    4096,
                    false))
                {
                    string marker = reader.ReadLine();
                    if (!string.Equals(marker, Marker, StringComparison.Ordinal))
                    {
                        sb.AppendLine(
                            "CLASSIFIER PARITY FAIL shared fixture marker");
                        return false;
                    }

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        caseCount++;
                        string[] fields = line.Split('\t');
                        if (fields.Length != 5 ||
                            string.IsNullOrWhiteSpace(fields[0]) ||
                            string.IsNullOrWhiteSpace(fields[1]) ||
                            string.IsNullOrWhiteSpace(fields[4]) ||
                            (fields[2] != "general" &&
                             fields[2] != "edgy" &&
                             fields[2] != "nsfw") ||
                            (fields[3] != "0" && fields[3] != "1"))
                        {
                            ok = false;
                            sb.AppendLine(
                                "CLASSIFIER PARITY FAIL malformed shared case " +
                                caseCount.ToString(CultureInfo.InvariantCulture));
                            continue;
                        }

                        string actualLevel;
                        bool actualProf;
                        FortuneClassifier.Classify(
                            fields[4],
                            fields[1],
                            out actualLevel,
                            out actualProf);
                        bool expectedProf = fields[3] == "1";
                        if (!string.Equals(
                                actualLevel,
                                fields[2],
                                StringComparison.Ordinal) ||
                            actualProf != expectedProf)
                        {
                            ok = false;
                            sb.AppendLine(
                                "CLASSIFIER PARITY FAIL shared case: " +
                                fields[0]);
                        }
                    }
                }
            }
            catch (DecoderFallbackException)
            {
                sb.AppendLine(
                    "CLASSIFIER PARITY FAIL shared fixture is not strict UTF-8");
                return false;
            }

            if (caseCount != ExpectedCaseCount)
            {
                sb.AppendLine(
                    "CLASSIFIER PARITY FAIL shared fixture case count: " +
                    caseCount.ToString(CultureInfo.InvariantCulture));
                ok = false;
            }
            return ok;
        }

        private static bool RunParserSelfTest(StringBuilder sb)
        {
            bool ok = true;
            int rows, schema;
            string error;

            string v2 = "source-a\ttech\tquip\tgeneral\t0\tA valid version two fortune.";
            if (!TryValidateTaggedPack(v2, 1, out rows, out schema, out error) ||
                rows != 1 || schema != FortuneTaxonomy.CurrentSchemaVersion)
            {
                ok = false; sb.AppendLine("PARSER FAIL valid v2: " + error);
            }

            string v1 = "source-a\ttech\tgeneral\t0\tA valid legacy fortune.";
            if (!TryValidateTaggedPack(v1, 1, out rows, out schema, out error) ||
                rows != 1 || schema != FortuneTaxonomy.LegacySchemaVersion)
            {
                ok = false; sb.AppendLine("PARSER FAIL valid v1: " + error);
            }
            else
            {
                List<FortuneEntry> legacyEntries;
                if (!TryParseTaggedContent(v1, true, true, out legacyEntries, out schema, out error) ||
                    legacyEntries.Count != 1 || legacyEntries[0].Topic != "tech" ||
                    legacyEntries[0].Genre != "quip")
                {
                    ok = false; sb.AppendLine("PARSER FAIL legacy compatibility mapping");
                }
            }

            List<FortuneEntry> detectedEntries;
            if (ParseTaggedPackContent(v2, 2, out detectedEntries) != TaggedLoadResult.Loaded ||
                detectedEntries.Count != 1)
            {
                ok = false; sb.AppendLine("PARSER FAIL exact tagged shape was not detected");
            }
            if (ParseTaggedPackContent(v1, 2, out detectedEntries) !=
                    TaggedLoadResult.Loaded ||
                detectedEntries.Count != 1 ||
                detectedEntries[0].Topic != "tech")
            {
                ok = false; sb.AppendLine("PARSER FAIL unmarked legacy tagged compatibility");
            }

            string declaredV2 = TaggedFormatV2Declaration + "\n" + v2;
            if (ParseTaggedPackContent(declaredV2, 1, out detectedEntries) !=
                    TaggedLoadResult.Loaded ||
                detectedEntries.Count != 1 ||
                !TryValidateTaggedPack(
                    declaredV2,
                    1,
                    out rows,
                    out schema,
                    out error) ||
                schema != FortuneTaxonomy.CurrentSchemaVersion)
            {
                ok = false; sb.AppendLine("PARSER FAIL declared v2 content");
            }

            const string tabbedPlain =
                "An ordinary custom fortune\twith intentional horizontal spacing.";
            if (ParseTaggedPackContent(tabbedPlain, 2, out detectedEntries) !=
                    TaggedLoadResult.NotTagged ||
                !TryParsePlainContent(tabbedPlain, "plain-source", 2, out detectedEntries) ||
                detectedEntries.Count != 1 ||
                !string.Equals(
                    detectedEntries[0].Text,
                    "An ordinary custom fortune with intentional horizontal spacing.",
                    StringComparison.Ordinal))
            {
                ok = false; sb.AppendLine("PARSER FAIL ordinary tabbed plain text");
            }

            string[] exactWidthPlain = {
                "An ordinary plain fortune\tkeeps\tfour\ttab-separated\tphrases intact.",
                "Another ordinary plain fortune\tkeeps\tfive\tseparate\ttab stops\tintact today."
            };
            string[] exactWidthPlainExpected = {
                "An ordinary plain fortune keeps four tab-separated phrases intact.",
                "Another ordinary plain fortune keeps five separate tab stops intact today."
            };
            for (int index = 0; index < exactWidthPlain.Length; index++)
            {
                if (ParseTaggedPackContent(
                            exactWidthPlain[index],
                            2,
                            out detectedEntries) != TaggedLoadResult.NotTagged ||
                    !TryParsePlainContent(
                            exactWidthPlain[index],
                            "plain-source",
                            2,
                            out detectedEntries) ||
                    detectedEntries.Count != 1 ||
                    !string.Equals(
                        detectedEntries[0].Text,
                        exactWidthPlainExpected[index],
                        StringComparison.Ordinal))
                {
                    ok = false;
                    sb.AppendLine(
                        "PARSER FAIL exact-width plain text fixture " + (index + 1));
                }
            }

            const string metadataLookingPlain =
                "Ordinary advice\ttech\tquip\tkeeps\tthese\twords understandable.";
            if (ParseTaggedPackContent(
                        metadataLookingPlain,
                        2,
                        out detectedEntries) != TaggedLoadResult.NotTagged ||
                !TryParsePlainContent(
                    metadataLookingPlain,
                    "plain-source",
                    2,
                    out detectedEntries) ||
                detectedEntries.Count != 1)
            {
                ok = false;
                sb.AppendLine("PARSER FAIL metadata-looking plain prose was rejected");
            }

            string malformedTagged = TaggedFormatV2Declaration + "\n" +
                "source-a\ttech\tquip\tgeneral\t0\tA malformed tagged fortune.\textra";
            if (ParseTaggedPackContent(malformedTagged, 2, out detectedEntries) !=
                    TaggedLoadResult.Invalid ||
                detectedEntries.Count != 0)
            {
                ok = false;
                sb.AppendLine("PARSER FAIL malformed declared tagged content fell back to plain");
            }
            string unknownDeclaration =
                TaggedFormatDeclarationPrefix + "99\n" + v2;
            if (ParseTaggedPackContent(
                        unknownDeclaration,
                        2,
                        out detectedEntries) != TaggedLoadResult.Invalid)
            {
                ok = false; sb.AppendLine("PARSER FAIL unknown declaration fell back to plain");
            }
            string mismatchedDeclaration =
                TaggedFormatV1Declaration + "\n" + v2;
            if (TryValidateTaggedPack(
                    mismatchedDeclaration,
                    1,
                    out rows,
                    out schema,
                    out error))
            {
                ok = false; sb.AppendLine("PARSER FAIL accepted declaration/schema mismatch");
            }

            string mislabeledNonCustom =
                "source-a\tlife\tquip\tgeneral\t0\tPlease jack him off before the party.";
            if (!TryParseTaggedContent(
                    mislabeledNonCustom, false, true,
                    out detectedEntries, out schema, out error) ||
                detectedEntries.Count != 1 || detectedEntries[0].Custom ||
                !detectedEntries[0].Prof ||
                !string.Equals(detectedEntries[0].Level, "nsfw", StringComparison.Ordinal))
            {
                ok = false; sb.AppendLine("PARSER FAIL non-custom classification escalation");
            }

            string restrictiveMetadata =
                "source-a\tlife\tquip\tnsfw\t1\tA deliberately restricted clean fortune.";
            if (!TryParseTaggedContent(
                    restrictiveMetadata, false, true,
                    out detectedEntries, out schema, out error) ||
                detectedEntries.Count != 1 || !detectedEntries[0].Prof ||
                !string.Equals(detectedEntries[0].Level, "nsfw", StringComparison.Ordinal))
            {
                ok = false; sb.AppendLine("PARSER FAIL supplied severity was lowered");
            }

            string[] invalid = {
                "source-a\tunknown\tquip\tgeneral\t0\tInvalid topic fortune.",
                "source-a\tTECH\tquip\tgeneral\t0\tInvalid case topic fortune.",
                "source-a\ttech\tunknown\tgeneral\t0\tInvalid genre fortune.",
                "source-a\ttech\tquip\tunknown\t0\tInvalid level fortune.",
                "source-a\ttech\tquip\tgeneral\t2\tInvalid profanity fortune.",
                "source-a\ttech\tquip\tgeneral\t0\textra\tToo many columns.",
                "source-a\tunknown\tgeneral\t0\tUnknown legacy category.",
                v2 + "\n\n" + v2,
                v2 + "\n" + v1
            };
            for (int i = 0; i < invalid.Length; i++)
            {
                if (TryValidateTaggedPack(invalid[i], -1, out rows, out schema, out error))
                {
                    ok = false; sb.AppendLine("PARSER FAIL accepted invalid fixture " + (i + 1));
                }
            }
            if (TryValidateTaggedPack(v2, 2, out rows, out schema, out error))
            {
                ok = false; sb.AppendLine("PARSER FAIL accepted wrong expected row count");
            }
            byte[] invalidUtf8 = { 0x73, 0x72, 0x63, 0x09, 0xFF };
            if (TryValidateTaggedPack(invalidUtf8, -1, out rows, out schema, out error))
            {
                ok = false; sb.AppendLine("PARSER FAIL accepted invalid UTF-8");
            }

            List<FortuneEntry> limitedRows;
            string threeRows = v2 + "\n" + v2 + "\n" + v2;
            if (TryParseTaggedContent(
                    threeRows, true, true, 2,
                    out limitedRows, out schema, out error))
            {
                ok = false; sb.AppendLine("PARSER FAIL accepted rows above hard parser limit");
            }

            sb.AppendLine("parser=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static bool RunCustomIngestionSelfTest(StringBuilder sb)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-fortune-ingestion-" + Guid.NewGuid().ToString("N"));
            bool ok = true;
            try
            {
                Directory.CreateDirectory(root);
                var encoding = new UTF8Encoding(false, true);

                string validationDir = Path.Combine(root, "validation");
                Directory.CreateDirectory(validationDir);
                File.WriteAllText(
                    Path.Combine(validationDir, "a-valid.txt"),
                    "A valid custom fortune line.",
                    encoding);
                File.WriteAllBytes(
                    Path.Combine(validationDir, "b-invalid.txt"),
                    new byte[] { 0x41, 0xFF, 0x42 });
                File.WriteAllBytes(
                    Path.Combine(validationDir, "c-oversized.txt"),
                    new byte[129]);
                File.WriteAllText(
                    Path.Combine(validationDir, "d-tagged.txt"),
                    "source-a\ttech\tquip\tgeneral\t0\tA valid tagged custom fortune.",
                    encoding);
                var validationEntries = new List<FortuneEntry>();
                LoadCustomFromDirectory(
                    validationEntries,
                    validationDir,
                    new CustomLoadLimits {
                        Files = 4, FileBytes = 128, TotalBytes = 256, Entries = 8
                    });
                if (validationEntries.Count != 2)
                {
                    ok = false;
                    sb.AppendLine(
                        "CUSTOM FAIL strict validation count=" + validationEntries.Count);
                }

                string fileCapDir = Path.Combine(root, "file-cap");
                Directory.CreateDirectory(fileCapDir);
                for (int i = 0; i < 3; i++)
                    File.WriteAllText(
                        Path.Combine(fileCapDir, "valid-" + i + ".txt"),
                        "A valid capped fortune " + i + ".",
                        encoding);
                var fileCapEntries = new List<FortuneEntry>();
                LoadCustomFromDirectory(
                    fileCapEntries,
                    fileCapDir,
                    new CustomLoadLimits {
                        Files = 2, FileBytes = 128, TotalBytes = 256, Entries = 8
                    });
                if (fileCapEntries.Count != 2)
                {
                    ok = false;
                    sb.AppendLine("CUSTOM FAIL file cap count=" + fileCapEntries.Count);
                }

                string entryCapDir = Path.Combine(root, "entry-cap");
                Directory.CreateDirectory(entryCapDir);
                File.WriteAllText(
                    Path.Combine(entryCapDir, "too-many.txt"),
                    "First valid capped fortune.\nSecond valid capped fortune.\n" +
                    "Third valid capped fortune.",
                    encoding);
                var entryCapEntries = new List<FortuneEntry>();
                LoadCustomFromDirectory(
                    entryCapEntries,
                    entryCapDir,
                    new CustomLoadLimits {
                        Files = 1, FileBytes = 256, TotalBytes = 256, Entries = 2
                    });
                if (entryCapEntries.Count != 0)
                {
                    ok = false;
                    sb.AppendLine("CUSTOM FAIL partially accepted over-entry file");
                }

                string totalCapDir = Path.Combine(root, "total-cap");
                Directory.CreateDirectory(totalCapDir);
                const string equalContent = "One equal-sized valid fortune.";
                File.WriteAllText(
                    Path.Combine(totalCapDir, "one.txt"), equalContent, encoding);
                File.WriteAllText(
                    Path.Combine(totalCapDir, "two.txt"), equalContent, encoding);
                int oneFileBytes = encoding.GetByteCount(equalContent);
                var totalCapEntries = new List<FortuneEntry>();
                LoadCustomFromDirectory(
                    totalCapEntries,
                    totalCapDir,
                    new CustomLoadLimits {
                        Files = 2, FileBytes = 128,
                        TotalBytes = oneFileBytes * 2 - 1, Entries = 8
                    });
                if (totalCapEntries.Count != 1)
                {
                    ok = false;
                    sb.AppendLine("CUSTOM FAIL total-byte cap count=" + totalCapEntries.Count);
                }

                string classificationDir = Path.Combine(root, "classification");
                Directory.CreateDirectory(classificationDir);
                File.WriteAllText(
                    Path.Combine(classificationDir, "mislabeled.txt"),
                    "source-a\tlife\tquip\tgeneral\t0\t" +
                    "This shit was deliberately mislabeled.",
                    encoding);
                var classifiedEntries = new List<FortuneEntry>();
                LoadCustomFromDirectory(
                    classifiedEntries,
                    classificationDir,
                    new CustomLoadLimits {
                        Files = 1, FileBytes = 256, TotalBytes = 256, Entries = 2
                    });
                var generalOnly = new FortuneProvider(
                    classifiedEntries,
                    new FortuneSettings {
                        ContentLevel = ContentLevels.Clean,
                        NoProfanity = true
                    });
                if (classifiedEntries.Count != 1 ||
                    !classifiedEntries[0].Prof ||
                    string.Equals(
                        classifiedEntries[0].Level,
                        "general",
                        StringComparison.OrdinalIgnoreCase) ||
                    generalOnly.Count != 0)
                {
                    ok = false;
                    sb.AppendLine(
                        "CUSTOM FAIL tagged metadata weakened content classification");
                }
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("CUSTOM EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception ex)
                {
                    ok = false;
                    sb.AppendLine("CUSTOM CLEANUP EXC: " + ex.Message);
                }
            }
            sb.AppendLine("custom_ingestion=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static bool ValidateEmbeddedForSelfTest(StringBuilder sb)
        {
            List<FortuneEntry> entries = EmbeddedCorpus();
            bool ok = entries.Count > 0;
            foreach (FortuneEntry entry in entries)
            {
                if (!FortuneTaxonomy.IsTopic(entry.Topic) ||
                    !FortuneTaxonomy.IsGenre(entry.Genre) ||
                    !FortuneTaxonomy.IsLevel(entry.Level))
                {
                    ok = false;
                    break;
                }
            }
            sb.AppendLine("embedded_rows=" + entries.Count + " schema=" +
                _embeddedSchemaVersion + " taxonomy=" + (ok ? "PASS" : "FAIL"));
            if (!string.IsNullOrEmpty(_embeddedError)) sb.AppendLine("embedded_error=" + _embeddedError);
            return ok;
        }

        // ---- loading --------------------------------------------------------

        private enum TaggedLoadResult { NotTagged, Loaded, Invalid }
        private enum TaggedDeclarationState { None, Valid, Invalid }

        internal const string TaggedFormatV1Declaration =
            "#!desktop-pet-fortunes-v1";
        internal const string TaggedFormatV2Declaration =
            "#!desktop-pet-fortunes-v2";
        private const string TaggedFormatDeclarationPrefix =
            "#!desktop-pet-fortunes-v";

        private static List<FortuneEntry> _embeddedCorpus;                       // parsed once; the embedded resource is immutable at runtime
        private static readonly object _embeddedCorpusLock = new object();
        private static int _embeddedSchemaVersion;
        private static string _embeddedError;

        private static void LoadEmbedded(List<FortuneEntry> list)
        {
            list.AddRange(EmbeddedCorpus());                                     // entries are read-only after load, so sharing refs is safe
        }

        /// <summary>
        /// Parse the embedded fortunes.txt once and cache it. Previously re-parsed the ~486KB
        /// resource on every static Sources() call (tab build / add / download).
        /// </summary>
        private static List<FortuneEntry> EmbeddedCorpus()
        {
            if (_embeddedCorpus != null) return _embeddedCorpus;
            lock (_embeddedCorpusLock)
            {
                if (_embeddedCorpus != null) return _embeddedCorpus;
                var parsed = new List<FortuneEntry>();
                try
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    string resName = null;
                    foreach (string n in asm.GetManifestResourceNames())
                        if (n.EndsWith("fortunes.txt", StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
                    if (resName == null)
                    {
                        _embeddedError = "embedded fortunes.txt resource not found";
                    }
                    else
                    {
                        string content;
                        using (Stream st = asm.GetManifestResourceStream(resName))
                        using (StreamReader r = new StreamReader(st, new UTF8Encoding(false, true)))
                            content = r.ReadToEnd();

                        string error;
                        if (!TryParseTaggedContent(content, false, true, out parsed,
                            out _embeddedSchemaVersion, out error))
                        {
                            parsed.Clear();
                            _embeddedError = error;
                        }
                    }
                }
                catch (Exception ex)
                {
                    parsed.Clear();
                    _embeddedError = ex.GetType().Name + ": " + ex.Message;
                }
                _embeddedCorpus = parsed;
                return _embeddedCorpus;
            }
        }

        private static List<FortuneEntry> _customCorpus;                         // parsed writable drop folder, cached on a directory fingerprint
        private static string _customSignature;
        private static readonly object _customCorpusLock = new object();

        private static void LoadCustom(List<FortuneEntry> list)
        {
            list.AddRange(CustomCorpus());
        }

        /// <summary>
        /// The user's writable fortunes folder, parsed and cached in RAM like the embedded and bundled
        /// tiers. Unlike those it can change at runtime (pack downloads, "Add fortunes…", manual drops),
        /// so the cache is keyed on a cheap directory fingerprint: an unchanged folder is a cache hit,
        /// and any change re-parses automatically. Previously this folder was re-read and re-parsed on
        /// every static Sources()/Genres() call and every pool rebuild, which froze the Options UI for
        /// seconds once a few megabytes of packs had been downloaded.
        /// </summary>
        private static List<FortuneEntry> CustomCorpus()
        {
            string directory;
            try { directory = CustomDir; } catch { directory = null; }
            string signature = CustomDirSignature(directory);

            List<FortuneEntry> cached = _customCorpus;
            if (cached != null && string.Equals(_customSignature, signature, StringComparison.Ordinal))
                return cached;

            lock (_customCorpusLock)
            {
                if (_customCorpus != null && string.Equals(_customSignature, signature, StringComparison.Ordinal))
                    return _customCorpus;
                var parsed = new List<FortuneEntry>();
                try { LoadCustomFromDirectory(parsed, directory, DefaultCustomLoadLimits); }
                catch { parsed.Clear(); }
                _customCorpus = parsed;
                _customSignature = signature;
                return _customCorpus;
            }
        }

        /// <summary>
        /// A cheap fingerprint of the writable fortunes folder over exactly the files the loader reads
        /// (top-level <c>*.txt</c>): each file's path, byte length, and last-write time. Reading this
        /// metadata is microseconds; parsing the files is the slow part, so this lets an unchanged
        /// folder skip the re-parse while any add/remove/edit invalidates the cache.
        /// </summary>
        private static string CustomDirSignature(string directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    return "none";
                var paths = new List<string>(
                    Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly));
                paths.Sort(StringComparer.OrdinalIgnoreCase);
                var sb = new StringBuilder();
                foreach (string path in paths)
                {
                    var info = new FileInfo(path);
                    sb.Append(path).Append('|')
                      .Append(info.Length).Append('|')
                      .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
                return sb.ToString();
            }
            catch { return Guid.NewGuid().ToString("N"); }   // on any error, never serve a stale cache
        }

        private static List<FortuneEntry> _bundledCorpus;                        // parsed once; the bundled tier is immutable at runtime
        private static readonly object _bundledCorpusLock = new object();

        /// <summary>
        /// Read-only fortune packs under the module's own storage (<see cref="FortunePaths.BundledDir"/>).
        /// The module bundles none by default, so this directory normally does not exist and simply
        /// contributes nothing — the user's packs come from the catalog or their own imports instead.
        /// Parsed once and cached like the embedded corpus: without this, bundled packs would be
        /// re-read and re-parsed on every static Sources()/Genres() call and every pool rebuild.
        /// </summary>
        private static void LoadBundled(List<FortuneEntry> list)
        {
            list.AddRange(BundledCorpus());                                      // entries are read-only after load, so sharing refs is safe
        }

        private static List<FortuneEntry> BundledCorpus()
        {
            if (_bundledCorpus != null) return _bundledCorpus;
            lock (_bundledCorpusLock)
            {
                if (_bundledCorpus != null) return _bundledCorpus;
                var parsed = new List<FortuneEntry>();
                try
                {
                    LoadCustomFromDirectory(
                        parsed, FortunePaths.BundledDir, DefaultCustomLoadLimits);
                }
                catch { parsed.Clear(); }
                _bundledCorpus = parsed;
                return _bundledCorpus;
            }
        }

        /// <summary>
        /// Assemble the full corpus from every tier: the embedded default set, the read-only bundled
        /// packs under the module's storage, then the user's writable drop folder. Single source of truth
        /// so the pool, the source picker, and the genre picker never diverge on what is available.
        /// </summary>
        private static void LoadStandardCorpus(List<FortuneEntry> list)
        {
            LoadEmbedded(list);
            LoadBundled(list);
            LoadCustom(list);
        }

        private static void LoadCustomFromDirectory(
            List<FortuneEntry> list,
            string directory,
            CustomLoadLimits limits)
        {
            if (list == null || limits == null || limits.Files < 1 ||
                limits.FileBytes < 1 || limits.TotalBytes < 1 || limits.Entries < 1 ||
                string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;

            int files = 0;
            int totalBytes = 0;
            int totalEntries = 0;
            try
            {
                foreach (string path in Directory.EnumerateFiles(
                    directory, "*.txt", SearchOption.TopDirectoryOnly))
                {
                    if (files >= limits.Files || totalEntries >= limits.Entries)
                        break;
                    files++;

                    string content;
                    int bytesRead;
                    int remainingBytes = limits.TotalBytes - totalBytes;
                    int fileLimit = Math.Min(limits.FileBytes, remainingBytes);
                    long declaredLength;
                    try { declaredLength = new FileInfo(path).Length; }
                    catch { continue; }
                    if (fileLimit < 1 || declaredLength < 1 ||
                        declaredLength > fileLimit)
                        continue;
                    int chargedBytes = (int)declaredLength;
                    totalBytes += chargedBytes;
                    if (!TryReadStrictUtf8File(
                            path, chargedBytes, out content, out bytesRead) ||
                        bytesRead != chargedBytes)
                        continue;

                    string source = Path.GetFileNameWithoutExtension(path);
                    if (!IsValidCustomSource(source))
                        continue;

                    int remainingEntries = limits.Entries - totalEntries;
                    List<FortuneEntry> staged;
                    string validationError;
                    if (!TryParseCustomContent(
                            content,
                            source,
                            remainingEntries,
                            out staged,
                            out validationError))
                        continue;

                    // Nothing from a file becomes visible until the entire file has passed strict
                    // decoding, format validation, and all configured resource bounds.
                    list.AddRange(staged);
                    totalEntries += staged.Count;
                }
            }
            catch { }
        }

        /// <summary>
        /// Parse a declared tagged pack, or preserve compatibility with an unmarked legacy pack only
        /// when its complete contents strictly validate as v1/v2. A declaration is the unambiguous
        /// fail-closed boundary: damaged declared data is never reinterpreted as plain prose.
        /// </summary>
        private static TaggedLoadResult ParseTaggedPackContent(
            string content,
            int remainingEntries,
            out List<FortuneEntry> parsed)
        {
            parsed = new List<FortuneEntry>();
            if (remainingEntries < 1)
                return TaggedLoadResult.Invalid;

            string taggedContent;
            int declaredSchema;
            string declarationError;
            TaggedDeclarationState declaration = ReadTaggedDeclaration(
                content,
                out taggedContent,
                out declaredSchema,
                out declarationError);
            if (declaration == TaggedDeclarationState.Invalid)
                return TaggedLoadResult.Invalid;

            int schema;
            string error;
            if (!TryParseTaggedContent(
                    taggedContent, true, true,
                    Math.Min(MaximumTaggedRows, remainingEntries),
                    out parsed, out schema, out error))
            {
                parsed.Clear();
                return declaration == TaggedDeclarationState.Valid
                    ? TaggedLoadResult.Invalid
                    : TaggedLoadResult.NotTagged;
            }
            if (declaration == TaggedDeclarationState.Valid &&
                schema != declaredSchema)
            {
                parsed.Clear();
                return TaggedLoadResult.Invalid;
            }
            return TaggedLoadResult.Loaded;
        }

        private static TaggedDeclarationState ReadTaggedDeclaration(
            string content,
            out string taggedContent,
            out int schemaVersion,
            out string error)
        {
            taggedContent = content;
            schemaVersion = 0;
            error = null;
            if (string.IsNullOrEmpty(content))
                return TaggedDeclarationState.None;

            int lineEnd = content.IndexOf('\n');
            string firstLine = lineEnd < 0
                ? content
                : content.Substring(0, lineEnd);
            if (firstLine.EndsWith("\r", StringComparison.Ordinal))
                firstLine = firstLine.Substring(0, firstLine.Length - 1);
            if (firstLine.Length > 0 && firstLine[0] == '\uFEFF')
                firstLine = firstLine.Substring(1);

            if (!firstLine.StartsWith(
                    TaggedFormatDeclarationPrefix,
                    StringComparison.OrdinalIgnoreCase))
                return TaggedDeclarationState.None;

            if (string.Equals(
                    firstLine,
                    TaggedFormatV1Declaration,
                    StringComparison.OrdinalIgnoreCase))
                schemaVersion = FortuneTaxonomy.LegacySchemaVersion;
            else if (string.Equals(
                    firstLine,
                    TaggedFormatV2Declaration,
                    StringComparison.OrdinalIgnoreCase))
                schemaVersion = FortuneTaxonomy.CurrentSchemaVersion;
            else
            {
                error = "unsupported tagged fortune declaration";
                taggedContent = "";
                return TaggedDeclarationState.Invalid;
            }

            if (lineEnd < 0 || lineEnd + 1 >= content.Length)
            {
                error = "declared tagged fortune content has no rows";
                taggedContent = "";
                return TaggedDeclarationState.Invalid;
            }
            taggedContent = content.Substring(lineEnd + 1);
            return TaggedDeclarationState.Valid;
        }

        /// <summary>
        /// Side-effect-free pack validator for the downloader. An expected row count less than zero
        /// disables the count check. Successful validation returns schema version 1 or 2.
        /// </summary>
        internal static bool TryValidateTaggedPack(string content, int expectedRowCount,
            out int actualRowCount, out int schemaVersion, out string error)
        {
            string taggedContent;
            int declaredSchema;
            string declarationError;
            TaggedDeclarationState declaration = ReadTaggedDeclaration(
                content,
                out taggedContent,
                out declaredSchema,
                out declarationError);
            if (declaration == TaggedDeclarationState.Invalid)
            {
                actualRowCount = 0;
                schemaVersion = 0;
                error = declarationError;
                return false;
            }

            List<FortuneEntry> parsed;
            bool ok = TryParseTaggedContent(
                taggedContent, true, true, MaximumTaggedRows,
                out parsed, out schemaVersion, out error);
            actualRowCount = parsed == null ? 0 : parsed.Count;
            if (!ok) return false;
            if (declaration == TaggedDeclarationState.Valid &&
                schemaVersion != declaredSchema)
            {
                error = "declared schema v" + declaredSchema +
                    " does not match row schema v" + schemaVersion;
                return false;
            }
            if (expectedRowCount >= 0 && actualRowCount != expectedRowCount)
            {
                error = "row count " + actualRowCount + " does not match expected " + expectedRowCount;
                return false;
            }
            return true;
        }

        /// <summary>Strict UTF-8 byte overload for bounded download pipelines.</summary>
        internal static bool TryValidateTaggedPack(byte[] bytes, int expectedRowCount,
            out int actualRowCount, out int schemaVersion, out string error)
        {
            actualRowCount = 0;
            schemaVersion = 0;
            error = null;
            if (bytes == null)
            {
                error = "pack bytes are null";
                return false;
            }
            if (bytes.Length > MaximumTaggedContentCharacters)
            {
                error = "pack exceeds the tagged-content size limit";
                return false;
            }
            try
            {
                string content = new UTF8Encoding(false, true).GetString(bytes);
                return TryValidateTaggedPack(content, expectedRowCount,
                    out actualRowCount, out schemaVersion, out error);
            }
            catch (DecoderFallbackException)
            {
                error = "pack is not valid UTF-8";
                return false;
            }
        }

        /// <summary>
        /// Strictly validates one custom-file payload using the same tagged/plain parser and
        /// classification rules as runtime ingestion.
        /// </summary>
        internal static bool TryValidateCustomPackBytes(
            byte[] bytes,
            string source,
            int maximumEntries,
            out int entryCount,
            out string error)
        {
            entryCount = 0;
            error = null;
            if (bytes == null || bytes.Length < 1 ||
                bytes.Length > FortunePackLoadPolicy.MaximumFileBytes)
            {
                error = "file byte count is outside the runtime per-file limit";
                return false;
            }

            string content;
            try
            {
                content = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                error = "file is not valid UTF-8";
                return false;
            }
            if (content.Length > 0 && content[0] == '\uFEFF')
                content = content.Substring(1);

            List<FortuneEntry> staged;
            if (!TryParseCustomContent(
                    content,
                    source,
                    maximumEntries,
                    out staged,
                    out error))
                return false;
            entryCount = staged.Count;
            return true;
        }

        private static bool TryParseTaggedContent(string content, bool custom, bool allowLegacy,
            out List<FortuneEntry> parsed, out int schemaVersion, out string error)
        {
            return TryParseTaggedContent(
                content, custom, allowLegacy, MaximumTaggedRows,
                out parsed, out schemaVersion, out error);
        }

        private static bool TryParseTaggedContent(
            string content,
            bool custom,
            bool allowLegacy,
            int maximumRows,
            out List<FortuneEntry> parsed,
            out int schemaVersion,
            out string error)
        {
            parsed = new List<FortuneEntry>();
            schemaVersion = 0;
            error = null;
            if (string.IsNullOrEmpty(content))
            {
                error = "pack is empty";
                return false;
            }
            if (content.Length > MaximumTaggedContentCharacters)
            {
                error = "pack exceeds the tagged-content size limit";
                return false;
            }
            if (maximumRows < 1 || maximumRows > MaximumTaggedRows)
            {
                error = "tagged row limit is invalid";
                return false;
            }

            int lineNumber = 0;
            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (lineNumber > maximumRows)
                    {
                        error = "pack exceeds the maximum row count of " + maximumRows;
                        parsed.Clear();
                        return false;
                    }
                    if (lineNumber == 1 && line.Length > 0 && line[0] == '\uFEFF')
                        line = line.Substring(1);
                    if (line.Length == 0)
                    {
                        error = "line " + lineNumber + " is blank";
                        parsed.Clear();
                        return false;
                    }

                    if (line.Length > MaximumTaggedLineCharacters)
                    {
                        error = "line " + lineNumber + " exceeds the line length limit";
                        parsed.Clear();
                        return false;
                    }
                    int fieldCount = 1;
                    for (int i = 0; i < line.Length; i++)
                        if (line[i] == '\t') fieldCount++;
                    if (fieldCount != 6 && !(allowLegacy && fieldCount == 5))
                    {
                        error = "line " + lineNumber + " has " + fieldCount +
                            " fields; expected exactly 6" + (allowLegacy ? " or legacy 5" : "");
                        parsed.Clear();
                        return false;
                    }

                    string[] fields = line.Split('\t');
                    int rowSchema = fields.Length == 6 ? FortuneTaxonomy.CurrentSchemaVersion :
                        fields.Length == 5 && allowLegacy ? FortuneTaxonomy.LegacySchemaVersion : 0;
                    if (rowSchema == 0)
                    {
                        error = "line " + lineNumber + " has " + fields.Length +
                            " fields; expected exactly 6" + (allowLegacy ? " or legacy 5" : "");
                        parsed.Clear();
                        return false;
                    }
                    if (schemaVersion == 0) schemaVersion = rowSchema;
                    if (schemaVersion != rowSchema)
                    {
                        error = "line " + lineNumber + " mixes schema v" + rowSchema +
                            " with schema v" + schemaVersion;
                        parsed.Clear();
                        return false;
                    }

                    FortuneEntry entry;
                    string rowError;
                    bool valid = rowSchema == FortuneTaxonomy.CurrentSchemaVersion
                        ? TryParseCurrentRow(fields, custom, out entry, out rowError)
                        : TryParseLegacyRow(fields, custom, out entry, out rowError);
                    if (!valid)
                    {
                        error = "line " + lineNumber + ": " + rowError;
                        parsed.Clear();
                        return false;
                    }
                    ApplyClassificationFloor(ref entry);
                    parsed.Add(entry);
                }
            }

            if (parsed.Count == 0)
            {
                error = "pack contains no rows";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Treat supplied tags as a minimum severity and raise them when the text classifier finds
        /// stricter content. This applies to every tagged source, including the embedded corpus.
        /// </summary>
        private static void ApplyClassificationFloor(ref FortuneEntry entry)
        {
            string classifiedLevel;
            bool classifiedProfanity;
            FortuneClassifier.Classify(
                entry.Text,
                entry.Source,
                out classifiedLevel,
                out classifiedProfanity);
            entry.Prof = entry.Prof || classifiedProfanity;
            if (ContentLevelRank(classifiedLevel) > ContentLevelRank(entry.Level))
                entry.Level = classifiedLevel;
        }

        private static int ContentLevelRank(string level)
        {
            if (string.Equals(level, "nsfw", StringComparison.Ordinal))
                return 2;
            if (string.Equals(level, "edgy", StringComparison.Ordinal))
                return 1;
            return 0;
        }

        private static bool TryParseCurrentRow(string[] fields, bool custom,
            out FortuneEntry entry, out string error)
        {
            entry = new FortuneEntry();
            error = null;
            if (!ValidateCommonFields(fields[0], fields[3], fields[4], fields[5], out error))
                return false;
            if (!FortuneTaxonomy.IsTopic(fields[1]))
            {
                error = "unknown topic '" + fields[1] + "'";
                return false;
            }
            if (!FortuneTaxonomy.IsGenre(fields[2]))
            {
                error = "unknown genre '" + fields[2] + "'";
                return false;
            }

            entry = new FortuneEntry {
                Source = fields[0], Topic = fields[1], Genre = fields[2], Level = fields[3],
                Prof = fields[4] == "1", Text = DecodeScrapedText(fields[5]), Custom = custom
            };
            return true;
        }

        /// <summary>
        /// Undo HTML escaping left behind in scraped pack text.
        ///
        /// The packs were built from web sources and some lines arrived still escaped, so the bubble showed
        /// "me &amp;amp; Dave" literally. Reddit-sourced lines are worse: they carry <c>&amp;amp;#x200B;</c>,
        /// a zero-width space escaped TWICE, so a single decode pass leaves a visible <c>&amp;#x200B;</c>
        /// behind.
        ///
        /// Two passes, bounded, and stopping early once a pass changes nothing: unbounded decoding would
        /// eventually mangle a line whose SUBJECT is an entity (a joke about typing "&amp;amp;" is still a
        /// legitimate fortune). Zero-width characters are then stripped, because they survive decoding
        /// invisibly and only serve to pad the bubble and confuse word wrap.
        ///
        /// Done at PARSE time rather than by rewriting the .txt packs, so it also repairs the packs already
        /// sitting in a user's fortunes folder without them re-downloading anything.
        /// </summary>
        private static bool IsZeroWidth(char c)
        {
            // By CODE POINT, never as a literal: an invisible character in a source file cannot be
            // reviewed, and a stray edit could silently delete or alter one without any visible diff.
            return c == (char)0x200B      // zero-width space (the Reddit artifact)
                || c == (char)0x200C      // zero-width non-joiner
                || c == (char)0x200D      // zero-width joiner
                || c == (char)0xFEFF;     // BOM / zero-width no-break space
        }

        internal static string DecodeScrapedText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            for (int pass = 0; pass < 2 && text.IndexOf('&') >= 0; pass++)
            {
                string decoded = WebUtility.HtmlDecode(text);
                if (string.Equals(decoded, text, StringComparison.Ordinal)) break;
                text = decoded;
            }
            bool anyZeroWidth = false;
            for (int i = 0; i < text.Length && !anyZeroWidth; i++) anyZeroWidth = IsZeroWidth(text[i]);
            if (anyZeroWidth)
            {
                var kept = new StringBuilder(text.Length);
                foreach (char c in text) if (!IsZeroWidth(c)) kept.Append(c);
                text = kept.ToString();
            }
            return text.Trim();
        }

        private static bool TryParseLegacyRow(string[] fields, bool custom,
            out FortuneEntry entry, out string error)
        {
            entry = new FortuneEntry();
            error = null;
            if (!ValidateCommonFields(fields[0], fields[2], fields[3], fields[4], out error))
                return false;

            string topic, genre;
            if (!TryMapLegacyCategory(fields[1], out topic, out genre))
            {
                error = "unsupported legacy category '" + fields[1] + "'";
                return false;
            }

            entry = new FortuneEntry {
                Source = fields[0], Topic = topic, Genre = genre, Level = fields[2],
                Prof = fields[3] == "1", Text = DecodeScrapedText(fields[4]), Custom = custom
            };
            return true;
        }

        private static bool ValidateCommonFields(string source, string level,
            string profanity, string text, out string error)
        {
            error = null;
            if (!IsValidCustomSource(source))
            {
                error = "source must be 1..128 trimmed non-control characters";
                return false;
            }
            if (!FortuneTaxonomy.IsLevel(level))
            {
                error = "unknown level '" + level + "'";
                return false;
            }
            if (profanity != "0" && profanity != "1")
            {
                error = "profanity flag must be 0 or 1";
                return false;
            }
            if (!IsValidFortuneText(text))
            {
                error = "text must be 8..280 trimmed non-control characters";
                return false;
            }
            return true;
        }

        private static bool TryMapLegacyCategory(string category, out string topic, out string genre)
        {
            topic = null;
            genre = null;
            if (category == null) return false;
            switch (category.ToLowerInvariant())
            {
                case "tech": topic = "tech"; genre = "quip"; return true;
                case "facts": topic = "science"; genre = "fact"; return true;
                case "work": topic = "work-money"; genre = "aphorism"; return true;
                case "creative": topic = "arts"; genre = "aphorism"; return true;
                case "wisdom": topic = "life"; genre = "wisdom"; return true;
                case "observations": topic = "life"; genre = "observation"; return true;
                case "tv": topic = "life"; genre = "tv-quote"; return true;
                case "nsfw": topic = "life"; genre = "dark"; return true;
                case "spicy": topic = "life"; genre = "dark"; return true;
                case "whimsy": topic = "life"; genre = "quip"; return true;
                case "general": topic = "life"; genre = "quip"; return true;
                case "custom": topic = "life"; genre = "quip"; return true;
                default: return false;
            }
        }


        private static bool TryParsePlainContent(
            string content,
            string source,
            int maximumEntries,
            out List<FortuneEntry> parsed)
        {
            parsed = new List<FortuneEntry>();
            if (string.IsNullOrEmpty(content) || !IsValidCustomSource(source) ||
                maximumEntries < 1)
                return false;

            IEnumerable<string> chunks = Regex.IsMatch(content, @"(?m)^\s*%\s*$")
                ? (IEnumerable<string>)Regex.Split(content, @"(?m)^\s*%\s*$")
                : content.Split('\n');
            foreach (string chunk in chunks)
            {
                string text = Regex.Replace(chunk, @"\s+", " ").Trim();
                if (!IsValidFortuneText(text))
                    continue;
                if (parsed.Count >= maximumEntries)
                {
                    parsed.Clear();
                    return false;
                }

                string level;
                bool prof;
                FortuneClassifier.Classify(text, source, out level, out prof);
                parsed.Add(new FortuneEntry {
                    Source = source,
                    Topic = "life",
                    Genre = FortuneClassifier.ClassifyGenre(source),
                    Level = level,
                    Prof = prof,
                    Text = text,
                    Custom = true
                });
            }
            return parsed.Count > 0;
        }

        private static bool TryParseCustomContent(
            string content,
            string source,
            int maximumEntries,
            out List<FortuneEntry> parsed,
            out string error)
        {
            parsed = new List<FortuneEntry>();
            error = null;
            if (!IsValidCustomSource(source))
            {
                error = "file name is not a valid custom source identifier";
                return false;
            }
            if (maximumEntries < 1)
            {
                error = "runtime aggregate entry limit is exhausted";
                return false;
            }

            TaggedLoadResult tagged = ParseTaggedPackContent(
                content,
                maximumEntries,
                out parsed);
            if (tagged == TaggedLoadResult.Invalid)
            {
                error = "tagged fortune content is malformed or exceeds the row limit";
                return false;
            }
            if (tagged == TaggedLoadResult.NotTagged &&
                !TryParsePlainContent(
                    content,
                    source,
                    maximumEntries,
                    out parsed))
            {
                error = "plain fortune content has no valid rows or exceeds the row limit";
                return false;
            }
            return true;
        }

        private static bool TryReadStrictUtf8File(
            string path,
            int maximumBytes,
            out string content,
            out int bytesRead)
        {
            content = null;
            bytesRead = 0;
            if (string.IsNullOrWhiteSpace(path) || maximumBytes < 1)
                return false;
            try
            {
                using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length < 1 || stream.Length > maximumBytes)
                        return false;
                    using (var destination = new MemoryStream((int)stream.Length))
                    {
                        var buffer = new byte[8192];
                        while (true)
                        {
                            int remaining = maximumBytes - bytesRead;
                            int requested = Math.Min(buffer.Length, remaining + 1);
                            int read = stream.Read(buffer, 0, requested);
                            if (read == 0) break;
                            bytesRead += read;
                            if (bytesRead > maximumBytes)
                                return false;
                            destination.Write(buffer, 0, read);
                        }
                        content = StrictUtf8.GetString(destination.ToArray());
                    }
                }
                if (content.Length > 0 && content[0] == '\uFEFF')
                    content = content.Substring(1);
                return true;
            }
            catch
            {
                content = null;
                bytesRead = 0;
                return false;
            }
        }

        private static bool IsValidCustomSource(string source)
        {
            return !string.IsNullOrEmpty(source) &&
                   source.Length <= 128 &&
                   source == source.Trim() &&
                   !ContainsControlCharacter(source);
        }

        private static bool IsValidFortuneText(string text)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.Length >= 8 &&
                   text.Length <= 280 &&
                   text == text.Trim() &&
                   !ContainsControlCharacter(text);
        }

        private static bool ContainsControlCharacter(string value)
        {
            foreach (char character in value)
                if (char.IsControl(character))
                    return true;
            return false;
        }

        // ---- source enumeration for the picker ------------------------------

        private sealed class SourceAccumulator
        {
            public string Id;
            public int Count;
            public bool Custom;
            public bool HasSpicy;
            public readonly Dictionary<string, int> TopicCounts =
                new Dictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>All available source collections (built-in + custom) with entry counts.</summary>
        public static List<SourceStat> Sources()
        {
            var all = new List<FortuneEntry>();
            LoadStandardCorpus(all);

            var map = new Dictionary<string, SourceAccumulator>(StringComparer.OrdinalIgnoreCase);
            foreach (FortuneEntry e in all)
            {
                SourceAccumulator accumulator;
                if (!map.TryGetValue(e.Source, out accumulator))
                {
                    accumulator = new SourceAccumulator { Id = e.Source, Custom = e.Custom };
                    map[e.Source] = accumulator;
                }
                accumulator.Count++;
                if (!string.Equals(e.Level, "general", StringComparison.OrdinalIgnoreCase))
                    accumulator.HasSpicy = true;
                int topicCount;
                accumulator.TopicCounts.TryGetValue(e.Topic, out topicCount);
                accumulator.TopicCounts[e.Topic] = topicCount + 1;
            }

            var result = new List<SourceStat>();
            foreach (SourceAccumulator accumulator in map.Values)
            {
                string dominantTopic = "life";
                int dominantCount = -1;
                foreach (KeyValuePair<string, int> pair in accumulator.TopicCounts)
                    if (pair.Value > dominantCount ||
                        (pair.Value == dominantCount &&
                         string.CompareOrdinal(pair.Key, dominantTopic) < 0))
                    {
                        dominantTopic = pair.Key;
                        dominantCount = pair.Value;
                    }
                result.Add(new SourceStat {
                    Id = accumulator.Id, Topic = dominantTopic, Count = accumulator.Count,
                    Custom = accumulator.Custom, HasSpicy = accumulator.HasSpicy
                });
            }
            result.Sort((a, b) =>
            {
                if (a.Custom != b.Custom) return a.Custom ? 1 : -1;               // custom last
                int c = string.Compare(a.Topic, b.Topic, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;                                             // then grouped by theme
                return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>All delivery genres present in the corpus (built-in + custom) with entry counts,
        /// most common first. Backs the Fortunes-tab genre picker.</summary>
        public static List<GenreStat> Genres()
        {
            var all = new List<FortuneEntry>();
            LoadStandardCorpus(all);
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (FortuneEntry e in all)
            {
                if (!FortuneTaxonomy.IsGenre(e.Genre)) continue;
                int count;
                map.TryGetValue(e.Genre, out count);
                map[e.Genre] = count + 1;
            }
            var result = new List<GenreStat>();
            foreach (KeyValuePair<string, int> pair in map)
                result.Add(new GenreStat { Id = pair.Key, Count = pair.Value });
            result.Sort((a, b) =>
            {
                if (a.Count != b.Count) return b.Count - a.Count;               // most common first
                return string.CompareOrdinal(a.Id, b.Id);
            });
            return result;
        }

        /// <summary>
        /// Diagnostic (`--fortunecache-selftest`): proves the writable-folder cache reflects add / edit
        /// / remove without a restart (i.e. the directory fingerprint invalidates correctly). Requires
        /// an isolated DESKTOP_AI_COMPANION_DATA_ROOT so it only ever writes throwaway files.
        /// </summary>
        public static bool CustomCacheSelfTest()
        {
            var sb = new StringBuilder();
            bool ok = true;
            const string id = "dpcachetest";
            string file = null;
            try
            {
                string root = Environment.GetEnvironmentVariable("DESKTOP_AI_COMPANION_DATA_ROOT");
                if (string.IsNullOrWhiteSpace(root))
                {
                    sb.AppendLine("FAIL: DESKTOP_AI_COMPANION_DATA_ROOT must be set (isolated root).");
                    return FinishCacheTest(sb, false);
                }
                string dir = CustomDir;
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, id + ".txt");
                var utf8 = new UTF8Encoding(false);

                ok &= CacheCheck(sb, "source absent before any file", SourceCount(id) == 0);

                File.WriteAllText(file, "cache test alpha\ncache test bravo\ncache test charlie\n", utf8);
                int afterAdd = SourceCount(id);
                ok &= CacheCheck(sb, "add is reflected (source appears)", afterAdd > 0);
                ok &= CacheCheck(sb, "repeat read is stable (cache hit returns same)", SourceCount(id) == afterAdd);

                System.Threading.Thread.Sleep(20);   // ensure a distinct last-write time on fast disks
                File.WriteAllText(file, "cache test alpha\ncache test bravo\ncache test charlie\ncache test delta\ncache test echo\ncache test foxtrot\n", utf8);
                int afterEdit = SourceCount(id);
                ok &= CacheCheck(sb, "edit is reflected (count grows)", afterEdit > afterAdd);

                File.Delete(file); file = null;
                ok &= CacheCheck(sb, "remove is reflected (source gone)", SourceCount(id) == 0);
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { if (file != null && File.Exists(file)) File.Delete(file); } catch { } }
            return FinishCacheTest(sb, ok);
        }

        private static int SourceCount(string id)
        {
            foreach (SourceStat s in Sources())
                if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) return s.Count;
            return 0;
        }
        private static bool CacheCheck(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool FinishCacheTest(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-fortunecache-selftest.txt"), sb.ToString()); } catch { }
            return ok;
        }
    }

    /// <summary>A delivery genre as shown in the picker (aggregate over its entries).</summary>
    internal struct GenreStat
    {
        public string Id;
        public int    Count;
    }

    /// <summary>
    /// Deterministic content classifier for tagged and plain fortunes. Mirrors classify-corpus.py
    /// so runtime validation can enforce supplied tags as a minimum severity.
    /// </summary>
    internal static class FortuneClassifier
    {
        private const string LeftAsciiBoundary = @"(?<![a-z0-9_])";
        private const string RightAsciiBoundary = @"(?![a-z0-9_])";
        private const string AsciiWhitespace = @"[ \t\r\n\f\v]+";

        private static readonly Regex Nsfw = new Regex(
            LeftAsciiBoundary +
            @"(pussy|cocks|dicks|cocksuck[a-z0-9_]*|penis|penises|vagina[a-z0-9_]*|" +
            @"cums|cumming|jizz|blow ?jobs?|hand ?jobs?|rim ?jobs?|masturbat[a-z0-9_]*|" +
            @"porn[a-z0-9_]*|rap(?:e|ed|es|ing|ists?)|dildo[a-z0-9_]*|orgasms?|" +
            @"org(?:y|ies)|semen|ejaculat[a-z0-9_]*|erections?|boners?|incest[a-z0-9_]*|" +
            @"(?:ped|paed)ophil[a-z0-9_]*|necrophil[a-z0-9_]*|molest[a-z0-9_]*|" +
            @"horny|clit[a-z0-9_]*|nipples?|titties|titty|slut[a-z0-9_]*|" +
            @"whore[a-z0-9_]*|cunt[a-z0-9_]*|nsfw|hentai|dominatrix|" +
            @"fetish[a-z0-9_]*|genital[a-z0-9_]*|scrotum|testicles?|foreskin|" +
            @"blowie|creampie|cumshot|deepthroat|felch[a-z0-9_]*|fisting|gangbang|" +
            @"bukkake|blow job|handjob|jack him off)" +
            RightAsciiBoundary,
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);

        // Keep in parity with src/Fortunes/classify-corpus.py EXPLICIT_SEX. Phrase-level rules
        // catch explicit descriptions without making neutral terms such as "biological sex" or
        // "same-sex" adult content.
        private static readonly Regex ExplicitSex = new Regex(
            LeftAsciiBoundary +
            @"(?:(?:have|has|had|having|solicit(?:s|ed|ing)?)" +
            AsciiWhitespace +
            @"sex|sex" +
            AsciiWhitespace +
            @"(?:with|from)|sexual(?:ly)?" +
            AsciiWhitespace +
            @"assault(?:s|ed|ing)?|(?:bestiality|zoophil[a-z0-9_]*))" +
            RightAsciiBoundary,
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);

        private static readonly Regex Edgy = new Regex(
            LeftAsciiBoundary +
            @"(fuck[a-z0-9_]*|shit[a-z0-9_]*|bitch[a-z0-9_]*|asshole[a-z0-9_]*|" +
            @"ass|arse[a-z0-9_]*|damn|goddamn[a-z0-9_]*|bastard[a-z0-9_]*|" +
            @"piss[a-z0-9_]*|nigg[a-z0-9_]*|fag|faggot[a-z0-9_]*|" +
            @"retard[a-z0-9_]*|douche[a-z0-9_]*|pricks?|wank[a-z0-9_]*|" +
            @"bollocks|twat[a-z0-9_]*|jackass|dumbass|motherfuck[a-z0-9_]*|" +
            @"bullshit|dick(?:heads?|wads?|bags?|faces?)?|cock|boob[a-z0-9_]*|tits)" +
            RightAsciiBoundary,
            RegexOptions.CultureInvariant |
            RegexOptions.Compiled);

        private static readonly HashSet<string> EdgySources =
            new HashSet<string>(StringComparer.Ordinal) { "yo-mama", "carlin" };

        internal static bool UsesCanonicalUnicodeFold
        {
            get
            {
                return (Nsfw.Options & RegexOptions.IgnoreCase) == 0 &&
                    (ExplicitSex.Options & RegexOptions.IgnoreCase) == 0 &&
                    (Edgy.Options & RegexOptions.IgnoreCase) == 0;
            }
        }

        public static void Classify(string text, string source, out string level, out bool prof)
        {
            string canonicalText = Canonicalize(text);
            string canonicalSource = Canonicalize(source);
            bool nsfw =
                Nsfw.IsMatch(canonicalText) ||
                ExplicitSex.IsMatch(canonicalText);
            bool edgy = Edgy.IsMatch(canonicalText);
            prof = nsfw || edgy;
            level = nsfw
                ? "nsfw"
                : (edgy || EdgySources.Contains(canonicalSource))
                    ? "edgy"
                    : "general";
        }

        /// <summary>
        /// A taxonomy genre for a plain (untagged) pack, derived from its source id. A downloaded pack
        /// carries no per-line tags, so every plain line used to be hardcoded to <c>quip</c> — which made
        /// the Genres filter a silent no-op for ALL downloaded content (disabling "tv-quote" or "fact"
        /// removed nothing, since nothing was tagged either). A plain pack is homogeneous in delivery
        /// style, so a per-pack guess from the id is coarse but honest, and it makes the Genres toggles
        /// actually filter downloaded packs. Only unambiguous id signals are mapped; anything else stays
        /// the generic <c>quip</c>. Every returned value is a real <see cref="FortuneTaxonomy"/> genre.
        /// </summary>
        internal static string ClassifyGenre(string source)
        {
            string s = Canonicalize(source);
            if (string.IsNullOrEmpty(s)) return "quip";
            if (s.StartsWith("tv-", StringComparison.Ordinal)) return "tv-quote";
            if (s.Contains("fact")) return "fact";                       // realfacts, chuckfacts
            if (s.Contains("limerick") || s.Contains("songs-poems") ||
                s.Contains("nash") || s == "wblake" || s == "racter")
                return "verse";
            if (s.Contains("joke") || s == "yo-mama" ||
                s.EndsWith("riddles", StringComparison.Ordinal))
                return "joke";
            return "quip";
        }

        private static string Canonicalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            string decomposed = value.Normalize(NormalizationForm.FormKD);
            var folded = new StringBuilder(decomposed.Length);
            for (int index = 0; index < decomposed.Length;)
            {
                UnicodeCategory category =
                    CharUnicodeInfo.GetUnicodeCategory(decomposed, index);
                int scalarLength =
                    char.IsHighSurrogate(decomposed[index]) &&
                    index + 1 < decomposed.Length &&
                    char.IsLowSurrogate(decomposed[index + 1])
                        ? 2
                        : 1;
                if (category != UnicodeCategory.NonSpacingMark &&
                    category != UnicodeCategory.SpacingCombiningMark &&
                    category != UnicodeCategory.EnclosingMark)
                {
                    char first = decomposed[index];
                    if (first == '\u0131')
                    {
                        folded.Append('i');
                    }
                    else if (scalarLength == 1 &&
                             first >= 'A' &&
                             first <= 'Z')
                    {
                        folded.Append((char)(first + ('a' - 'A')));
                    }
                    else
                    {
                        folded.Append(decomposed, index, scalarLength);
                    }
                }
                index += scalarLength;
            }
            return folded.ToString();
        }
    }
}
