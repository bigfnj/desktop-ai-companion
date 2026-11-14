using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// One compiled admission row of the policy registry (signed revision H :41, :56-64): the registry id and case, and the
    /// retained input's scenarioId, pack, ticks, full scenario SHA-256 and pack SHA-256. Only the full scenario SHA admits a
    /// candidate; the other fields describe the retained input and are checked against it at registry build time.
    /// </summary>
    internal sealed class ParityPolicyRow
    {
        public readonly string Id;
        public readonly string Case;
        public readonly string ScenarioId;
        public readonly string Pack;
        public readonly int Ticks;
        public readonly string ScenarioSha256;
        public readonly string PackSha256;

        public ParityPolicyRow(string id, string caseName, string scenarioId, string pack, int ticks, string scenarioSha256, string packSha256)
        {
            Id = id;
            Case = caseName;
            ScenarioId = scenarioId;
            Pack = pack;
            Ticks = ticks;
            ScenarioSha256 = scenarioSha256;
            PackSha256 = packSha256;
        }
    }

    /// <summary>
    /// N-parity-1107-03 C7 and C8 (signed revision H :41-64, :230-232, adopted as CONTRACT v1.3): the registry drift
    /// self-test a scenario/2 candidate reaches after the output claim and the command's prerequisites, and then the one
    /// scenario admission site, <see cref="Admission.Find"/>, by the full SHA-256 of the retained bytes.
    ///
    /// C8 adopts the production table: the jointly signed tools/ShimejiParity/policy/REGISTRY.json is embedded by the host
    /// project under <see cref="ResourceName"/> (the file itself, never a second copy), its four rows are compiled below and
    /// its signed digest is <see cref="CompiledDigest"/>. A build whose embedded bytes, rows or digest disagree fails the
    /// drift self-test for every candidate with the named registry-integrity internal defect, exit 2 and no trace.
    /// Refused (brief critique A4, Codex 2026-11-13): any table seam or internal admission-table parameter, even for
    /// self-tests, which H :64 forbids as a test-only admission override, since the self-test ships inside the product exe.
    /// So <see cref="EnsureVerified"/> and <see cref="Admission.Find"/> take no table, an <see cref="Admission"/> can be made
    /// nowhere but Find, and the drift self-test's checks are graded on the pure <see cref="Verify"/> with tables built by
    /// hand.
    ///
    /// The verdict is computed once per process and kept in a static of this assembly; nothing is written to disk, so a
    /// fresh process verifies again. Every candidate run logs the verdict with its own runId, and the ok line names the
    /// digest the check itself computed on the embedded bytes, so a verdict nobody computed names none. This file spells
    /// no parity error code (the bd-parity-host-2 invariant scans it).
    /// </summary>
    internal static class ParityPolicyRegistry
    {
        /// <summary>The embedded resource's logical name; the host project gives REGISTRY.json this name.</summary>
        internal const string ResourceName = "DesktopAICompanion.ShimejiParity.PolicyRegistry.json";

        // ---- the production table: CONTRACT v1.3, the jointly signed REGISTRY.json (payload eaa9c41d, rows in its order)
        private static readonly ParityPolicyRow[] CompiledRows =
        {
            new ParityPolicyRow("birth-cap", "cap-at", "S-cap-at-01", "packs/breed-cap", 20,
                "4914fc647a79d33b698b68dbc87a55edef1c196e4a418b68494ef2554aaa5f6f", "711d36cffa52f016a4eb880e69e28f4fa5afe09253c0a21419701dc1e6da8065"),
            new ParityPolicyRow("window-off-plays", "window-carry-off", "S-window-carry-off-01", "packs/window-carry", 90,
                "5471944eaa634fcb75857d4790c18fa361db4e9954c1b9de367fd9d2c1d639c6", "f5d4ac604929126542b75e53040c162fab8afcd6e81951f3daf6d47d9ff429e0"),
            new ParityPolicyRow("window-off-plays", "window-throw-off", "S-window-throw-off-01", "packs/window-throw", 20,
                "edc9dec2edd0a9e8edad09a8a0518fd494944195d13d7793daf57ba7ffb779e9", "8b0d4b77a722ed515e2e94fae0f99c2ec2c1d262d211cbf5a9611c8a8673ac9a"),
            new ParityPolicyRow("window-off-plays", "window-minimize-off", "S-window-minimize-off-01", "packs/window-minimize", 20,
                "1477c1718d24a1b372f36ca26b9b87967b9e0345fa21f081f7876fb94b3ae41d", "28785fabfddcb1e6c466cdbcb0af946deb36168d7b1d071168211f1d02f42074"),
        };
        internal const string CompiledDigest = "954776734f2ab70a4a96645e15e188d2d936e935b89e703e488c9e5de76f1442";
        // ---- end of the production table

        private static readonly string[] RowMembers =
        {
            "case", "id", "legacyScenarioSha256", "pack", "packSha256", "qualificationStatus", "scenarioId", "scenarioSha256", "ticks",
        };

        private static readonly object Gate = new object();
        private static bool _verified;
        private static string _defect;
        private static string _verifiedDigest;

        /// <summary>
        /// The drift self-test, once per process (H :230): the embedded registry's bytes against the compiled rows and digest
        /// (<see cref="Verify"/>). Logs "parity registry-integrity ok ..." with the digest this check computed on those
        /// bytes, or "parity registry-integrity internal-defect: ..." with the run's runId, and answers whether the candidate
        /// may go on. Takes no table: the compiled one is the only one.
        /// </summary>
        internal static bool EnsureVerified(ParityLoadedEngine engine, string runId, TextWriter log)
        {
            string defect, digest;
            lock (Gate)
            {
                if (!_verified)
                {
                    byte[] resource = ResourceBytes();
                    _defect = Verify(resource, CompiledRows, CompiledDigest);
                    if (_defect == null) _verifiedDigest = ParityRun.Sha256(resource);
                    _verified = true;
                }
                defect = _defect;
                digest = _verifiedDigest;
            }
            log.WriteLine(defect == null ? ParityRunLog.RegistryIntegrityOkLine(digest, engine, runId) : ParityRunLog.RegistryIntegrityDefectLine(defect, runId));
            return defect == null;
        }

        /// <summary>The digest this process's drift self-test computed on the embedded registry when it passed, else null
        /// (not run yet, or failed). INPUT-ADMITTED names it.</summary>
        internal static string VerifiedDigest
        {
            get
            {
                lock (Gate)
                {
                    return _verified && _defect == null ? _verifiedDigest : null;
                }
            }
        }

        /// <summary>
        /// A scenario's admission (H step 4-5): the compiled row whose full scenario SHA-256 equals the retained bytes'. The
        /// interpreting reader accepts scenario/2 only with one in hand. Its only constructor is private, so nothing makes
        /// one but <see cref="Find"/>: no shipped path, the self-test included, can hand the reader an admission for bytes
        /// the signed table does not hold.
        /// </summary>
        internal sealed class Admission
        {
            public readonly ParityPolicyRow Row;

            private Admission(ParityPolicyRow row)
            {
                Row = row;
            }

            /// <summary>
            /// The single scenario admission site (H :52): by the full SHA-256 of the retained bytes FIRST, never by a
            /// declared id, case or S-id. Null on a miss (scenario-not-registered), and null when this process's drift
            /// self-test has not passed, so a lookup moved ahead of <see cref="EnsureVerified"/> admits nothing in a fresh
            /// process. Takes no table.
            /// </summary>
            internal static Admission Find(string scenarioSha256)
            {
                lock (Gate)
                {
                    if (!_verified || _defect != null) return null;
                }
                foreach (ParityPolicyRow row in CompiledRows)
                    if (string.Equals(row.ScenarioSha256, scenarioSha256, StringComparison.Ordinal)) return new Admission(row);
                return null;
            }
        }

        /// <summary>The embedded registry's bytes, or null when the build embeds none: the window self-test compares them
        /// with the compiled digest and with the installed file.</summary>
        internal static byte[] EmbeddedBytes()
        {
            return ResourceBytes();
        }

        /// <summary>The embedded registry's bytes, or null when the build embeds none (a build whose project lost the
        /// EmbeddedResource or renamed it).</summary>
        private static byte[] ResourceBytes()
        {
            using (Stream stream = typeof(ParityPolicyRegistry).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) return null;
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
        }

        /// <summary>
        /// Null when <paramref name="resource"/> is exactly the registry the compiled <paramref name="rows"/> and
        /// <paramref name="digest"/> were made from, else the defect: a table not adopted; a missing resource; a digest that
        /// is not 64 lowercase hex characters or not the resource's SHA-256; a resource that is not strict JSON (a byte order
        /// mark, invalid UTF-8, a duplicate member at any depth, trailing content); a row whose members are not exactly the
        /// registry's; a compiled hex digest that is not lowercase; any field of a row unequal; an extra row on either side;
        /// or a legacy or provenance digest among the admission keys. Pure: it reads only its arguments.
        /// </summary>
        internal static string Verify(byte[] resource, IList<ParityPolicyRow> rows, string digest)
        {
            if (rows == null || digest == null) return "the registry table is not adopted: no finalized registry is compiled into this build";
            if (resource == null) return "the registry resource " + ResourceName + " is missing";
            if (!IsHex64(digest)) return "the compiled registry digest is not 64 lowercase hex characters";
            string actual = ParityRun.Sha256(resource);
            if (actual != digest) return "the registry resource hashes " + actual + ", the compiled digest is " + digest;
            string strict = StrictJsonFault(resource);
            if (strict != null) return "the registry resource is not strict JSON: " + strict;
            using (JsonDocument doc = JsonDocument.Parse(resource))
            {
                JsonElement root = doc.RootElement, cases;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cases", out cases) || cases.ValueKind != JsonValueKind.Array)
                    return "the registry resource holds no cases list";
                var legacy = new HashSet<string>(StringComparer.Ordinal);
                JsonElement generator;
                if (root.TryGetProperty("generatorSha256", out generator) && generator.ValueKind == JsonValueKind.String) legacy.Add(generator.GetString());
                int i = 0;
                foreach (JsonElement row in cases.EnumerateArray())
                {
                    if (i >= rows.Count) return "the registry resource holds row " + Ordinal(i) + ", which the compiled table lacks";
                    string fault = RowFault(row, rows[i], legacy);
                    if (fault != null) return "row " + Ordinal(i) + ": " + fault;
                    i++;
                }
                if (rows.Count != i) return "the compiled table holds an extra row: " + Ordinal(rows.Count) + " compiled, " + Ordinal(i) + " in the resource";
                foreach (ParityPolicyRow row in rows)
                    if (legacy.Contains(row.ScenarioSha256) || legacy.Contains(row.PackSha256))
                        return "the admission key " + row.ScenarioSha256 + " of " + row.Id + "/" + row.Case + " is a legacy or provenance digest";
            }
            return null;
        }

        /// <summary>One resource row against its compiled row; collects the row's legacy digest.</summary>
        private static string RowFault(JsonElement row, ParityPolicyRow compiled, HashSet<string> legacy)
        {
            if (compiled == null) return "the compiled row is null";
            if (!IsHex64(compiled.ScenarioSha256) || !IsHex64(compiled.PackSha256)) return "a compiled hex digest is not 64 lowercase hex characters";
            if (row.ValueKind != JsonValueKind.Object) return "not an object";
            var names = new List<string>();
            foreach (JsonProperty p in row.EnumerateObject()) names.Add(p.Name);
            names.Sort(StringComparer.Ordinal);
            if (names.Count != RowMembers.Length) return "its members are not exactly " + string.Join(", ", RowMembers);
            for (int k = 0; k < names.Count; k++)
                if (names[k] != RowMembers[k]) return "its members are not exactly " + string.Join(", ", RowMembers);
            foreach (string member in new[] { "case", "id", "legacyScenarioSha256", "pack", "packSha256", "qualificationStatus", "scenarioId", "scenarioSha256" })
                if (row.GetProperty(member).ValueKind != JsonValueKind.String) return "'" + member + "' is not a string";
            int ticks;
            if (row.GetProperty("ticks").ValueKind != JsonValueKind.Number || !row.GetProperty("ticks").TryGetInt32(out ticks)) return "'ticks' is not an Int32";
            foreach (string member in new[] { "legacyScenarioSha256", "packSha256", "scenarioSha256" })
                if (!IsHex64(row.GetProperty(member).GetString())) return "'" + member + "' is not 64 lowercase hex characters";
            legacy.Add(row.GetProperty("legacyScenarioSha256").GetString());
            if (row.GetProperty("id").GetString() != compiled.Id || row.GetProperty("case").GetString() != compiled.Case
                || row.GetProperty("scenarioId").GetString() != compiled.ScenarioId || row.GetProperty("pack").GetString() != compiled.Pack
                || ticks != compiled.Ticks || row.GetProperty("scenarioSha256").GetString() != compiled.ScenarioSha256
                || row.GetProperty("packSha256").GetString() != compiled.PackSha256)
                return "the compiled row " + compiled.Id + "/" + compiled.Case + " is not the resource's";
            return null;
        }

        /// <summary>Null, or why <paramref name="bytes"/> is not strict JSON: a byte order mark, invalid UTF-8, a syntax error
        /// or trailing content, or a member name repeated in one object at any depth (JsonDocument would keep the last).</summary>
        private static string StrictJsonFault(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return "a byte order mark";
            try
            {
                new UTF8Encoding(false, true).GetCharCount(bytes);
            }
            catch (DecoderFallbackException)
            {
                return "invalid UTF-8";
            }
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var names = new Stack<HashSet<string>>();
            try
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.StartObject) names.Push(new HashSet<string>(StringComparer.Ordinal));
                    else if (reader.TokenType == JsonTokenType.EndObject) names.Pop();
                    else if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string name = reader.GetString();
                        if (!names.Peek().Add(name)) return "the member '" + name + "' repeats in one object";
                    }
                }
            }
            catch (JsonException e)
            {
                return e.Message;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
            return null;
        }

        private static bool IsHex64(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static string Ordinal(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
