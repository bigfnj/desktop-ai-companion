using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// The byte-level candidate scanner of signed revision H (src/dotNet/Shimeji/ParityScenarioScan.cs): every vector
    /// asserts the three scan facts, candidate, stop reason and absolute byte offset, and nothing else.
    ///
    /// Two tables. The first is the 33 normative vectors of revision H's review/SCANNER-CONTROLS.json, copied with
    /// their expected facts unchanged (generated from the file, not retyped). That file says its vectors are hand-derived
    /// and were never executed, so a disagreement between the scanner and a row goes to the policy owner through the
    /// coordinator; it is never settled by editing the expected value here. Until one vector file shared by the host,
    /// the comparer and the oracle exists, this copy carries the pin below, and a content digest the generator took from
    /// the pinned file guards every copied row, so an edited row fails even while its own vector passes. The second
    /// table holds the vectors revision H names that the controls file lacks, each derived by hand from the cited line;
    /// a none result is at input length by H :60, so those rows state the length.
    ///
    /// Each vector is its own group, "Parity scan vector NAME", so a mutation case names the vector it must fail. Each
    /// group first runs two WITNESS legs, a clean candidate and a clean no-candidate that both stop none at input
    /// length, so a vector's negative fact is never read from a scanner that cannot say yes, and its positive fact
    /// never from one that cannot say no.
    /// </summary>
    internal static partial class Program
    {
        private sealed class ParityScanVector
        {
            public readonly string Name;
            public readonly string Hex;
            public readonly bool Candidate;
            public readonly string Reason;
            public readonly int Offset;

            public ParityScanVector(string name, string hex, bool candidate, string reason, int offset)
            {
                Name = name;
                Hex = hex;
                Candidate = candidate;
                Reason = reason;
                Offset = offset;
            }
        }

        // review/SCANNER-CONTROLS.json of the revision H packet, sha256
        // 7e8cea721dec72e69811110f9c1d507bc47675960f4d64400c2121fbc119d0b1, its 33 vectors in file order.
        private static readonly ParityScanVector[] ParityScanControls =
        {
            new ParityScanVector("before-marker-NaN",
                "7b2261223a4e614e2c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "lexical", 5),
            new ParityScanVector("before-marker-Infinity",
                "7b2261223a496e66696e6974792c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "lexical", 5),
            new ParityScanVector("before-marker--Infinity",
                "7b2261223a2d496e66696e6974792c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "lexical", 6),
            new ParityScanVector("before-marker-01",
                "7b2261223a30312c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "lexical", 6),
            new ParityScanVector("after-marker-minus-infinity",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32222c202261223a2d496e66696e6974797d",
                true, "lexical", 44),
            new ParityScanVector("after-root-invalid-raw-byte",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227dff",
                true, "utf8", 38),
            new ParityScanVector("after-root-nonascii-scalar",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227dc2a0",
                true, "lexical", 38),
            new ParityScanVector("after-root-ascii-junk",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d78",
                true, "lexical", 38),
            new ParityScanVector("complete-with-trailing-whitespace",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d200d0a09",
                true, "none", 42),
            new ParityScanVector("completed-marker-before-eof",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f3222",
                true, "eof", 37),
            new ParityScanVector("unfinished-marker-string",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32",
                false, "eof", 36),
            new ParityScanVector("six-raw-byte-unpaired-root-name",
                "7b225c7544383030223a312c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                true, "none", 49),
            new ParityScanVector("twenty-five-raw-byte-unpaired-marker-value",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e5c7544383030227d",
                false, "none", 38),
            new ParityScanVector("before-marker-raw-ff",
                "7b22ff223a312c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "utf8", 2),
            new ParityScanVector("before-marker-encoded-surrogate",
                "7b22eda080223a312c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "utf8", 2),
            new ParityScanVector("before-marker-overlong",
                "7b22c0af223a312c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "utf8", 2),
            new ParityScanVector("before-marker-above-max",
                "7b22f4908080223a312c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "utf8", 2),
            new ParityScanVector("truncated-multibyte-at-eof",
                "7b2278223a22e282",
                false, "utf8", 6),
            new ParityScanVector("raw-utf8-fault-before-invalid-escape",
                "7b2278223a22ff5c71222c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "utf8", 6),
            new ParityScanVector("earlier-json-stop-before-later-utf8",
                "21ff7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "lexical", 0),
            new ParityScanVector("leading-complete-bom",
                "efbbbf7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "bom", 0),
            new ParityScanVector("root-plus-63-arrays-depth64",
                "7b2278223a5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b305d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d7d",
                false, "none", 133),
            new ParityScanVector("root-plus-64-arrays-depth65",
                "7b2278223a5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b305d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d7d",
                false, "depth", 68),
            new ParityScanVector("marker-before-depth65",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32222c2278223a5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b305d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d7d",
                true, "depth", 105),
            new ParityScanVector("nonobject-root-with-nested-lookalike",
                "5b7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d5d",
                false, "none", 40),
            new ParityScanVector("empty-input",
                "",
                false, "eof", 0),
            new ParityScanVector("whitespace-only",
                "2020",
                false, "eof", 2),
            new ParityScanVector("multibyte-cut-short-by-string-quote",
                "7b2278223a22e282227d",
                false, "utf8", 6),
            new ParityScanVector("depth64-number-then-forbidden-array",
                "5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b315b",
                false, "lexical", 65),
            new ParityScanVector("depth64-object-forbidden-array-name",
                "5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b7b5b",
                false, "lexical", 64),
            new ParityScanVector("depth64-value-position-array65",
                "5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b",
                false, "depth", 64),
            new ParityScanVector("candidate-with-unpaired-pack-value",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32222c227061636b223a225c7544383030227d",
                true, "none", 54),
            new ParityScanVector("marker-before-depth64-forbidden-array",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32222c2278223a5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b315b",
                true, "lexical", 106),
        };

        // The generator's sha256 of the pinned file's 33 rows, computed from SCANNER-CONTROLS.json itself and not from the
        // table above: per row, name, inputHex, candidate, stopReason and offset, tab-separated, one LF-terminated line
        // per row in file order (ParityScanContentSha256 writes the table the same way).
        private const string ParityScanControlsContentSha256 = "96edfdf4a4a7ccaab17c8634ae15146b8beddf563f66c2b01fb638fcf2a6bc85";

        // Revision H vectors the controls file lacks (DECLARATION-DRAFT.md c3c261fd, lines cited per row).
        private static readonly ParityScanVector[] ParityScanHostVectors =
        {
            // H :56, :195: 1e100000 is a valid number token, no float-range stop; the later marker counts.
            new ParityScanVector("host-huge-exponent-before-marker",
                "7b2261223a31653130303030302c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                true, "none", 51),
            // H :191: NaN after a completed marker is lexical at N and the marker is remembered.
            new ParityScanVector("host-after-marker-NaN",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32222c2261223a4e614e7d",
                true, "lexical", 42),
            // H :50: duplicates are not collapsed; the earlier completed marker is sticky.
            new ParityScanVector("host-duplicate-schema-v2-then-v1",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32222c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f31227d",
                true, "none", 75),
            // H :50: the later marker is a candidate too.
            new ParityScanVector("host-duplicate-schema-v1-then-v2",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f31222c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                true, "none", 75),
            // H :48, :56: the DECODED name is compared, so an escaped e still spells schema.
            new ParityScanVector("host-escaped-schema-name",
                "7b227363685c75303036356d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                true, "none", 43),
            // H :56: an escaped solidus decodes to the marker's slash.
            new ParityScanVector("host-escaped-solidus-in-marker",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f5c2f32227d",
                true, "none", 39),
            // H :48: ordinal and case-sensitive, so Schema is not schema.
            new ParityScanVector("host-case-folded-schema-name",
                "7b22536368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "none", 38),
            // H :48: only an immediate root member is a candidate site.
            new ParityScanVector("host-nested-object-lookalike",
                "7b2261223a7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d7d",
                false, "none", 44),
            // H :48: an object-valued schema member is no completed STRING marker.
            new ParityScanVector("host-object-valued-schema-lookalike",
                "7b22736368656d61223a7b2276223a227368696d656a692d7061726974792d7363656e6172696f2f32227d7d",
                false, "none", 44),
            // H :48: the marker spelled inside a string value is text, not a member.
            new ParityScanVector("host-string-description-lookalike",
                "7b226465736372697074696f6e223a225c22736368656d615c223a5c227368696d656a692d7061726974792d7363656e6172696f2f325c22227d",
                false, "none", 58),
            // H :195: schema then an unpaired low surrogate is a different, longer name; unequal, no stop.
            new ParityScanVector("host-longer-surrogate-root-name",
                "7b22736368656d615c7544433030223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "none", 44),
            // H :195: a different-length unpaired name is unequal without stopping; the later marker counts.
            new ParityScanVector("host-seven-raw-byte-surrogate-root-name",
                "7b22735c7544383030223a312c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                true, "none", 50),
            // H :56: a paired escape is two code units, unequal to schema; the later marker counts.
            new ParityScanVector("host-paired-surrogate-root-name-then-marker",
                "7b225c75443833445c7544453030223a312c22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                true, "none", 55),
            // H :195: the marker followed by an unpaired surrogate is a longer value; unequal, no stop.
            new ParityScanVector("host-longer-surrogate-marker-value",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f325c7544433030227d",
                false, "none", 44),
            // H :48: a proper prefix of the marker is not the marker.
            new ParityScanVector("host-shorter-marker-value",
                "7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f227d",
                false, "none", 37),
            // H :60: only a COMPLETE initial BOM is bom; EF BB cut short by { is utf8 at its first byte.
            new ParityScanVector("host-partial-bom",
                "efbb7b22736368656d61223a227368696d656a692d7061726974792d7363656e6172696f2f32227d",
                false, "utf8", 0),
            // H :48: a non-object root never yields a candidate and is scanned to the end.
            new ParityScanVector("host-string-root",
                "227368696d656a692d7061726974792d7363656e6172696f2f3222",
                false, "none", 27),
            // H :48, :56: a non-object number root, lexed whole, none at input length.
            new ParityScanVector("host-number-root",
                "2d302e35652b33",
                false, "none", 7),
        };

        private static void RunParityScanGroups()
        {
            Run("Parity scan controls: the pinned table holds all 33 vectors", TestParityScanControlCount);
            Run("Parity scan controls: the copied rows hash to the pinned file's content digest", TestParityScanControlContent);
            foreach (ParityScanVector vector in ParityScanControls)
                Run("Parity scan vector " + vector.Name, () => TestParityScanVector(vector));
            foreach (ParityScanVector vector in ParityScanHostVectors)
                Run("Parity scan vector " + vector.Name, () => TestParityScanVector(vector));
        }

        private static void TestParityScanControlCount()
        {
            AssertEqual(33, ParityScanControls.Length, "The SCANNER-CONTROLS.json copy lost or gained a row.");
        }

        private static void TestParityScanControlContent()
        {
            AssertEqual(ParityScanControlsContentSha256, ParityScanContentSha256(ParityScanControls, -1),
                "The SCANNER-CONTROLS.json copy no longer holds the pinned file's rows: a name, an input or an expected fact changed.");
            // WITNESS: the digest sees one changed expected fact, so a match is read from a digest that can differ.
            AssertTrue(!string.Equals(ParityScanControlsContentSha256, ParityScanContentSha256(ParityScanControls, 0), StringComparison.Ordinal),
                "WITNESS: changing the first row's offset left the content digest unchanged.");
        }

        /// <summary>The table's content digest, written as the generator writes the file's rows. A row index of 0 or more
        /// adds 1 to that row's offset first (the WITNESS); -1 changes nothing.</summary>
        private static string ParityScanContentSha256(ParityScanVector[] rows, int bumpOffsetRow)
        {
            var text = new StringBuilder();
            for (int index = 0; index < rows.Length; index++)
            {
                ParityScanVector row = rows[index];
                int offset = index == bumpOffsetRow ? row.Offset + 1 : row.Offset;
                text.Append(row.Name).Append('\t').Append(row.Hex).Append('\t').Append(row.Candidate ? "true" : "false")
                    .Append('\t').Append(row.Reason).Append('\t').Append(offset.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
            var hex = new StringBuilder(digest.Length * 2);
            foreach (byte value in digest) hex.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }

        private static void TestParityScanVector(ParityScanVector vector)
        {
            // Both 38 bytes long; a complete document stops none at input length (H :60).
            byte[] clean = Encoding.ASCII.GetBytes("{\"schema\":\"shimeji-parity-scenario/2\"}");
            byte[] legacy = Encoding.ASCII.GetBytes("{\"schema\":\"shimeji-parity-scenario/1\"}");
            AssertParityScan("WITNESS clean candidate", clean, true, ParityScenarioScan.ReasonNone, clean.Length);
            AssertParityScan("WITNESS clean no-candidate", legacy, false, ParityScenarioScan.ReasonNone, legacy.Length);
            AssertParityScan("vector", ParityScanBytes(vector.Hex), vector.Candidate, vector.Reason, vector.Offset);
        }

        private static void AssertParityScan(string leg, byte[] input, bool candidate, string reason, int offset)
        {
            ParityScenarioScan.Result result = ParityScenarioScan.Scan(input);
            if (result.Candidate == candidate && string.Equals(result.Reason, reason, StringComparison.Ordinal)
                && result.Offset == offset)
                return;
            throw new InvalidOperationException(
                leg + ": expected candidate " + candidate + ", " + reason + " at " +
                offset.ToString(CultureInfo.InvariantCulture) + "; got candidate " + result.Candidate + ", " +
                (result.Reason ?? "(no reason)") + " at " + result.Offset.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private static byte[] ParityScanBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
                bytes[index] = byte.Parse(hex.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
    }
}
