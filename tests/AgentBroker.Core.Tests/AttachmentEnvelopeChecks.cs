using System.Text.Json;
using AgentBroker.Core;

// Lane F (1.10.5): the attachments envelope is plain message text with a strict shape. Each label names the rule a
// mutation of AttachmentEnvelope must break. No file, path or store is touched here.
internal static class AttachmentEnvelopeChecks
{
    public static int Run(string root)
    {
        _ = root;
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        const long MiB = 1024L * 1024;
        string hash = new('a', 64);
        AttachmentEntry Entry(int index, long bytes, string name = "report.txt") => new(index, name, bytes, hash);
        AttachmentParse Parse(string text) => AttachmentEnvelope.TryParse(text, out _);
        // Hand-built JSON for shapes Compose refuses to produce. Anonymous members serialize in declaration order.
        string Raw(object attachments, string text = "hello", string kind = AttachmentEnvelope.Kind) =>
            JsonSerializer.Serialize(new { kind, text, attachments });
        object RawEntry(object index, object name, object bytes, object sha256) => new { index, name, bytes, sha256 };

        // Round trip.
        var four = Enumerable.Range(0, 4).Select(i => Entry(i, 1)).ToArray();
        string composed = AttachmentEnvelope.Compose("Synthetic attachment note.", four);
        Check(AttachmentEnvelope.TryParse(composed, out var parsed) == AttachmentParse.Valid && parsed!.Text == "Synthetic attachment note." &&
            parsed.Attachments.SequenceEqual(four) && parsed.TotalBytes == 4,
            "ATTACHMENT_ENVELOPE_ROUNDTRIP: a composed envelope parses back to the same text and entries");
        Check(Parse(AttachmentEnvelope.Compose("", [Entry(0, 0)])) == AttachmentParse.Valid,
            "ATTACHMENT_ENVELOPE_ROUNDTRIP: an empty note with one empty file is a valid envelope");

        // Bounds.
        Check(Parse(Raw(Enumerable.Range(0, 5).Select(i => RawEntry(i, "report.txt", 1, hash)).ToArray())) == AttachmentParse.Invalid,
            "ATTACHMENT_ENVELOPE_BOUNDS: five files are Invalid");
        Check(Parse(Raw(Array.Empty<object>())) == AttachmentParse.Invalid, "ATTACHMENT_ENVELOPE_BOUNDS: zero files are Invalid");
        Check(Parse(AttachmentEnvelope.Compose("x", [Entry(0, 8 * MiB)])) == AttachmentParse.Valid,
            "ATTACHMENT_ENVELOPE_BOUNDS: exactly 8 MiB in one file is valid");
        Check(Parse(Raw(new[] { RawEntry(0, "report.txt", 8 * MiB + 1, hash) })) == AttachmentParse.Invalid,
            "ATTACHMENT_ENVELOPE_BOUNDS: one byte over 8 MiB in a file is Invalid");
        Check(Parse(AttachmentEnvelope.Compose("x", [Entry(0, 8 * MiB), Entry(1, 8 * MiB)])) == AttachmentParse.Valid,
            "ATTACHMENT_ENVELOPE_BOUNDS: exactly 16 MiB in total is valid");
        Check(Parse(Raw(new[] { RawEntry(0, "a.bin", 8 * MiB, hash), RawEntry(1, "b.bin", 8 * MiB, hash), RawEntry(2, "c.bin", 1, hash) }))
            == AttachmentParse.Invalid, "ATTACHMENT_ENVELOPE_BOUNDS: one byte over 16 MiB in total is Invalid");
        string longNote = new('n', AttachmentEnvelope.MaximumEnvelopeBytes);
        Check(Parse(Raw(new[] { RawEntry(0, "report.txt", 1, hash) }, longNote)) == AttachmentParse.Invalid,
            "ATTACHMENT_ENVELOPE_BOUNDS: an envelope over 16 KiB of text is Invalid");
        bool composeRefused = false;
        try { AttachmentEnvelope.Compose(longNote, [Entry(0, 1)]); }
        catch (ArgumentException) { composeRefused = true; }
        Check(composeRefused, "ATTACHMENT_ENVELOPE_BOUNDS: Compose never produces an envelope a recipient would refuse");

        // Names.
        foreach (string bad in new[] { "..", ".", ".env", "a/b", "a\\b", "CON.txt", "con", "LPT1.log", "COM0", "name.", new string('n', 121), "résumé.pdf", "a b", "" })
        {
            Check(!AttachmentEnvelope.ValidName(bad), "ATTACHMENT_ENVELOPE_NAMES: \"" + bad + "\" is not a broker name");
            Check(Parse(Raw(new[] { RawEntry(0, bad, 1, hash) })) == AttachmentParse.Invalid,
                "ATTACHMENT_ENVELOPE_NAMES: an entry named \"" + bad + "\" makes the envelope Invalid");
            Check(AttachmentEnvelope.ValidName(AttachmentEnvelope.Sanitise(bad)),
                "ATTACHMENT_ENVELOPE_NAMES: Sanitise turns \"" + bad + "\" into a valid name");
        }
        Check(AttachmentEnvelope.ValidName("report-1.txt") && AttachmentEnvelope.ValidName("a_b.C-d") && AttachmentEnvelope.ValidName(new string('n', 120)),
            "ATTACHMENT_ENVELOPE_NAMES: ordinary names are valid");
        Check(AttachmentEnvelope.Sanitise("..\\..\\evil.exe") == "_.._evil.exe" && AttachmentEnvelope.Sanitise("CON.txt") == "_CON.txt" &&
            AttachmentEnvelope.Sanitise(".env") == "env" && AttachmentEnvelope.Sanitise("") == "file" &&
            AttachmentEnvelope.Sanitise("résumé.pdf") == "r_sum_.pdf",
            "ATTACHMENT_ENVELOPE_NAMES: Sanitise maps unsafe names predictably");
        string cut = AttachmentEnvelope.Sanitise(new string('a', 130) + ".txt");
        Check(cut.Length == 120 && cut.EndsWith(".txt", StringComparison.Ordinal),
            "ATTACHMENT_ENVELOPE_NAMES: a long name is cut to 120 characters and keeps its extension");
        Check(AttachmentEnvelope.StoredFileName(Entry(3, 1, "CON")) == "3-CON",
            "ATTACHMENT_ENVELOPE_NAMES: a stored name leads with its index, so it is never a device name");

        // Strict shape.
        Check(Parse("Synthetic ordinary message.") == AttachmentParse.NotEnvelope && Parse("{") == AttachmentParse.NotEnvelope &&
            Parse("[1,2]") == AttachmentParse.NotEnvelope && Parse("{\"kind\":\"other\"}") == AttachmentParse.NotEnvelope &&
            Parse(Raw(new[] { RawEntry(0, "report.txt", 1, hash) }, kind: "agentbroker.attachments.v2")) == AttachmentParse.NotEnvelope,
            "ATTACHMENT_ENVELOPE_STRICT: text without the exact kind is ordinary text");
        Check(Parse(JsonSerializer.Serialize(new { kind = AttachmentEnvelope.Kind, text = "x", attachments = new[] { RawEntry(0, "a.txt", 1, hash) }, extra = 1 }))
            == AttachmentParse.Invalid, "ATTACHMENT_ENVELOPE_STRICT: a claimed kind with an unknown member is Invalid, not plain text");
        // T-1: a VALID one-entry list, so only the duplicate rule (ExactMembers' count) can make these Invalid: once at the top
        // level and once inside the entry. The same texts without the duplicate are Valid (control).
        string entry = "{\"index\":0,\"name\":\"a.txt\",\"bytes\":1,\"sha256\":\"" + hash + "\"}";
        string duplicatedEntry = "{\"index\":0,\"name\":\"a.txt\",\"name\":\"a.txt\",\"bytes\":1,\"sha256\":\"" + hash + "\"}";
        string Shaped(string text, string listed) => "{\"kind\":\"" + AttachmentEnvelope.Kind + "\"," + text + ",\"attachments\":[" + listed + "]}";
        Check(Parse(Shaped("\"text\":\"x\"", entry)) == AttachmentParse.Valid &&
            Parse(Shaped("\"text\":\"x\",\"text\":\"x\"", entry)) == AttachmentParse.Invalid &&
            Parse(Shaped("\"text\":\"x\"", duplicatedEntry)) == AttachmentParse.Invalid,
            "ATTACHMENT_ENVELOPE_STRICT: a duplicated member is Invalid");
        Check(Parse(Raw(new[] { RawEntry(0, "a.txt", 1, hash), RawEntry(2, "b.txt", 1, hash) })) == AttachmentParse.Invalid &&
            Parse(Raw(new[] { RawEntry(1, "a.txt", 1, hash), RawEntry(0, "b.txt", 1, hash) })) == AttachmentParse.Invalid,
            "ATTACHMENT_ENVELOPE_STRICT: indexes run 0..n-1 in order");
        Check(Parse(Raw(new[] { RawEntry(0, "a.txt", 1, new string('A', 64)) })) == AttachmentParse.Invalid &&
            Parse(Raw(new[] { RawEntry(0, "a.txt", 1, new string('a', 63)) })) == AttachmentParse.Invalid,
            "ATTACHMENT_ENVELOPE_STRICT: sha256 is exactly 64 lowercase hex characters");
        Check(Parse(Raw(new[] { RawEntry(0, "a.txt", 1.5, hash) })) == AttachmentParse.Invalid &&
            Parse(Raw(new[] { RawEntry(0, "a.txt", -1, hash) })) == AttachmentParse.Invalid &&
            Parse(Raw(new[] { RawEntry(0, 7, 1, hash) })) == AttachmentParse.Invalid &&
            Parse(Raw(new[] { RawEntry("0", "a.txt", 1, hash) })) == AttachmentParse.Invalid,
            "ATTACHMENT_ENVELOPE_STRICT: members have their exact JSON types");
        Check(Parse(Raw(new[] { RawEntry(0, "a.txt", 1, hash) }, "nul\u0000inside")) == AttachmentParse.Invalid,
            "ATTACHMENT_ENVELOPE_STRICT: a NUL in the note is Invalid");
        Check(AttachmentCodes.IsAdmissionCode(AttachmentCodes.FetchPending) && AttachmentCodes.IsAdmissionCode(AttachmentCodes.Mismatch) &&
            !AttachmentCodes.IsAdmissionCode(AttachmentCodes.SourceInvalid) && !AttachmentCodes.IsAdmissionCode("recipient_unavailable"),
            "ATTACHMENT_ADMISSION_CODES: only the peer admission codes travel in a 409 body");
        return passed;
    }
}
