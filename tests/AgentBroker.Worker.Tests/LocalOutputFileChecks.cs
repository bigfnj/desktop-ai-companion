using AgentBroker.Worker;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// LocalOutputFile handle-owned publication (design v2 section 8.1 as amended by v2.1). Owned scratch only: no check
// renames, moves or swaps a directory, and nothing races. Static junctions are created before a call and removed before
// the check is evaluated. Notes go to stdout; the hygiene runner grades the first stderr line. The group stops at the
// first failure, so the order decides which label a mutation witness names.
internal static class LocalOutputFileChecks
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static void Run(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: LOCAL_OUTPUT_CHECKS_EXECUTED");
        var scratch = OwnedScratch.Create("local-output");
        try { Cases(check, scratch.Root); }
        catch
        {
            // A throwing cleanup would replace the failed check's exception (C# spec 13.10.6), so it is only noted.
            try { scratch.Dispose(); }
            catch (Exception cleanup) { Console.WriteLine("SCRATCH_RETAINED_AFTER_FAILURE: " + cleanup.GetType().Name + ": " + cleanup.Message); }
            throw;
        }
        scratch.Dispose();
    }

    private static void Cases(Action<bool, string> check, string root)
    {
        // ---- Pure comparators (steps 2c, 3 and 7) ----
        check(LocalOutputFile.CanonicalDirectory(@"\\?\C:\Out\Dir", @"c:\out\dir") == @"C:\Out\Dir",
            "LOCAL_OUTPUT_CANON_PIN_CASE: the held directory's final path pins the validated parent whatever case the user typed");
        check(LocalOutputFile.CanonicalDirectory(@"\\?\C:\Program Files\x", @"C:\PROGRA~1\x") is null,
            "LOCAL_OUTPUT_CANON_PIN_SHORT_NAME: a final path that differs from the validated parent beyond case is refused");
        check(LocalOutputFile.CanonicalDirectory(@"\\.\C:\a", @"C:\a") is null,
            "LOCAL_OUTPUT_CANON_PIN_PREFIX: only a long-path drive-letter final path can pin the parent");
        const string Volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}";
        check(!LocalOutputFile.IsFinalPathOf(Volume + @"\a\Out.json", Volume + @"\a", "out.json"),
            "LOCAL_OUTPUT_FINAL_LEAF_CASE: a published leaf that differs only in case never matches, so a tunnel-cache rename is a safe refusal");
        check(!LocalOutputFile.IsFinalPathOf(Volume + @"\A\out.json", Volume + @"\a", "out.json"),
            "LOCAL_OUTPUT_FINAL_DIRECTORY_CASE: a directory part that differs from the held spelling never matches");
        check(!LocalOutputFile.IsFinalPathOf(Volume + @"\moved\out.json", Volume + @"\a", "out.json"),
            "LOCAL_OUTPUT_FINAL_OTHER_DIRECTORY: the same leaf in another directory never matches");
        check(!LocalOutputFile.IsFinalPathOf(Volume + @"\a\b\out.json", Volume + @"\a", "out.json"),
            "LOCAL_OUTPUT_FINAL_DEEPER: a file one level below the held directory never matches");
        check(!LocalOutputFile.IsFinalPathOf(Volume + @"\a\out.json.tmp", Volume + @"\a", "out.json"),
            "LOCAL_OUTPUT_FINAL_LONGER_LEAF: a longer final name never matches a shorter leaf");
        check(LocalOutputFile.IsFinalPathOf(Volume + @"\out.json", Volume + @"\", "out.json") &&
            LocalOutputFile.IsFinalPathOf(Volume + @"\a\out.json", Volume + @"\a", "out.json"),
            "LOCAL_OUTPUT_FINAL_ROOT: an exact final path matches, directly under a volume root without a doubled separator too");

        // ---- The held directory (step 2) ----
        string plain = Case(root, "held-plain");
        string canonRoot = "";
        bool heldPlain = Safe("HELD_PLAIN", () =>
        {
            using (var held = LocalOutputFile.OpenHeldDirectory(plain, null, out string canon, out string volume))
                if (held.IsInvalid || !string.Equals(canon, plain, StringComparison.OrdinalIgnoreCase) ||
                    !volume.StartsWith(@"\\?\Volume{", StringComparison.Ordinal)) return false;
            using (var held = LocalOutputFile.OpenHeldDirectory(root, null, out canonRoot, out _))
            {
                Console.WriteLine("NOTE: LOCAL_OUTPUT_ROOT_SPELLING=" + (string.Equals(canonRoot, root, StringComparison.Ordinal) ? "exact" : "case-variant"));
                return !held.IsInvalid && string.Equals(canonRoot, root, StringComparison.OrdinalIgnoreCase);
            }
        });
        check(heldPlain, "LOCAL_OUTPUT_HELD_PLAIN: an owned plain directory opens as the held directory, proves local and pins its own path");

        string reparseCase = Case(root, "held-reparse"), reparseTarget = Sub(reparseCase, "target");
        File.WriteAllText(Path.Combine(reparseTarget, "keep.txt"), "kept", new UTF8Encoding(false));
        string junction = Path.Combine(reparseCase, "junction");
        string[] targetBefore = Listing(reparseTarget);
        bool heldReparse = false;
        HandoffClientChecks.CreateJunction(junction, reparseTarget);
        try { heldReparse = Refused("invalid_output_path", () => Hold(junction, null), out _); }
        finally { Directory.Delete(junction); }
        check(heldReparse && Safe("HELD_REPARSE", () => Listing(reparseTarget).SequenceEqual(targetBefore)),
            "LOCAL_OUTPUT_HELD_REPARSE: the held-directory open refuses an owned junction by its own handle");

        string fileCase = Case(root, "held-not-directory"), regular = Path.Combine(fileCase, "regular.txt");
        File.WriteAllText(regular, "owned regular file", new UTF8Encoding(false));
        check(Refused("invalid_output_path", () => Hold(regular, null), out _) &&
            Safe("HELD_NOT_DIRECTORY", () => File.ReadAllText(regular) == "owned regular file"),
            "LOCAL_OUTPUT_HELD_NOT_DIRECTORY: the held-directory open refuses an owned regular file");

        string ancestorCase = Case(root, "ancestor-reparse"), ancestorTarget = Sub(ancestorCase, "target");
        string ancestorSub = Sub(ancestorTarget, "sub"), ancestorJunction = Path.Combine(ancestorCase, "junction2");
        string[] subBefore = Listing(ancestorSub);
        bool heldAncestor = false, validateAncestor = false, saveAncestor = false;
        HandoffClientChecks.CreateJunction(ancestorJunction, ancestorTarget);
        try
        {
            string through = Path.Combine(ancestorJunction, "sub");
            heldAncestor = Refused("invalid_output_path", () => Hold(through, null), out _);
            validateAncestor = Refused("invalid_output_path", () => LocalOutputFile.Validate(Path.Combine(through, "out.json")), out _);
            saveAncestor = Refused("invalid_output_path", () => LocalOutputFile.Save(Path.Combine(through, "out.json"), "{}"u8.ToArray()), out _);
        }
        finally { Directory.Delete(ancestorJunction); }
        check(heldAncestor, "LOCAL_OUTPUT_HELD_ANCESTOR_REPARSE: a held directory reached through an ancestor junction fails the pin");
        check(validateAncestor && saveAncestor && Safe("ANCESTOR_REPARSE", () => Listing(ancestorSub).SequenceEqual(subBefore)),
            "LOCAL_OUTPUT_ANCESTOR_REPARSE: a junction above the parent is refused invalid_output_path by Validate and Save and its target is untouched");

        string localCase = Case(root, "local-proof");
        bool volumeObserved = false;
        bool localRefused = Refused("invalid_output_path", () => LocalOutputFile.Save(Path.Combine(localCase, "out.json"), "{}"u8.ToArray(),
            seams: new() { ParentVolumePath = _ => { volumeObserved = true; return null; } }), out _);
        check(localRefused && volumeObserved && Safe("LOCAL_PROOF_UNAVAILABLE", () => Listing(localCase).Length == 0),
            "LOCAL_OUTPUT_LOCAL_PROOF_UNAVAILABLE: a held directory with no volume GUID path is refused invalid_output_path and nothing is created");

        string ntfsCase = Case(root, "not-ntfs");
        string? observedFileSystem = null;
        bool ntfsRefused = Refused("invalid_output_path", () => LocalOutputFile.Save(Path.Combine(ntfsCase, "out.json"), "{}"u8.ToArray(),
            seams: new() { FileSystemName = observed => { observedFileSystem = observed; return "FAT32"; } }), out _);
        Console.WriteLine("NOTE: LOCAL_OUTPUT_FILE_SYSTEM=" + (observedFileSystem ?? "<null>"));
        check(ntfsRefused && observedFileSystem == "NTFS" && Safe("NOT_NTFS", () => Listing(ntfsCase).Length == 0),
            "LOCAL_OUTPUT_NOT_NTFS: a held directory whose file system is not NTFS is refused invalid_output_path and nothing is created");

        // 2e is reachable only if the file system reports a final path containing a reparse point; the seam reports the
        // typed junction path in place of the target's, and the real canonical walk must then refuse the static junction.
        string chainCase = Case(root, "canon-chain"), chainTarget = Sub(chainCase, "target");
        _ = Sub(chainTarget, "sub");
        string chainLink = Path.Combine(chainCase, "link"), chainTyped = Path.Combine(chainLink, "sub");
        string? chainObserved = null;
        bool chainRefused = false;
        HandoffClientChecks.CreateJunction(chainLink, chainTarget);
        try
        {
            chainRefused = Refused("invalid_output_path", () => Hold(chainTyped, new()
            {
                ParentFinalPath = observed => { chainObserved = observed; return @"\\?\" + chainTyped; },
            }), out _);
        }
        finally { Directory.Delete(chainLink); }
        check(chainRefused && chainObserved is not null &&
            chainObserved.EndsWith(@"\target\sub", StringComparison.OrdinalIgnoreCase),
            "LOCAL_OUTPUT_CANON_CHAIN_PLAIN: the canonical-chain re-walk refuses a reparse component above the held directory");

        // ---- Publication ----
        string publishCase = Case(root, "publish"), neighbour = Path.Combine(publishCase, "neighbour.txt");
        File.WriteAllText(neighbour, "sentinel neighbour", new UTF8Encoding(false));
        byte[] payload = [.. Encoding.UTF8.GetBytes("mixed text 雪🙂\r\nC:\\literal\\file\n"),
            .. Enumerable.Range(0, 70000).Select(index => (byte)(index * 7 + 3))];
        string publishFull = Path.Combine(publishCase, "out.json");
        string[] publishBefore = Listing(publishCase);
        var published = Attempt(() => LocalOutputFile.Save(publishFull, payload), "PUBLISH");
        check(Safe("PUBLISH", () => published is not null && published.Path == publishFull &&
                published.Sha256 == Convert.ToHexString(SHA256.HashData(payload)) && published.Bytes == payload.LongLength &&
                File.ReadAllBytes(publishFull).AsSpan().SequenceEqual(payload) &&
                Listing(publishCase).SequenceEqual(publishBefore.Append("out.json").Order(StringComparer.Ordinal)) &&
                File.ReadAllText(neighbour) == "sentinel neighbour" && ReopensExclusively(publishFull)),
            "LOCAL_OUTPUT_PUBLISH: exact bytes, retention and listing after a handle-owned publication; the file reopens exclusively");

        string boundCase = Case(root, "publish-bound"), boundFull = Path.Combine(boundCase, "bound.json");
        const string BoundTemp = ".agentbroker-output-bound.tmp";
        string? boundCreated = null, boundPublished = null;
        var bound = Attempt(() => LocalOutputFile.Save(boundFull, "bound payload"u8.ToArray(), seams: new()
        {
            TempLeaf = () => BoundTemp,
            TempFinalPath = observed => boundCreated = observed,
            PublishedFinalPath = observed => boundPublished = observed,
        }), "PUBLISH_BOUND");
        check(Safe("PUBLISH_BOUND", () =>
        {
            using var held = LocalOutputFile.OpenHeldDirectory(boundCase, null, out _, out string volume);
            Console.WriteLine("NOTE: LOCAL_OUTPUT_PUBLISHED_FINAL=" + (boundPublished ?? "<null>"));
            return bound is not null && volume.StartsWith(@"\\?\Volume{", StringComparison.Ordinal) &&
                boundCreated == LocalOutputFile.Join(volume, BoundTemp) && boundPublished == LocalOutputFile.Join(volume, "bound.json") &&
                File.ReadAllText(boundFull) == "bound payload" && Listing(boundCase).SequenceEqual(["bound.json"]);
        }), "LOCAL_OUTPUT_PUBLISH_BOUND: the temporary and the published file are proven at the held directory's volume GUID path plus their exact leaves");

        string deleteCase = Case(root, "held-denies-delete"), deleteFull = Path.Combine(deleteCase, "out.json");
        int during = -1, after = -1;
        var deleteSaved = Attempt(() => LocalOutputFile.Save(deleteFull, "{}"u8.ToArray(), () => during = ProbeDelete(deleteCase)), "HELD_DENIES_DELETE");
        after = ProbeDelete(deleteCase);
        Console.WriteLine($"NOTE: LOCAL_OUTPUT_DELETE_PROBE during={during} after={after}");
        check(deleteSaved is not null && during == 32 && after == 0 && File.Exists(deleteFull),
            "LOCAL_OUTPUT_HELD_DENIES_DELETE: while publishing, a delete-access open of the parent is a sharing violation; after return it succeeds");

        string unicodeCase = Case(root, "unicode"), unicodeParent = Sub(unicodeCase, "雪 dir 🙂");
        string unicodeLeaf = new string('l', 100) + "雪🙂.json", unicodeFull = Path.Combine(unicodeParent, unicodeLeaf);
        byte[] unicodeBytes = Encoding.UTF8.GetBytes("unicode payload 雪🙂");
        var unicode = Attempt(() => LocalOutputFile.Save(unicodeFull, unicodeBytes), "PUBLISH_UNICODE");
        check(Safe("PUBLISH_UNICODE", () => unicode is not null && Listing(unicodeParent) is [var only] &&
                string.Equals(only, unicodeLeaf, StringComparison.Ordinal) && File.ReadAllBytes(unicodeFull).AsSpan().SequenceEqual(unicodeBytes)),
            "LOCAL_OUTPUT_PUBLISH_UNICODE: a non-ASCII parent and a long leaf with a surrogate pair publish under the exact name");

        string longParent = Case(root, "long-path");
        while (Path.Combine(longParent, "long.json").Length <= 300) longParent = Sub(longParent, "segment-0123456789abcdefghij");
        string longFull = Path.Combine(longParent, "long.json");
        object? longPaths = null;
        try { longPaths = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", null); }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        Console.WriteLine("NOTE: LongPathsEnabled=" + (longPaths?.ToString() ?? "<absent>") + " length=" + longFull.Length);
        var longSaved = Attempt(() => LocalOutputFile.Save(longFull, "long path payload"u8.ToArray()), "PUBLISH_LONG_PATH");
        check(Safe("PUBLISH_LONG_PATH", () => longSaved is not null && longFull.Length > 300 && File.ReadAllText(longFull) == "long path payload"),
            "LOCAL_OUTPUT_PUBLISH_LONG_PATH: a destination longer than MAX_PATH publishes");

        // ---- Case (step 2f). canonRoot is the file system's own spelling of the scratch root (from HELD_PLAIN). ----
        string variantDirectory = Sub(canonRoot, "variant-dir");
        string variantTyped = Path.Combine(canonRoot, "VARIANT-DIR", "variant.json");
        var variant = Attempt(() => LocalOutputFile.Save(variantTyped, "variant payload"u8.ToArray()), "CASE_VARIANT_PUBLISH");
        check(Safe("CASE_VARIANT_PUBLISH", () => variant is not null && variant.Path == variantTyped &&
                Listing(variantDirectory).SequenceEqual(["variant.json"]) &&
                File.ReadAllText(Path.Combine(variantDirectory, "variant.json")) == "variant payload"),
            "LOCAL_OUTPUT_CASE_VARIANT_PUBLISH: a parent typed in another case over an all-insensitive canonical chain publishes");

        string sensitive = Sub(canonRoot, "case-sensitive-dir");
        bool fixture = SetCaseSensitive(sensitive, true);
        if (!fixture) Console.WriteLine("CASE_SENSITIVE_FIXTURE_UNAVAILABLE");
        string sensitiveVariant = Path.Combine(canonRoot, "CASE-SENSITIVE-DIR", "refused.json");
        bool sensitiveRefused = fixture &&
            Refused("invalid_output_path", () => LocalOutputFile.Save(sensitiveVariant, "refused payload"u8.ToArray()), out _) &&
            Safe("CASE_SENSITIVE_REFUSED", () => Listing(sensitive).Length == 0);
        check(sensitiveRefused,
            "LOCAL_OUTPUT_CASE_SENSITIVE_REFUSED: a case-sensitive directory reached through a case-variant typed path is refused invalid_output_path and nothing is created");
        string sensitiveExact = Path.Combine(sensitive, "exact.json");
        var exact = fixture ? Attempt(() => LocalOutputFile.Save(sensitiveExact, "exact payload"u8.ToArray()), "CASE_SENSITIVE_EXACT") : null;
        check(fixture && Safe("CASE_SENSITIVE_EXACT", () => exact is not null && exact.Path == sensitiveExact &&
                Listing(sensitive).SequenceEqual(["exact.json"]) && File.ReadAllText(sensitiveExact) == "exact payload"),
            "LOCAL_OUTPUT_CASE_SENSITIVE_EXACT: the same case-sensitive directory typed exactly publishes");

        // 2f covers every canonical component, not only the held one. Here the flagged component is an ANCESTOR of an
        // insensitive held directory that is typed in another case below it. A directory made inside a case-sensitive one
        // inherits the flag, so mid's is cleared while mid is still empty; leaf-dir is then made under an insensitive mid.
        string flaggedAncestor = Sub(canonRoot, "cs-anc");
        bool ancestorFixture = SetCaseSensitive(flaggedAncestor, true);
        string ancestorMid = Sub(flaggedAncestor, "mid");
        bool? midInherited = ReadCaseSensitive(ancestorMid);
        Console.WriteLine("NOTE: LOCAL_OUTPUT_CASE_FLAG_INHERITED=" + (midInherited?.ToString() ?? "<unreadable>"));
        ancestorFixture = ancestorFixture && midInherited is { } inherited && (!inherited || SetCaseSensitive(ancestorMid, false));
        string ancestorHeld = Sub(ancestorMid, "leaf-dir");
        ancestorFixture = ancestorFixture && ReadCaseSensitive(ancestorHeld) == false && ReadCaseSensitive(flaggedAncestor) == true;
        if (!ancestorFixture) Console.WriteLine("CASE_SENSITIVE_FIXTURE_UNAVAILABLE (flagged ancestor)");
        check(ancestorFixture &&
            Refused("invalid_output_path", () => LocalOutputFile.Save(Path.Combine(ancestorMid, "LEAF-DIR", "x.json"), "ancestor payload"u8.ToArray()), out _) &&
            Safe("CASE_SENSITIVE_ANCESTOR", () => Listing(ancestorHeld).Length == 0),
            "LOCAL_OUTPUT_CASE_SENSITIVE_ANCESTOR: a case-sensitive ancestor above an insensitive held directory typed in another case is refused invalid_output_path and nothing is created");

        // ---- Refusals and handle-owned cleanup ----
        string existingCase = Case(root, "existing"), existing = Path.Combine(existingCase, "exists.json");
        File.WriteAllText(existing, "existing sentinel", new UTF8Encoding(false));
        string[] existingBefore = Listing(existingCase);
        bool reached = false;
        bool existingRefused = Refused("output_exists", () => LocalOutputFile.Save(existing, "replacement"u8.ToArray(),
            seams: new() { TempLeaf = () => { reached = true; return ".agentbroker-output-" + Guid.NewGuid().ToString("N") + ".tmp"; } }), out _);
        check(existingRefused && !reached && Safe("EXISTING_REFUSED", () => File.ReadAllText(existing) == "existing sentinel" &&
                Listing(existingCase).SequenceEqual(existingBefore)),
            "LOCAL_OUTPUT_EXISTING_REFUSED: an existing destination is refused output_exists before any temporary is chosen");

        string takenCase = Case(root, "temp-taken");
        const string TakenLeaf = ".agentbroker-output-fixed21.tmp";
        string taken = Path.Combine(takenCase, TakenLeaf);
        File.WriteAllText(taken, "planted temporary sentinel", new UTF8Encoding(false));
        bool takenRefused = Refused("output_unavailable", () => LocalOutputFile.Save(Path.Combine(takenCase, "out.json"), "payload"u8.ToArray(),
            seams: new() { TempLeaf = () => TakenLeaf }), out var takenError);
        check(takenRefused && takenError?.InnerException is Win32Exception { NativeErrorCode: 80 } &&
            Safe("TEMP_NAME_TAKEN", () => File.ReadAllText(taken) == "planted temporary sentinel" && Listing(takenCase).SequenceEqual([TakenLeaf])),
            "LOCAL_OUTPUT_TEMP_NAME_TAKEN: a file already at the temporary name is refused and left byte-identical");

        string failureCase = Case(root, "before-publish-failure");
        string[] failureBefore = Listing(failureCase);
        var failure = new IOException("test-held before-publish failure");
        LocalOutputFile.DiscardOutcome? discarded = null;
        const string FailureLeaf = ".agentbroker-output-fixed28.tmp";
        // An attribute-only handle of the test's own (what an indexer holds, residual R4) stays open across the discard. With
        // POSIX semantics the name leaves the namespace when Save closes its handle; a classic delete would leave it visible
        // until this observer closes too. The observer never sets a disposition and never renames.
        SafeFileHandle? observer = null;
        string[]? listedWithObserver = null;
        Exception? caught;
        try
        {
            caught = Caught(() => LocalOutputFile.Save(Path.Combine(failureCase, "out.json"), "payload"u8.ToArray(), () =>
            {
                observer = Native.CreateFileW(@"\\?\" + Path.Combine(failureCase, FailureLeaf), Native.ReadAttributes,
                    Native.ShareRead | Native.ShareWrite | Native.ShareDelete, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
                throw failure;
            }, new() { TempLeaf = () => FailureLeaf, Discarded = outcome => discarded = outcome }));
            listedWithObserver = observer is { IsInvalid: false } ? Listing(failureCase) : null;
        }
        finally { observer?.Dispose(); }
        Console.WriteLine("NOTE: LOCAL_OUTPUT_DISCARD=" + (discarded?.ToString() ?? "<not observed>") +
            " listedWithObserver=" + (listedWithObserver is null ? "<none>" : string.Join(",", listedWithObserver)));
        check(ReferenceEquals(caught, failure) && discarded == LocalOutputFile.DiscardOutcome.PosixDeleted &&
            listedWithObserver is not null && listedWithObserver.SequenceEqual(failureBefore) &&
            LocalOutputFile.CleanupReport(failure) is null && Safe("BEFORE_PUBLISH_FAILURE", () => Listing(failureCase).SequenceEqual(failureBefore)),
            "LOCAL_OUTPUT_BEFORE_PUBLISH_FAILURE: a failure before publication surfaces unchanged and the temporary is removed by POSIX delete through its handle");

        string reportCase = Case(root, "cleanup-reported");
        var reportFailure = new IOException("test-held failure with a refused discard");
        LocalOutputFile.DiscardOutcome? reportOutcome = null;
        Exception? reportCaught = Caught(() => LocalOutputFile.Save(Path.Combine(reportCase, "out.json"), "payload"u8.ToArray(), () => throw reportFailure,
            new() { SkipDisposition = true, Discarded = outcome => reportOutcome = outcome }));
        using var dispatched = JsonDocument.Parse("{\"status\":\"ok\",\"dispatchState\":\"confirmed\"}");
        check(ReferenceEquals(reportCaught, reportFailure) && reportFailure.Message == "test-held failure with a refused discard" &&
            reportFailure.Data[LocalOutputFile.CleanupDataKey] is "temporary_not_removed" &&
            reportOutcome == LocalOutputFile.DiscardOutcome.NotDeleted &&
            Safe("CLEANUP_REPORTED", () => Listing(reportCase) is [var remaining] &&
                remaining.StartsWith(".agentbroker-output-", StringComparison.Ordinal) && remaining.EndsWith(".tmp", StringComparison.Ordinal) &&
                JsonSerializer.Serialize(HandoffPrepareClient.OutputError(reportFailure), Web) ==
                    "{\"status\":\"output_error\",\"dispatchState\":\"not_dispatched\",\"retained\":false,\"code\":\"output_unavailable\"," +
                    "\"cleanup\":\"temporary_not_removed\",\"nextAction\":\"Choose a new local file in an existing plain directory; no broker was contacted.\"}" &&
                JsonSerializer.Serialize(McpCallClient.OutputError(reportFailure, dispatched.RootElement), Web) ==
                    "{\"status\":\"output_error\",\"dispatchState\":\"confirmed\",\"retained\":false,\"code\":\"output_unavailable\"," +
                    "\"cleanup\":\"temporary_not_removed\",\"response\":{\"status\":\"ok\",\"dispatchState\":\"confirmed\"}," +
                    "\"nextAction\":\"Retain this response using a new local file. Do not repeat a confirmed operation because retention failed.\"}"),
            "LOCAL_OUTPUT_CLEANUP_REPORTED: a refused discard leaves the primary exception unchanged, reports temporary_not_removed and both exit-74 bodies carry it");
        var plainFailure = new IOException("output_unavailable");
        check(Safe("ERROR_BODY_UNCHANGED", () =>
                JsonSerializer.Serialize(HandoffPrepareClient.OutputError(plainFailure), Web) ==
                    "{\"status\":\"output_error\",\"dispatchState\":\"not_dispatched\",\"retained\":false,\"code\":\"output_unavailable\"," +
                    "\"nextAction\":\"Choose a new local file in an existing plain directory; no broker was contacted.\"}" &&
                JsonSerializer.Serialize(McpCallClient.OutputError(plainFailure, dispatched.RootElement), Web) ==
                    "{\"status\":\"output_error\",\"dispatchState\":\"confirmed\",\"retained\":false,\"code\":\"output_unavailable\"," +
                    "\"response\":{\"status\":\"ok\",\"dispatchState\":\"confirmed\"}," +
                    "\"nextAction\":\"Retain this response using a new local file. Do not repeat a confirmed operation because retention failed.\"}"),
            "LOCAL_OUTPUT_ERROR_BODY_UNCHANGED: without a cleanup report both exit-74 bodies are byte-identical to 1.10.0, with no null member");

        // The same report through each CLI's own catch site, in process with per-call seams: the step-3 proof is refused (the
        // observed temporary final path is withheld) and the disposition call is not made, so a real temporary remains
        // under its own name. Neither run contacts a broker: the MCP request is malformed, so it is answered before any
        // connection and that known response is then retained.
        string cliCase = Case(root, "cli-cleanup-reported");
        var cliSeams = new LocalOutputFile.Seams { TempFinalPath = _ => null, SkipDisposition = true };
        string handoffRequest = Path.Combine(cliCase, "handoff-request.json"), handoffOut = Sub(cliCase, "handoff-out");
        File.WriteAllText(handoffRequest, new HandoffPacket(HandoffPacket.Version, "cli-cleanup", "request", "ordinary owned fixture", []).ToText(),
            new UTF8Encoding(false));
        var handoffRun = RunCli(output => HandoffPrepareClient.RunCommandAsync(
            ["--handoff-prepare", "--request-file", handoffRequest, "--output-file", Path.Combine(handoffOut, "packet.txt")], output, cliSeams));
        check(handoffRun.Code == 74 && handoffRun.Text ==
                "{\"status\":\"output_error\",\"dispatchState\":\"not_dispatched\",\"retained\":false,\"code\":\"output_unavailable\"," +
                "\"cleanup\":\"temporary_not_removed\",\"nextAction\":\"Choose a new local file in an existing plain directory; no broker was contacted.\"}\n" &&
            Safe("HANDOFF_CLI_CLEANUP", () => Listing(handoffOut) is [var left] &&
                left.StartsWith(".agentbroker-output-", StringComparison.Ordinal) && left.EndsWith(".tmp", StringComparison.Ordinal)),
            "LOCAL_OUTPUT_HANDOFF_CLI_CLEANUP: --handoff-prepare reports a refused discard as cleanup temporary_not_removed in its exit-74 JSON");
        string mcpRequest = Path.Combine(cliCase, "malformed-request.json"), mcpOut = Sub(cliCase, "mcp-out");
        File.WriteAllText(mcpRequest, "{", new UTF8Encoding(false));
        var mcpRun = RunCli(output => McpCallClient.RunAsync(new(cliCase, mcpRequest, false, Path.Combine(mcpOut, "result.json")), output, cliSeams));
        check(mcpRun.Code == 74 && mcpRun.Text ==
                "{\"status\":\"output_error\",\"dispatchState\":\"not_dispatched\",\"retained\":false,\"code\":\"output_unavailable\"," +
                "\"cleanup\":\"temporary_not_removed\",\"response\":{\"status\":\"invalid_request\",\"dispatchState\":\"not_dispatched\"," +
                "\"code\":\"invalid_request\",\"httpStatus\":null,\"nextAction\":\"Check the request and broker connection; the tool was not dispatched.\"}," +
                "\"nextAction\":\"Retain this response using a new local file. Do not repeat a confirmed operation because retention failed.\"}\n" &&
            Safe("MCP_CLI_CLEANUP", () => Listing(mcpOut) is [var left] &&
                left.StartsWith(".agentbroker-output-", StringComparison.Ordinal) && left.EndsWith(".tmp", StringComparison.Ordinal)),
            "LOCAL_OUTPUT_MCP_CLI_CLEANUP: --mcp-call reports a refused discard as cleanup temporary_not_removed in its exit-74 JSON with the known response");

        // Post-push audit of 7ad9d901 (C2): a destination that step 2 refuses is refused by the preflight, before any tool is
        // dispatched, not only by Save afterwards. The reachable case is a SUBST drive, whose DOS final path names the backing
        // volume, so the pin fails. The ParentFinalPath seam reports exactly such a path; no drive letter is mapped. The MCP
        // request is malformed, so no broker is contacted either way: a refusal left to Save would show as the post-dispatch
        // body that embeds that response, not as the preflight body below.
        string preflightCase = Case(root, "preflight-pin"), preflightOut = Sub(preflightCase, "out");
        var substSeams = new LocalOutputFile.Seams { ParentFinalPath = _ => @"\\?\Z:\subst-backing\preflight-pin\out" };
        bool validateRefused = Refused("invalid_output_path",
            () => LocalOutputFile.Validate(Path.Combine(preflightOut, "result.json"), substSeams), out _);
        check(validateRefused && Safe("PREFLIGHT_PIN", () => Listing(preflightOut).Length == 0),
            "LOCAL_OUTPUT_PREFLIGHT_PIN: Validate makes the held-directory proofs, so a parent whose final path names another volume is refused before any output exists");
        string preflightRequest = Path.Combine(preflightCase, "malformed-request.json");
        File.WriteAllText(preflightRequest, "{", new UTF8Encoding(false));
        var preflightRun = RunCli(output => McpCallClient.RunAsync(new(preflightCase, preflightRequest, false,
            Path.Combine(preflightOut, "result.json")), output, substSeams));
        check(preflightRun.Code == 74 && preflightRun.Text ==
                "{\"status\":\"output_error\",\"dispatchState\":\"not_dispatched\",\"retained\":false,\"code\":\"output_unavailable\"," +
                "\"nextAction\":\"Choose a new local file in an existing plain directory; no tool was dispatched.\"}\n" &&
            Safe("MCP_PREFLIGHT_PIN", () => Listing(preflightOut).Length == 0),
            "LOCAL_OUTPUT_MCP_PREFLIGHT_PIN: --mcp-call refuses a parent that fails the pin in its preflight, before dispatch, not after it at Save");

        string racedCase = Case(root, "atomic-no-replace"), raced = Path.Combine(racedCase, "raced.json");
        string[] racedBefore = Listing(racedCase);
        bool racedRefused = Refused("output_exists", () => LocalOutputFile.Save(raced, "payload"u8.ToArray(),
            () => File.WriteAllText(raced, "raced", new UTF8Encoding(false))), out var racedError);
        Console.WriteLine("NOTE: LOCAL_OUTPUT_COLLISION_NTSTATUS=" + (racedError?.InnerException?.Data[LocalOutputFile.NtStatusDataKey] ?? "<none>"));
        check(racedRefused && Safe("ATOMIC_NO_REPLACE", () => File.ReadAllText(raced) == "raced" &&
                Listing(racedCase).SequenceEqual(racedBefore.Append("raced.json").Order(StringComparer.Ordinal))),
            "LOCAL_OUTPUT_ATOMIC_NO_REPLACE: a destination created after flush wins, the refusal is output_exists and no temporary remains");

        string renameCase = Case(root, "rename-failure");
        string[] renameBefore = Listing(renameCase);
        bool renameObserved = false;
        LocalOutputFile.DiscardOutcome? renameDiscard = null;
        Exception? renameCaught = Caught(() => LocalOutputFile.Save(Path.Combine(renameCase, "out.json"), "payload"u8.ToArray(), seams: new()
        {
            RenameStatus = _ => { renameObserved = true; return unchecked((int)0xC0000022); },
            Discarded = outcome => renameDiscard = outcome,
        }));
        check(renameCaught is IOException { Message: "output_unavailable" } && renameCaught.GetType() == typeof(IOException) &&
            renameCaught.InnerException?.Data[LocalOutputFile.NtStatusDataKey] is "0xC0000022" && renameObserved &&
            renameDiscard == LocalOutputFile.DiscardOutcome.PosixDeleted &&
            Safe("RENAME_FAILURE_TYPED", () => Listing(renameCase).SequenceEqual(renameBefore)),
            "LOCAL_OUTPUT_RENAME_FAILURE_TYPED: a non-collision rename status is IOException output_unavailable carrying the status and leaves nothing");

        // STATUS_PENDING is a success-class NTSTATUS; on a synchronous handle it must never be read as a completed rename.
        string pendingCase = Case(root, "rename-pending");
        string[] pendingBefore = Listing(pendingCase);
        bool pendingObserved = false;
        Exception? pendingCaught = Caught(() => LocalOutputFile.Save(Path.Combine(pendingCase, "out.json"), "payload"u8.ToArray(), seams: new()
        {
            RenameStatus = _ => { pendingObserved = true; return 0x103; },
        }));
        check(pendingCaught is IOException { Message: "output_unavailable" } &&
            pendingCaught.InnerException?.Data[LocalOutputFile.NtStatusDataKey] is "0x00000103" && pendingObserved &&
            Safe("RENAME_PENDING_REFUSED", () => Listing(pendingCase).SequenceEqual(pendingBefore)),
            "LOCAL_OUTPUT_RENAME_PENDING_REFUSED: a STATUS_PENDING rename result is output_unavailable, never success, and leaves nothing");

        string mismatchCase = Case(root, "final-path-mismatch"), decoy = Path.Combine(mismatchCase, "decoy.json");
        File.WriteAllText(decoy, "decoy sentinel", new UTF8Encoding(false));
        string[] mismatchBefore = Listing(mismatchCase);
        bool mismatchObserved = false;
        bool mismatchRefused = Refused("output_unavailable", () => LocalOutputFile.Save(Path.Combine(mismatchCase, "out.json"), "payload"u8.ToArray(),
            seams: new()
            {
                PublishedFinalPath = observed =>
                {
                    mismatchObserved = true;
                    return observed is not null && observed.EndsWith(@"\out.json", StringComparison.Ordinal) ? observed[..^"out.json".Length] + "decoy.json" : null;
                },
            }), out _);
        check(mismatchRefused && mismatchObserved && Safe("FINAL_PATH_MISMATCH", () => !File.Exists(Path.Combine(mismatchCase, "out.json")) &&
                File.ReadAllText(decoy) == "decoy sentinel" && Listing(mismatchCase).SequenceEqual(mismatchBefore)),
            "LOCAL_OUTPUT_FINAL_PATH_MISMATCH: a published file whose final path disagrees is removed by its own handle and a planted decoy is untouched");

        string outsideCase = Case(root, "temp-outside-held"), outsideHeld = Sub(outsideCase, "held"), outsideDecoyDirectory = Sub(outsideCase, "decoy-dir");
        const string OutsideLeaf = ".agentbroker-output-fixed26.tmp";
        string outsideDecoy = Path.Combine(outsideDecoyDirectory, OutsideLeaf);
        File.WriteAllText(outsideDecoy, "decoy temporary sentinel", new UTF8Encoding(false));
        bool outsideObserved = false;
        bool outsideRefused = Refused("output_unavailable", () => LocalOutputFile.Save(Path.Combine(outsideHeld, "out.json"), "payload"u8.ToArray(),
            seams: new()
            {
                TempLeaf = () => OutsideLeaf,
                TempFinalPath = observed =>
                {
                    outsideObserved = true;
                    return observed?.Replace(@"\held\", @"\decoy-dir\", StringComparison.Ordinal);
                },
            }), out _);
        check(outsideRefused && outsideObserved && Safe("TEMP_OUTSIDE_HELD", () => Listing(outsideHeld).Length == 0 &&
                File.ReadAllText(outsideDecoy) == "decoy temporary sentinel" && Listing(outsideDecoyDirectory).SequenceEqual([OutsideLeaf])),
            "LOCAL_OUTPUT_TEMP_OUTSIDE_HELD: a temporary not proven at the held directory is removed by its own handle and a planted decoy is untouched");

        // ---- Step 3b (design v2.1 D5): the definitive chain proofs run once the owned temporary exists, before any payload
        // byte. The seams report a flagged held directory only when the probe sees our temporary in it, so the preflight
        // passes and only the post-create position can refuse. No directory changes and nothing races. ----
        byte[] postPayload = "post-create payload"u8.ToArray();
        string postChain = Case(root, "post-create-chain");
        var chainProbe = new OwnedEntryProbe(postChain);
        bool postChainRefused = Refused("invalid_output_path", () => LocalOutputFile.Save(Path.Combine(postChain, "out.json"), postPayload, seams: new()
        {
            ChainAttributes = (component, attributes) => chainProbe.Observe(component) ? attributes | FileAttributes.ReparsePoint : attributes,
        }), out _);
        Console.WriteLine("NOTE: LOCAL_OUTPUT_POST_CREATE_CHAIN_TEMP_BYTES=" + (chainProbe.TemporaryBytes?.ToString() ?? "<not observed>"));
        check(postChainRefused && chainProbe.TemporaryBytes == 0 && Safe("POST_CREATE_CHAIN", () => Listing(postChain).Length == 0),
            "LOCAL_OUTPUT_POST_CREATE_CHAIN: the definitive canonical-chain check runs once the owned temporary exists and before any payload byte, and its refusal leaves nothing");
        string postCase = Sub(canonRoot, "post-create-case");
        var caseProbe = new OwnedEntryProbe(postCase);
        bool postCaseRefused = Refused("invalid_output_path", () => LocalOutputFile.Save(Path.Combine(canonRoot, "POST-CREATE-CASE", "out.json"), postPayload,
            seams: new() { CaseSensitiveFlags = (component, flags) => caseProbe.Observe(component) ? flags | 1u : flags }), out _);
        Console.WriteLine("NOTE: LOCAL_OUTPUT_POST_CREATE_CASE_TEMP_BYTES=" + (caseProbe.TemporaryBytes?.ToString() ?? "<not observed>"));
        check(postCaseRefused && caseProbe.TemporaryBytes == 0 && Safe("POST_CREATE_CASE", () => Listing(postCase).Length == 0),
            "LOCAL_OUTPUT_POST_CREATE_CASE: the definitive case-sensitivity check runs once the owned temporary exists and before any payload byte, and its refusal leaves nothing");

        // Static helpers, Win32 exports, and (post-push audit of 7ad9d901) the instance spellings: any .Delete( or .MoveTo(
        // call and any FileInfo/DirectoryInfo/FileSystemInfo use. Span.CopyTo stays legal, so CopyTo is not listed.
        check(Safe("SOURCE_NO_PATH_DESTRUCTION", () => SourceText() is { } source && !Regex.IsMatch(source,
                @"\b(File\.(Delete|Move|Replace|Copy)|Directory\.(Delete|Move)|(DeleteFile|MoveFile|MoveFileEx|RemoveDirectory|ReplaceFile)W?)\s*\(" +
                @"|\.(Delete|MoveTo)\s*\(|\b(FileInfo|DirectoryInfo|FileSystemInfo)\b")),
            "LOCAL_OUTPUT_SOURCE_NO_PATH_DESTRUCTION: LocalOutputFile.cs contains no path-based delete, move or replace call");
    }

    private static string Case(string root, string name) => Sub(root, name);

    private static string Sub(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string[] Listing(string directory) =>
        Directory.GetFileSystemEntries(directory).Select(entry => Path.GetFileName(entry)!).Order(StringComparer.Ordinal).ToArray();

    private static void Hold(string directory, LocalOutputFile.Seams? seams)
    {
        using var held = LocalOutputFile.OpenHeldDirectory(directory, seams, out _, out _);
    }

    // True only for the coded refusal: an IOException or InvalidDataException whose message is the code.
    private static bool Refused(string code, Action action, out Exception? error)
    {
        error = Caught(action);
        if (error is not null && !(error is IOException or InvalidDataException && error.Message == code))
            Console.WriteLine("NOTE: expected " + code + ", caught " + error.GetType().Name + ": " + error.Message);
        return error is IOException or InvalidDataException && error.Message == code;
    }

    private static Exception? Caught(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static T? Attempt<T>(Func<T> action, string label) where T : class
    {
        try { return action(); }
        catch (Exception error)
        {
            Console.WriteLine("NOTE: " + label + " threw " + error.GetType().Name + ": " + error.Message +
                (error.InnerException is { } inner ? " inner " + inner.GetType().Name + ": " + inner.Message : ""));
            return null;
        }
    }

    // Every read inside a check is wrapped, so an unexpected exception makes the check false instead of escaping unnamed.
    private static bool Safe(string label, Func<bool> body)
    {
        try { return body(); }
        catch (Exception error) { Console.WriteLine("NOTE: " + label + " read threw " + error.GetType().Name + ": " + error.Message); return false; }
    }

    private static bool ReopensExclusively(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return stream.Length >= 0;
    }

    // A delete-ACCESS probe on an owned directory: it never sets a disposition and never renames; a handle is closed at once.
    private static int ProbeDelete(string directory)
    {
        using SafeFileHandle probe = Native.CreateFileW(@"\\?\" + directory, Native.DeleteAccess,
            Native.ShareRead | Native.ShareWrite | Native.ShareDelete, IntPtr.Zero, Native.OpenExisting, Native.BackupSemantics, IntPtr.Zero);
        return probe.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    // fsutil sets or clears an owned, empty directory's case-sensitivity flag (no administrator rights needed); the flag is
    // then read back, and only the requested value counts as success.
    private static bool SetCaseSensitive(string directory, bool enable)
    {
        try
        {
            var start = new ProcessStartInfo("fsutil.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (string argument in new[] { "file", "setCaseSensitiveInfo", directory, enable ? "enable" : "disable" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return false;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); return false; }
            stdout.GetAwaiter().GetResult(); stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) { Console.WriteLine("NOTE: fsutil exit " + process.ExitCode); return false; }
            return ReadCaseSensitive(directory) == enable;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            Console.WriteLine("NOTE: case-sensitive fixture threw " + error.GetType().Name + ": " + error.Message);
            return false;
        }
    }

    // FILE_CS_FLAG_CASE_SENSITIVE_DIR through an attribute-only handle; null when the open or the query fails.
    private static bool? ReadCaseSensitive(string directory)
    {
        using SafeFileHandle handle = Native.CreateFileW(@"\\?\" + directory, Native.ReadAttributes,
            Native.ShareRead | Native.ShareWrite | Native.ShareDelete, IntPtr.Zero, Native.OpenExisting, Native.BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        byte[] flags = new byte[4];
        return Native.GetFileInformationByHandleEx(handle, Native.FileCaseSensitiveInfo, flags, (uint)flags.Length)
            ? (BinaryPrimitives.ReadUInt32LittleEndian(flags) & 1) != 0 : null;
    }

    // Step 3b (design v2.1 D5) observer for the chain seams. It answers true, so the seam reports a flagged component, only
    // for the held directory and only once that directory holds exactly one owned temporary; it records that temporary's
    // end-of-file through an attribute-only handle (which never conflicts with Save's share mode 0, never sets a
    // disposition and never renames). The preflight sees an empty directory and is answered unchanged.
    private sealed class OwnedEntryProbe(string held)
    {
        internal long? TemporaryBytes { get; private set; }

        internal bool Observe(string component)
        {
            if (!string.Equals(component, held, StringComparison.OrdinalIgnoreCase)) return false;
            if (Listing(held) is not [var only] || !only.StartsWith(".agentbroker-output-", StringComparison.Ordinal)) return false;
            TemporaryBytes = EndOfFile(Path.Combine(held, only));
            return true;
        }

        private static long EndOfFile(string path)
        {
            using SafeFileHandle handle = Native.CreateFileW(@"\\?\" + path, Native.ReadAttributes,
                Native.ShareRead | Native.ShareWrite | Native.ShareDelete, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
            byte[] standard = new byte[24]; // FILE_STANDARD_INFO: AllocationSize, EndOfFile, NumberOfLinks, DeletePending, Directory
            return !handle.IsInvalid && Native.GetFileInformationByHandleEx(handle, Native.FileStandardInfo, standard, (uint)standard.Length)
                ? BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(8)) : -1;
        }
    }

    // One in-process CLI run; an escaped exception becomes code -1 with its type in the text, so the check fails by name.
    private static (int Code, string Text) RunCli(Func<Stream, Task<int>> run)
    {
        try
        {
            using var output = new MemoryStream();
            int code = run(output).GetAwaiter().GetResult();
            return (code, Encoding.UTF8.GetString(output.ToArray()));
        }
        catch (Exception error) { Console.WriteLine("NOTE: CLI run threw " + error.GetType().Name + ": " + error.Message); return (-1, ""); }
    }

    private static string? SourceText()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "tools", "AgentBroker.Worker", "LocalOutputFile.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        Console.WriteLine("NOTE: LocalOutputFile.cs was not found above " + AppContext.BaseDirectory);
        return null;
    }

    private static class Native
    {
        internal const uint DeleteAccess = 0x00010000, ReadAttributes = 0x80, ShareRead = 0x1, ShareWrite = 0x2, ShareDelete = 0x4,
            OpenExisting = 3, BackupSemantics = 0x02000000;
        internal const int FileStandardInfo = 1, FileCaseSensitiveInfo = 23;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation,
            uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, [Out] byte[] information,
            uint size);
    }
}
