using AgentBroker.Worker;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class HandoffClientChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: HANDOFF_CLIENT_EXECUTED");
        using var scratch = OwnedScratch.Create("handoff-client-" + Guid.NewGuid().ToString("N"));
        string absent = Path.Combine(scratch.Root, "never-opened", "artifact.txt");
        string summary = "first actual line\nC:\\literal\\file\n雪🙂\tend";
        var packet = new HandoffPacket(HandoffPacket.Version, "review-42", "request", summary,
            [new("evidence", absent, new string('a', 64), 0)]);
        bool metadataOnly;
        try { _ = packet.ToText(); metadataOnly = true; }
        catch (IOException) { metadataOnly = false; }
        check(metadataOnly, "HANDOFF_METADATA_ONLY: preparation succeeds without opening nonexistent artifact paths");
        string lockedArtifact = Path.Combine(scratch.Root, "locked-artifact.txt");
        await File.WriteAllTextAsync(lockedArtifact, "owned mismatching artifact", new UTF8Encoding(false));
        bool lockedMetadataOnly = false;
        using (var locked = new FileStream(lockedArtifact, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                string prepared = (packet with { Artifacts = [new("evidence", lockedArtifact, new string('A', 64), 0)] }).ToText();
                lockedMetadataOnly = HandoffPacket.Parse(Encoding.UTF8.GetBytes(prepared)).Artifacts[0].Path == lockedArtifact;
            }
            catch (IOException) { }
        }
        check(lockedMetadataOnly && await File.ReadAllTextAsync(lockedArtifact) == "owned mismatching artifact",
            "HANDOFF_EXISTING_ARTIFACT_METADATA_ONLY: even an existing exclusively locked mismatching artifact is never opened");
        string text = packet.ToText();
        var parsed = HandoffPacket.Parse(Encoding.UTF8.GetBytes(text));
        check(parsed.TaskId == packet.TaskId && parsed.Summary == summary && parsed.Artifacts[0].Path == absent &&
            parsed.Artifacts[0].Sha256 == new string('A', 64) && parsed.Artifacts[0].Bytes == 0,
            "HANDOFF_CODEC_ROUNDTRIP: typed text preserves task, newlines, paths, Unicode and zero bytes");
        check(!Directory.Exists(Path.GetDirectoryName(absent)), "HANDOFF_ARTIFACT_UNTOUCHED: nonexistent artifact paths are not created");
        foreach (string state in new[] { "request", "progress", "result", "cannot_do" })
        {
            var value = packet with { State = state, Artifacts = [] };
            check(HandoffPacket.Parse(Encoding.UTF8.GetBytes(value.ToText())).State == state,
                "HANDOFF_STATES: each explicit task state round-trips with an empty artifact list");
        }
        string valid = JsonSerializer.Serialize(packet, Json);
        string Without(string property, bool nested)
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(valid)!;
            var fields = nested ? root["artifacts"]![0]!.AsObject() : root.AsObject();
            if (!fields.Remove(property)) throw new InvalidOperationException("Synthetic missing-field fixture did not remove its field.");
            return root.ToJsonString();
        }
        foreach (var (bad, name) in new[]
        {
            (valid.Replace("\"kind\":", "\"kind\":\"agentbroker.handoff.v1\",\"kind\":", StringComparison.Ordinal), "duplicate"),
            (valid.Replace("\"kind\":", "\"Kind\":\"agentbroker.handoff.v1\",\"kind\":", StringComparison.Ordinal), "case-duplicate"),
            (valid[..^1] + ",\"unexpected\":true}", "unknown"),
            (valid.Replace("\"bytes\":0", "\"bytes\":0,\"role\":\"other\"", StringComparison.Ordinal), "nested-duplicate"),
            (valid.Replace("\"bytes\":0", "\"bytes\":0,\"extra\":true", StringComparison.Ordinal), "nested-unknown"),
            (valid.Replace("\"bytes\":0", "\"bytes\":-1", StringComparison.Ordinal), "negative-bytes"),
            (valid.Replace("\"bytes\":0", "\"bytes\":0.5", StringComparison.Ordinal), "fractional-bytes"),
            (valid.Replace("\"bytes\":0", "\"bytes\":\"0\"", StringComparison.Ordinal), "string-bytes"),
            (valid.Replace("\"bytes\":0", "\"bytes\":9223372036854775808", StringComparison.Ordinal), "overflow-bytes"),
            (valid.Replace(new string('a', 64), new string('g', 64), StringComparison.Ordinal), "invalid-hash"),
            (valid.Replace(new string('a', 64), new string('a', 63), StringComparison.Ordinal), "short-hash"),
            (valid.Replace("\"request\"", "\"complete\"", StringComparison.Ordinal), "invalid-state"),
            (valid.Replace("agentbroker.handoff.v1", "agentbroker.handoff.v2", StringComparison.Ordinal), "unknown-version"),
            (Without("summary", false), "missing-root-field"),
            (Without("role", true), "missing-artifact-field"),
            (valid.Replace("review-42", string.Concat((char)92, "uD800"), StringComparison.Ordinal), "unpaired-surrogate"),
            // A lone surrogate in a property NAME (root and nested), not only in a value: names transcode lazily (1.10.1).
            ("{\"" + string.Concat((char)92, "uD800") + "\":1," + valid[1..], "name-surrogate"),
            (valid.Replace("\"bytes\":0", "\"bytes\":0,\"" + string.Concat((char)92, "uD800") + "\":1", StringComparison.Ordinal), "nested-name-surrogate"),
            ("[]", "array-root"), ("{}", "empty-root"), ("null", "null-root"), (valid + "x", "trailing-data"),
        })
            check(Rejected(() => HandoffPacket.Parse(Encoding.UTF8.GetBytes(bad))),
                "HANDOFF_STRICT_" + name.ToUpperInvariant() + ": malformed metadata is refused");
        byte[] rawName = [.. Encoding.UTF8.GetBytes("{\""), 0xFF, .. Encoding.UTF8.GetBytes("\":1," + valid[1..])];
        check(Rejected(() => HandoffPacket.Parse(rawName)),
            "HANDOFF_STRICT_NAME_INVALID_UTF8: a property name that is not UTF-8 is refused as invalid metadata");
        foreach (var (bad, name) in new[]
        {
            (packet with { TaskId = new string('x', 129) }, "TASK_BOUND"),
            (packet with { Summary = new string('x', 8193) }, "SUMMARY_BOUND"),
            (packet with { TaskId = "\nclaimed" }, "TASK_CONTROL"),
            (packet with { TaskId = new string((char)0xD800, 1) }, "CONSTRUCTION_UNICODE"),
            (packet with { Artifacts = [packet.Artifacts[0] with { Path = new string((char)0xD800, 1) }] }, "ARTIFACT_PATH_UNICODE"),
            (packet with { Artifacts = [packet.Artifacts[0] with { Role = new string((char)0xD800, 1) }] }, "ARTIFACT_ROLE_UNICODE"),
            (packet with { Artifacts = [packet.Artifacts[0] with { Sha256 = new string('A', 63) + (char)0xD800 }] }, "ARTIFACT_HASH_INVALID_UNICODE"),
            (packet with { Summary = "bad\0summary" }, "SUMMARY_CONTROL"),
            (packet with { State = "done" }, "CONSTRUCTION_VALIDATED"),
            (packet with { Artifacts = Enumerable.Repeat(packet.Artifacts[0], 17).ToArray() }, "ARTIFACT_COUNT"),
            (packet with { Artifacts = [packet.Artifacts[0] with { Role = new string('x', 41) }] }, "ROLE_BOUND"),
            (packet with { Artifacts = [packet.Artifacts[0] with { Path = new string('x', 4097) }] }, "PATH_BOUND"),
            (packet with { Artifacts = [packet.Artifacts[0] with { Path = "C:\\bad\npath" }] }, "PATH_CONTROL"),
        })
            check(Rejected(() => bad.ToText()), "HANDOFF_" + name + ": public construction keeps the strict codec contract");
        foreach (var (atLimit, name) in new[]
        {
            (packet with { TaskId = new string('x', 128) }, "TASK"),
            (packet with { Summary = new string('x', 8192) }, "SUMMARY"),
            (packet with { Artifacts = [packet.Artifacts[0] with { Role = new string('x', 40) }] }, "ROLE"),
            (packet with { Artifacts = [packet.Artifacts[0] with { Path = new string('x', 4096) }] }, "PATH"),
            (packet with { Artifacts = Enumerable.Repeat(new HandoffArtifact("x", "x", new string('A', 64), 0), 16).ToArray() }, "COUNT")
        })
            check(Accepted(() => atLimit.ToText()), "HANDOFF_AT_LIMIT_" + name + ": a field at its declared limit remains valid");
        byte[] padded = new byte[HandoffPacket.MaximumInputBytes], oversized = new byte[HandoffPacket.MaximumInputBytes + 1];
        padded.AsSpan().Fill((byte)' '); oversized.AsSpan().Fill((byte)' ');
        byte[] validBytes = Encoding.UTF8.GetBytes(valid);
        validBytes.CopyTo(padded, 0); validBytes.CopyTo(oversized, 0);
        check(Accepted(() => HandoffPacket.Parse(padded)),
            "HANDOFF_INPUT_EXACT_BOUND: valid whitespace-padded JSON at the raw input limit is accepted");
        check(Rejected(() => HandoffPacket.Parse(oversized)),
            "HANDOFF_INPUT_BOUND: otherwise-valid JSON one byte over the raw input limit is refused");
        string multilingualSummary = new string('日', 3000) + " <tag>&'\"+\n";
        string? multilingual = null;
        try { multilingual = (packet with { Summary = multilingualSummary, Artifacts = [] }).ToText(); }
        catch (InvalidDataException) { }
        check(multilingual is not null && multilingual.Contains(new string('日', 3000), StringComparison.Ordinal) &&
            Encoding.UTF8.GetByteCount(multilingual) < HandoffPacket.MaximumTextBytes &&
            HandoffPacket.Parse(Encoding.UTF8.GetBytes(multilingual)).Summary == multilingualSummary,
            "HANDOFF_MULTILINGUAL_UTF8: ordinary JSON message text preserves multilingual content within its actual UTF-8 budget");
        var unicodeEnvelope = packet with { Summary = "日", Artifacts = [] };
        int unicodeRoom = HandoffPacket.MaximumTextBytes - (Encoding.UTF8.GetByteCount(unicodeEnvelope.ToText()) - 3);
        var unicodeBoundary = unicodeEnvelope with { Summary = new string('日', unicodeRoom / 3) + new string('x', unicodeRoom % 3) };
        check(Encoding.UTF8.GetByteCount(unicodeBoundary.ToText()) == HandoffPacket.MaximumTextBytes,
            "HANDOFF_MULTILINGUAL_EXACT_BOUND: multilingual message text can fill exactly sixteen KiB");
        check(Rejected(() => (unicodeBoundary with { Summary = unicodeBoundary.Summary + "x" }).ToText()),
            "HANDOFF_MULTILINGUAL_BYTE_BOUND: one additional UTF-8 byte exceeds the same text budget");
        var envelope = packet with { Summary = "x", Artifacts = Enumerable.Range(0, 3).Select(index =>
            new HandoffArtifact("evidence", new string((char)('a' + index), 4000), new string('A', 64), long.MaxValue)).ToArray() };
        int padding = HandoffPacket.MaximumTextBytes - Encoding.UTF8.GetByteCount(envelope.ToText());
        var boundary = envelope with { Summary = new string('x', 1 + padding) };
        check(Encoding.UTF8.GetByteCount(boundary.ToText()) == HandoffPacket.MaximumTextBytes,
            "HANDOFF_TEXT_EXACT_BOUND: a valid packet may fill the broker's text budget exactly");
        check(Rejected(() => (boundary with { Summary = boundary.Summary + "x" }).ToText()),
            "HANDOFF_TEXT_BYTE_BOUND: canonical message text over sixteen KiB is refused");

        string request = Path.Combine(scratch.Root, "packet.json"), retained = Path.Combine(scratch.Root, "packet.txt");
        await File.WriteAllTextAsync(request, valid, new UTF8Encoding(false));
        var child = await ChildAsync(check, "--handoff-prepare", "--request-file", request, "--output-file", retained);
        // Guarded read: a failed publication fails the named check below, not an unqualified FileNotFoundException.
        byte[] saved = File.Exists(retained) ? await File.ReadAllBytesAsync(retained) : [];
        check(child.Code == 0 && child.Value.GetProperty("status").GetString() == "ok" &&
            child.Value.GetProperty("dispatchState").GetString() == "not_dispatched" &&
            child.Value.GetProperty("text").GetString() == text && Encoding.UTF8.GetString(saved) == text,
            "HANDOFF_PROCESS_PREPARE: the actual CLI prepares ordinary message text and saves the exact payload");
        check(child.Value.GetProperty("textBytes").GetInt32() == saved.Length &&
            child.Value.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(saved)) &&
            child.Value.GetProperty("retention").GetProperty("path").GetString() == retained,
            "HANDOFF_PROCESS_HASH: saved payload bytes and SHA-256 match the report");
        string boundedRequest = Path.Combine(scratch.Root, "bounded.json");
        await File.WriteAllBytesAsync(boundedRequest, padded);
        var atInputLimit = await ChildAsync(check, "--handoff-prepare", "--request-file", boundedRequest);
        check(atInputLimit.Code == 0 && atInputLimit.Value.GetProperty("text").GetString() == text,
            "HANDOFF_PROCESS_INPUT_EXACT_BOUND: the actual CLI accepts valid JSON padded to its input limit");
        await File.WriteAllBytesAsync(boundedRequest, oversized);
        var aboveInputLimit = await ChildAsync(check, "--handoff-prepare", "--request-file", boundedRequest);
        check(aboveInputLimit.Code == 2 && aboveInputLimit.Value.GetProperty("status").GetString() == "invalid_handoff",
            "HANDOFF_PROCESS_INPUT_BOUND: the actual CLI refuses otherwise-valid oversized input");
        var collision = await ChildAsync(check, "--handoff-prepare", "--request-file", request, "--output-file", retained);
        check(collision.Code == 74 && collision.Value.GetProperty("status").GetString() == "output_error" &&
            (await File.ReadAllBytesAsync(retained)).SequenceEqual(saved),
            "HANDOFF_OUTPUT_NO_OVERWRITE: an existing payload is preserved and output failure exits seventy-four");
        var missingParent = await ChildAsync(check, "--handoff-prepare", "--request-file", request, "--output-file", Path.Combine(scratch.Root, "missing", "packet.txt"));
        check(missingParent.Code == 74 && !Directory.Exists(Path.Combine(scratch.Root, "missing")),
            "HANDOFF_OUTPUT_PARENT: preparation does not create an output parent implicitly");
        await File.WriteAllTextAsync(request, valid[..^1] + ",\"extra\":true}", new UTF8Encoding(false));
        var malformed = await ChildAsync(check, "--handoff-prepare", "--request-file", request);
        check(malformed.Code == 2 && malformed.Value.GetProperty("status").GetString() == "invalid_handoff",
            "HANDOFF_PROCESS_INVALID: the real CLI refuses invalid handoff JSON");
        foreach (var (missingField, name) in new[] { (Without("summary", false), "ROOT"), (Without("role", true), "ARTIFACT") })
        {
            await File.WriteAllTextAsync(request, missingField, new UTF8Encoding(false));
            var missingFieldResult = await ChildAsync(check, "--handoff-prepare", "--request-file", request);
            check(missingFieldResult.Code == 2 && missingFieldResult.Value.GetProperty("status").GetString() == "invalid_handoff",
                "HANDOFF_PROCESS_MISSING_" + name + ": a genuinely absent required field returns the public invalid-handoff contract");
        }
        var arguments = await ChildAsync(check, "--handoff-prepare", "--request-file", "relative.json");
        check(arguments.Code == 2 && arguments.Value.GetProperty("status").GetString() == "invalid_arguments",
            "HANDOFF_PROCESS_ARGUMENTS: a relative request is rejected before file access");

        // 1.10.1: a bad property NAME is a coded refusal (it crashed with exit 1 and no JSON at 1.10.0).
        await File.WriteAllTextAsync(request, "{\"" + string.Concat((char)92, "uD800") + "\":1," + valid[1..], new UTF8Encoding(false));
        var badName = await ChildAsync(check, "--handoff-prepare", "--request-file", request);
        check(badName.Code == 2 && badName.Value.GetProperty("status").GetString() == "invalid_handoff" &&
            badName.Value.GetProperty("dispatchState").GetString() == "not_dispatched",
            "HANDOFF_PROCESS_INVALID_NAME: a lone surrogate in a property name returns the public invalid-handoff contract");
        // 1.10.1: one UTF-8 signature (Windows PowerShell 5.1 writes it) is an encoding mark, not JSON; a second one is not.
        await File.WriteAllTextAsync(request, valid, new UTF8Encoding(true));
        var signed = await ChildAsync(check, "--handoff-prepare", "--request-file", request);
        check(signed.Code == 0 && signed.Value.GetProperty("text").GetString() == text,
            "HANDOFF_PROCESS_UTF8_SIGNATURE: a request file that starts with one UTF-8 signature is prepared normally");
        await File.WriteAllBytesAsync(request, [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(valid)]);
        var doubleSigned = await ChildAsync(check, "--handoff-prepare", "--request-file", request);
        check(doubleSigned.Code == 2 && doubleSigned.Value.GetProperty("status").GetString() == "invalid_handoff",
            "HANDOFF_PROCESS_UTF8_SIGNATURE_ONCE: only one leading signature is dropped; a second remains invalid JSON");
        // The request-path guards (HandoffPrepareClient): the .json extension and the reparse-free directory chain.
        string wrongExtension = Path.Combine(scratch.Root, "packet-request.txt");
        await File.WriteAllTextAsync(wrongExtension, valid, new UTF8Encoding(false));
        var extension = await ChildAsync(check, "--handoff-prepare", "--request-file", wrongExtension);
        check(extension.Code == 2 && extension.Value.GetProperty("status").GetString() == "invalid_arguments",
            "HANDOFF_PROCESS_REQUEST_EXTENSION: a request file without the .json extension is refused before it is read");
        string requestTarget = Path.Combine(scratch.Root, "request-target"), requestJunction = Path.Combine(scratch.Root, "request-junction");
        Directory.CreateDirectory(requestTarget);
        await File.WriteAllTextAsync(Path.Combine(requestTarget, "packet.json"), valid, new UTF8Encoding(false));
        CreateJunction(requestJunction, requestTarget);
        try
        {
            var throughJunction = await ChildAsync(check, "--handoff-prepare", "--request-file", Path.Combine(requestJunction, "packet.json"));
            check(throughJunction.Code == 2 && throughJunction.Value.GetProperty("status").GetString() == "invalid_handoff",
                "HANDOFF_PROCESS_REQUEST_REPARSE: a request read through a junction is refused");
        }
        finally { Directory.Delete(requestJunction); }

        OutputPublicationChecks(check, scratch.Root, "HANDOFF_OUTPUT");
        check(!Directory.EnumerateFiles(scratch.Root, ".agentbroker-output-*.tmp", SearchOption.AllDirectories).Any() &&
            !Directory.Exists(Path.GetDirectoryName(absent)),
            "HANDOFF_OWNED_CLEANUP: temporary output files are absent and artifact paths remain untouched");
    }

    internal static void OutputPublicationChecks(Action<bool, string> check, string root, string prefix)
    {
        string directory = Path.Combine(root, "publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string raced = Path.Combine(directory, "publish-race.json");
        bool publicationRefused = Rejected(() => LocalOutputFile.Save(raced, "payload"u8.ToArray(),
            () => File.WriteAllText(raced, "raced destination", new UTF8Encoding(false))));
        check(publicationRefused && File.ReadAllText(raced) == "raced destination",
            prefix + "_ATOMIC_NO_REPLACE: a destination created after the temporary file is flushed wins without overwrite");
        check(!Directory.EnumerateFiles(directory, ".agentbroker-output-*.tmp").Any(),
            prefix + "_PUBLICATION_TEMP_CLEANUP: a failed post-flush publication removes its actual owned temporary");

        foreach (var (path, label) in new[]
        {
            (Path.Combine(directory, "ads.txt") + ":stream", "ADS"),
            (Path.Combine(directory, "CON.txt"), "DEVICE"),
            (Path.Combine(directory, "trailing."), "TRAILING_DOT"),
            (Path.Combine(directory, "trailing "), "TRAILING_SPACE"),
            (@"\\server\share\output.json", "UNC"),
        })
            check(Rejected(() => LocalOutputFile.Validate(path)),
                prefix + "_PATH_" + label + ": the explicit output path policy refuses this shape");
        string aliasParent = Path.Combine(directory, "alias-parent");
        Directory.CreateDirectory(aliasParent);
        string navigated = Path.Combine(directory, ".", "alias-parent", "..", "navigation.json");
        check(Accepted(() =>
        {
            if (LocalOutputFile.Validate(navigated) != Path.Combine(directory, "navigation.json"))
                throw new InvalidDataException("Unexpected navigation resolution.");
        }), prefix + "_PATH_NAVIGATION: exact dot and dotdot components still resolve to an existing plain parent");
        foreach (var (component, label) in new[] { ("alias-parent.", "DOT"), ("alias-parent ", "SPACE") })
        {
            // The alias target exists, and dotdot consumes it during normalization. A missing parent cannot mask this guard.
            string rawParent = Path.Combine(directory, component, "..", "raw-parent-" + label + ".json");
            check(Rejected(() => LocalOutputFile.Validate(rawParent)),
                prefix + "_PATH_RAW_PARENT_" + label + ": an ambiguous raw parent is refused before navigation erases its name");
        }
        string parentFile = Path.Combine(directory, "file-parent");
        File.WriteAllText(parentFile, "owned file", new UTF8Encoding(false));
        check(Rejected(() => LocalOutputFile.Validate(Path.Combine(parentFile, "output.json"))),
            prefix + "_PATH_FILE_PARENT: an output parent must be an existing directory");

        string target = Path.Combine(directory, "target"), junction = Path.Combine(directory, "junction");
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);
        try
        {
            check(Rejected(() => LocalOutputFile.Save(Path.Combine(junction, "output.json"), "{}"u8.ToArray())) &&
                !File.Exists(Path.Combine(target, "output.json")),
                prefix + "_REPARSE: an explicit output through a junction is refused without writing its target");
        }
        finally { Directory.Delete(junction); }

        // RETIRED-REPLACED (1.10.1, LocalOutputFile design v2.1): _POSTFLUSH_PARENT and _POSTFLUSH_CLEANUP_SCOPE renamed the
        // output parent during publication and replaced it with a junction. The held parent handle (FILE_TRAVERSE, no
        // FILE_SHARE_DELETE) and NTFS's open-file rule (MS-FSA 2.1.4.2) now make the file system refuse that premise, so the
        // fixture can no longer run. They are replaced by LOCAL_OUTPUT_HELD_DENIES_DELETE, LOCAL_OUTPUT_FINAL_PATH_MISMATCH,
        // LOCAL_OUTPUT_TEMP_OUTSIDE_HELD and LOCAL_OUTPUT_SOURCE_NO_PATH_DESTRUCTION (--local-output-only). No equivalence is
        // claimed: the new checks prove the new mechanism, and none renames, moves or swaps a directory.
    }

    internal static void CreateJunction(string junction, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction); start.ArgumentList.Add(target);
        using (var process = Process.Start(start) ?? throw new IOException("Output junction fixture did not start."))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new IOException("Output junction fixture timed out."); }
            stdout.GetAwaiter().GetResult(); stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new IOException("Output junction fixture failed.");
        }
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception error) { return error is InvalidDataException or JsonException or IOException; }
    }

    private static bool Accepted(Action action)
    {
        try { action(); return true; }
        catch { return false; }
    }

    internal static async Task<(int Code, JsonElement Value)> ChildAsync(Action<bool, string> check, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(typeof(WorkerApplication).Assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Handoff child did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        string text = await stdout, errors = await stderr;
        check(errors.Length == 0 && text.Count(character => character == '\n') == 1,
            "HANDOFF_PROCESS_FRAME: actual child returns one JSON line and no stderr");
        using var document = JsonDocument.Parse(text);
        return (process.ExitCode, document.RootElement.Clone());
    }
}
