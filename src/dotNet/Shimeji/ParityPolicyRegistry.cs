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
    /// N-parity-1107-03 C7 (signed revision H :45, :56-64, :230-232): the registry drift self-test a scenario/2 candidate
    /// reaches after the output claim and the command's prerequisites, before any registry consultation.
    ///
    /// Until registry finalization and the joint signature, the production table is NOT ADOPTED: no resource is embedded, no
    /// row is compiled and no digest is set, so <see cref="EnsureVerified"/> fails for every candidate with the named
    /// registry-integrity internal defect, exit 2 and no trace. C8 replaces the three with the finalized REGISTRY.json's
    /// embedded bytes, its compiled rows and its digest, and adds the admission lookup. Refused (brief critique A4, Codex
    /// 2026-11-13): any table seam or internal admission-table parameter, even for self-tests, which H :64 forbids as a
    /// test-only admission override, since the self-test ships inside the product exe. So <see cref="EnsureVerified"/> takes
    /// no table, and the drift self-test's checks are graded on the pure <see cref="Verify"/> with tables built by hand.
    ///
    /// The verdict is computed once per process and kept in a static of this assembly; nothing is written to disk, so a
    /// fresh process verifies again. Every candidate run logs the verdict with its own runId. This file spells no parity
    /// error code (the bd-parity-host-2 invariant scans it).
    /// </summary>
    internal static class ParityPolicyRegistry
    {
        /// <summary>The embedded resource C8 adds (brief section 7, C8).</summary>
        internal const string ResourceName = "DesktopAICompanion.ShimejiParity.PolicyRegistry.json";

        // ---- the production table: NOT ADOPTED until C8 (null rows and digest; no resource is embedded)
        private static readonly ParityPolicyRow[] CompiledRows = null;
        private static readonly string CompiledDigest = null;
        // ---- end of the production table

        private static readonly string[] RowMembers =
        {
            "case", "id", "legacyScenarioSha256", "pack", "packSha256", "qualificationStatus", "scenarioId", "scenarioSha256", "ticks",
        };

        private static readonly object Gate = new object();
        private static bool _verified;
        private static string _defect;

        /// <summary>
        /// The drift self-test, once per process (H :230): the embedded registry's bytes against the compiled rows and digest
        /// (<see cref="Verify"/>). Logs "parity registry-integrity ok ..." or "parity registry-integrity internal-defect: ..."
        /// with the run's runId, and answers whether the candidate may go on. Takes no table: the compiled one is the only one.
        /// </summary>
        internal static bool EnsureVerified(ParityLoadedEngine engine, string runId, TextWriter log)
        {
            string defect;
            lock (Gate)
            {
                if (!_verified)
                {
                    _defect = Verify(ResourceBytes(), CompiledRows, CompiledDigest);
                    _verified = true;
                }
                defect = _defect;
            }
            log.WriteLine(defect == null ? ParityRunLog.RegistryIntegrityOkLine(CompiledDigest, engine, runId) : ParityRunLog.RegistryIntegrityDefectLine(defect, runId));
            return defect == null;
        }

        /// <summary>The embedded registry's bytes, or null when the build embeds none (every build before C8).</summary>
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
