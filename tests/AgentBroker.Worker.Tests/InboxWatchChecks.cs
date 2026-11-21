using AgentBroker.Core;
using AgentBroker.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class InboxWatchChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Private = "SYNTHETIC_PRIVATE_WATCH_TEXT_NEVER_OUTPUT";
    private static readonly SessionAddress Address = new(Guid.Parse("11111111-2222-4333-8444-555555555555"),
        "claude:22222222-3333-4444-8555-666666666666", Guid.Parse("33333333-4444-4555-8666-777777777777"));
    private static string Id(int index) => new Guid(index, 0, 0, new byte[8]).ToString("D");
    private static object Entry(int sequence, string status = "Accepted", string? id = null) =>
        new { arrivalSequence = sequence, receipt = new { message = new { id = id ?? Id(sequence), text = Private }, status } };
    private static InboxWatchClient.Reply Page(object[]? entries = null, string cursor = "QQ==", bool more = false, long? pending = null) =>
        new(0, JsonSerializer.SerializeToElement(new { status = "ok", dispatchState = "confirmed", result = new
        { entries = entries ?? [], nextCursor = cursor, hasMore = more, pendingCount = pending ?? entries?.LongLength ?? 0 },
            advisories = new[] { new { text = Private } }, structuredContent = new { text = Private } }, Json));
    private static InboxWatchClient.Reply Error(string code, int exit = 10) => new(exit,
        JsonSerializer.SerializeToElement(new { status = "tool_error", dispatchState = "confirmed", result = "An error occurred invoking 'read_inbox_page': " + code }, Json));

    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var owner = OwnedScratch.Create("inbox-watch");
        string root = owner.Root;
        Options(check, root);
        await ProtocolAsync(check, root);
        await EdgeCasesAsync(check, root);
        await OutputFailureAsync(check, root);
        await OwnershipAsync(check, root);
        await DeletePendingCleanupAsync(check, root);
        await ParentExitAsync(check, root);
        await ProcessAsync(check, root);
        await ActualTransportAsync(check);
    }

    private static string[] Arguments(string root)
    {
        string data = Path.Combine(root, "data"), arm = Path.Combine(root, "arm");
        Directory.CreateDirectory(data); Directory.CreateDirectory(arm);
        return [InboxWatchClient.Command, "--data", data, "--broker-id", Address.BrokerId.ToString("D"),
            "--session-id", Address.SessionId, "--incarnation", Address.Incarnation.ToString("D"), "--arm-root", arm,
            "--max-seconds", "100", "--interval-seconds", "1", "--unbound"];
    }

    private static void Options(Action<bool, string> check, string root)
    {
        var parsed = InboxWatchClient.Options.Parse(Arguments(root));
        check(parsed.Session == Address && parsed.Unbound && parsed.MaxSeconds == 100 && parsed.IntervalSeconds == 1,
            "WATCH_OPTIONS: explicit exact Claude tuple, caller budget and limited unbound mode parse without retargeting");
        foreach (string value in new[] { "0", "-1", "31536001", "+2", "1.5", "1\n", "" })
            check(Refused(Replace(Arguments(root), "--max-seconds", value)), "WATCH_BUDGET_INPUT: only a positive caller-selected duration up to one year is accepted");
        check(Refused(Arguments(root).Where((_, index) => index is not (11 or 12)).ToArray()),
            "WATCH_BUDGET_REQUIRED: no implicit short deadline or unbounded watch is selected");
        foreach (string[] invalid in new[] {
            Arguments(root).Concat(["--unknown", "x"]).ToArray(), Arguments(root).Concat(["--max-seconds", "100"]).ToArray(),
            Arguments(root).Concat(["--cursor"]).ToArray(), Arguments(root).Concat(["--unbound"]).ToArray(),
            Replace(Arguments(root), "--broker-id", Guid.Empty.ToString("D")), Replace(Arguments(root), "--incarnation", Guid.Empty.ToString("D")),
            Replace(Arguments(root), "--session-id", Address.SessionId.Replace("claude:", "codex:", StringComparison.Ordinal)),
            Replace(Arguments(root), "--session-id", Address.SessionId + "\n"), Replace(Arguments(root), "--data", "relative"),
            Replace(Arguments(root), "--broker-id", "ABCDEF12-3333-4444-8555-66666666ABCD"),
            Replace(Arguments(root), "--incarnation", "ABCDEF12-3333-4444-8555-66666666ABCD"),
            Replace(Arguments(root), "--session-id", "claude:ABCDEF12-3333-4444-8555-66666666ABCD"),
            Replace(Arguments(root), "--data", Path.Combine(root, "x", "..", "data")),
            Replace(Arguments(root), "--arm-root", Path.Combine(root, "arm", ".")),
            Replace(Arguments(root), "--arm-root", root + "\\"), Replace(Arguments(root), "--interval-seconds", "301"),
            Arguments(root).Concat(["--cursor", "QQ==\n"]).ToArray(), Arguments(root).Concat(["--seen-ids", Id(1) + ",bad"]).ToArray(),
            Arguments(root).Concat(["--seen-ids", string.Join(',', Enumerable.Repeat(Id(1), 65))]).ToArray(),
            Arguments(root)[..^1], Arguments(root)[..^1].Concat(["--parent-pid", "1"]).ToArray(),
            Arguments(root).Concat(["--parent-pid", "1", "--parent-start", "1"]).ToArray() })
            check(Refused(invalid), "WATCH_INVALID_ARGUMENTS: malformed, ambiguous, noncanonical or unsupported arming input is refused");
        string[] year = Replace(Arguments(root), "--max-seconds", "31536000"), days = Replace(Arguments(root), "--max-seconds", "259200");
        check(!Refused(year) && !Refused(days) && InboxWatchClient.Options.Parse(year).MaxSeconds == 31_536_000 &&
            InboxWatchClient.Options.Parse(days).MaxSeconds == 259200,
            "WATCH_LONG_EXPLICIT_BUDGET: explicit multi-day and one-year durations are supported without a 48-hour limit");
        using var parent = Process.GetCurrentProcess();
        check(WorkerLifetime.ParentIsAlive(parent.Id, parent.StartTime.ToUniversalTime().ToFileTimeUtc()) &&
            !WorkerLifetime.ParentIsAlive(parent.Id, parent.StartTime.ToUniversalTime().Ticks),
            "WATCH_PARENT_FILETIME: parent-start uses the worker's creation FILETIME, never UTC DateTime ticks");
    }

    private static bool Refused(string[] args)
    { try { InboxWatchClient.Options.Parse(args); return false; } catch (ArgumentException) { return true; } }
    private static string[] Replace(string[] args, string key, string value)
    { var result = args.ToArray(); result[Array.IndexOf(result, key) + 1] = value; return result; }

    private static async Task<(int Code, JsonElement Value)> Run(Action<bool, string> check, string root, InboxWatchClient.Call call,
        InboxWatchClient.Options? options = null, Clock? clock = null)
    {
        clock ??= new Clock();
        using var output = new MemoryStream();
        Task<int> pending = InboxWatchClient.RunAsync(options ?? InboxWatchClient.Options.Parse(Arguments(root)), output, call, clock);
        var budget = Stopwatch.StartNew();
        while (!pending.IsCompleted && budget.Elapsed < TimeSpan.FromSeconds(5))
        { await Task.Delay(2); clock.AdvanceNext(); }
        int code = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        string text = Encoding.UTF8.GetString(output.ToArray());
        if (text.Contains(Private, StringComparison.Ordinal) || text.Split('\n').Length != 2 || !text.EndsWith('\n'))
            throw new InvalidOperationException("WATCH_METADATA_ONLY: every outcome has one JSON line with no private text, raw envelope or exception message");
        using var document = JsonDocument.Parse(text);
        check(HasMetadata(document.RootElement, options?.Unbound == false ? "bound" : "unbound"),
            "WATCH_PARENT_CLEANUP_METADATA: every retained armed outcome reports its exact parent mode and successful owned-lock cleanup");
        return (code, document.RootElement.Clone());
    }

    private static async Task ProtocolAsync(Action<bool, string> check, string root)
    {
        foreach (string state in new[] { "Accepted", "Submitting", "Submitted", "Uncertain" })
        {
            var requests = new List<(string Tool, JsonElement Arguments)>();
            var run = await Run(check, root, (tool, arguments, token) =>
            {
                requests.Add((tool, arguments.Clone()));
                return Task.FromResult(Page([Entry(1, state)]));
            });
            check(requests.Count > 0 && requests.All(request => request.Tool == "read_inbox_page" && request.Arguments.GetProperty("limit").GetInt32() == 16 &&
                request.Arguments.GetProperty("session").Deserialize<SessionAddress>(Json) == Address) && !requests[0].Arguments.TryGetProperty("cursor", out _),
                "WATCH_READ_ONLY_EXACT: only read_inbox_page receives the original exact address and fixed page limit");
            check(run.Code == 0 && run.Value.GetProperty("source").GetString() == "baseline" &&
                run.Value.GetProperty("newCount").GetInt32() == 1 && run.Value.GetProperty("ids")[0].GetString() == Id(1),
                "WATCH_ACTIONABLE_PENDING: an unseen baseline entry alerts in every pending state, including Submitted and Uncertain");
        }
        int pages = 0;
        var multi = await Run(check, root, (_, _, _) => Task.FromResult(++pages == 1
            ? Page(Enumerable.Range(1, 16).Select(index => Entry(index, id: Id(1))).ToArray(), more: true, pending: 17)
            : Page([Entry(17)], "Qg==", pending: 17)));
        check(multi.Code == 0 && pages == 2 && multi.Value.GetProperty("newCount").GetInt32() == 2,
            "WATCH_FULL_SCAN_DEDUP: every page validates before mail and duplicate IDs count once");
        var many = await Run(check, root, (_, _, _) => Task.FromResult(++pages == 3
            ? Page(Enumerable.Range(1, 16).Select(index => Entry(index)).ToArray(), more: true, pending: 17)
            : Page([Entry(17)], "Qg==", pending: 17)));
        check(many.Code == 0 && many.Value.GetProperty("newCount").GetInt32() == 17 && many.Value.GetProperty("ids").GetArrayLength() == 16 &&
            many.Value.GetProperty("more").GetBoolean(), "WATCH_OUTPUT_BOUND: metadata lists at most16 IDs while retaining the full unseen count");

        var options = InboxWatchClient.Options.Parse(Arguments(root)) with { SeenIds = new HashSet<string>([Id(1)]), MaxSeconds = 20 };
        var cursors = new List<string?>();
        int scans = 0;
        var first = await Run(check, root, (_, arguments, _) =>
        {
            cursors.Add(arguments.TryGetProperty("cursor", out var cursor) ? cursor.GetString() : null);
            scans++;
            return Task.FromResult(scans == 1 ? Page([Entry(1, "Submitted")]) : scans == 2 ? Page([], "Qg==", pending: 1) : Page([Entry(2)], "Qw=="));
        }, options);
        check(first.Code == 0 && cursors.SequenceEqual(new string?[] { null, "QQ==", "QQ==" }) && first.Value.GetProperty("source").GetString() == "arrival",
            "WATCH_FIRST_WATERMARK: caller-seen baseline IDs do not alert and later successful polls never advance the first cursor");

        int attempts = 0;
        var partialCursors = new List<string?>();
        var partial = await Run(check, root, (_, arguments, _) =>
        {
            partialCursors.Add(arguments.GetProperty("cursor").GetString());
            return Task.FromResult(++attempts switch { 1 => Page(Enumerable.Range(1,16).Select(index => Entry(index)).ToArray(), "Qg==", true, 17),
                2 => new InboxWatchClient.Reply(12, default), _ => Page([Entry(1)], "Qw==") });
        }, options with { Cursor = "QQ==", SeenIds = new HashSet<string>() });
        check(partial.Code == 0 && partialCursors.SequenceEqual(new[] { "QQ==", "Qg==", "QQ==" }) && partial.Value.GetProperty("newCount").GetInt32() == 1,
            "WATCH_PARTIAL_NO_ADVANCE: an incomplete scan reports no mail and retries the original caller cursor");

        foreach ((string code, int exit) in new[] { ("inbox_cursor_reset_required", 24), ("session_unavailable", 21), ("invalid_inbox_cursor", 21), (Private, 21) })
        {
            int called = 0;
            var ended = await Run(check, root, (_, _, _) => { called++; return Task.FromResult(Error(code)); });
            string reason = code == "inbox_cursor_reset_required" ? "cursor_reset" : code == Private ? "tool_error" : code;
            check(ended.Code == exit && called == 1 && ended.Value.GetProperty("reason").GetString() == reason,
                "WATCH_TERMINAL_TOOL_ERRORS: cursor reset never rebases and fixed error reasons never echo peer text");
        }
        foreach (int exit in new[] { 2, 11, 12, 69, 74, 87 })
        {
            int failed = 0;
            var ended = await Run(check, root, (_, _, _) => { failed++; return Task.FromResult(new InboxWatchClient.Reply(exit, default)); });
            string expected = exit switch { 2 => "invalid_request", 11 => "rpc_error", 12 => "http_error", 69 => "transport_error", 74 => "output_error", _ => "unexpected_exit" };
            check(ended.Code == 22 && failed == 4 && ended.Value.GetProperty("failures").GetInt32() == 4 && ended.Value.GetProperty("last").GetString() == expected,
                "WATCH_FAILURE_BUDGET: local refusal and transport failures are counted, never treated as empty mail");
        }
        int resetCalls = 0;
        var reset = await Run(check, root, (_, _, _) => Task.FromResult(++resetCalls switch
        { <= 3 => new(12, default), 4 => Page(), <= 7 => new(12, default), _ => Page([Entry(1)]) }));
        check(reset.Code == 0 && resetCalls == 8, "WATCH_VALIDATED_RESET: only a fully validated scan resets the consecutive failure budget");

        string good = Page().Envelope.GetRawText();
        foreach (string malformed in new[] {
            good.Replace("\"pendingCount\":0", "\"pendingCount\":-1", StringComparison.Ordinal),
            good.Replace("\"pendingCount\":0", "\"pendingCount\":\"0\"", StringComparison.Ordinal),
            good.Replace("\"pendingCount\":0", "\"unused\":0", StringComparison.Ordinal),
            good.Replace("\"entries\":[]", "\"entries\":{}", StringComparison.Ordinal),
            good.Replace("\"entries\":[]", "\"entries\":null", StringComparison.Ordinal),
            good.Replace("\"hasMore\":false", "\"hasMore\":true", StringComparison.Ordinal),
            good.Replace("\"nextCursor\":\"QQ==\"", "\"nextCursor\":\"QQ==\\n\"", StringComparison.Ordinal),
            good.Replace("\"result\":", "\"notResult\":", StringComparison.Ordinal),
            good.Replace("\"status\":\"ok\"", "\"status\":\"bad\"", StringComparison.Ordinal),
            good.Replace("\"dispatchState\":\"confirmed\"", "\"dispatchState\":\"not_dispatched\"", StringComparison.Ordinal) })
        {
            using var document = JsonDocument.Parse(malformed);
            var ended = await Run(check, root, (_, _, _) => Task.FromResult(new InboxWatchClient.Reply(0, document.RootElement.Clone())));
            check(ended.Code == 22, "WATCH_MALFORMED_IS_FAILURE: malformed normalized metadata never becomes zero mail or reads advisory/structured alternatives");
        }
        var clock = new Clock();
        int stalled = 0;
        var lifetime = await Run(check, root, async (_, _, token) =>
        { stalled++; clock.UtcJump(TimeSpan.FromDays(-30)); await Task.Delay(Timeout.Infinite, token); return Page([Entry(1)]); },
            options with { MaxSeconds = 2 }, clock);
        check(lifetime.Code == 20 && stalled == 1 && clock.GetElapsedTime(0) <= TimeSpan.FromSeconds(2),
            "WATCH_CALLER_MONOTONIC_DEADLINE: caller lifetime caps in-flight cancellation at its monotonic budget despite a backward UTC jump");
        var sleepClock = new Clock();
        var sleepBudget = await Run(check, root, (_, _, _) => Task.FromResult(Page()), options with { MaxSeconds = 2, IntervalSeconds = 300 }, sleepClock);
        check(sleepBudget.Code == 20 && sleepClock.GetElapsedTime(0) <= TimeSpan.FromSeconds(2),
            "WATCH_SLEEP_BUDGET: a300-second interval cannot overrun the remaining explicit lifetime");
    }

    private static async Task OwnershipAsync(Action<bool, string> check, string root)
    {
        var options = InboxWatchClient.Options.Parse(Arguments(root));
        string[] domains = [InboxWatchClient.ArmKey(options), InboxWatchClient.ArmKey(options with { DataDirectory = root + "-other" }),
            InboxWatchClient.ArmKey(options with { Session = Address with { BrokerId = Guid.NewGuid() } }),
            InboxWatchClient.ArmKey(options with { Session = Address with { SessionId = "claude:" + Guid.NewGuid().ToString("D") } }),
            InboxWatchClient.ArmKey(options with { Session = Address with { Incarnation = Guid.NewGuid() } })];
        check(domains.Distinct(StringComparer.Ordinal).Count() == 5 &&
            domains[0] == InboxWatchClient.ArmKey(options with { DataDirectory = options.DataDirectory.ToUpperInvariant() }),
            "WATCH_ARM_KEY_DOMAIN: data, broker, session and incarnation separate arm ownership while Windows path case is normalized");
        string lockPath = Path.Combine(options.ArmRoot, InboxWatchClient.ArmKey(options) + ".lock");
        using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            int calls = 0;
            var loser = await Run(check, root, (_, _, _) => { calls++; return Task.FromResult(Page([Entry(1)])); });
            check(loser.Code == 25 && calls == 0 && held.CanRead,
                "WATCH_ONE_ARM: a competing arm neither polls nor deletes the owner's exclusive lock");
        }
        check(!File.Exists(lockPath) && Directory.GetFiles(options.DataDirectory).Length == 0 && Directory.GetDirectories(options.DataDirectory).Length == 0 &&
            Directory.GetFiles(options.ArmRoot).Length == 0 && Directory.GetDirectories(options.ArmRoot).Length == 0,
            "WATCH_NO_REQUEST_OR_SAMPLE_FILES: only the delete-on-close lock is used and it is gone after the owner closes");
        var ownerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ownerOutput = new MemoryStream();
        using var rivalOutput = new MemoryStream();
        Task<int> owning = InboxWatchClient.RunAsync(options, ownerOutput, async (_, _, _) =>
        { ownerEntered.TrySetResult(); await ownerRelease.Task; return Page([Entry(1)]); }, TimeProvider.System);
        Task<int>? rival = null;
        try
        {
            await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            int rivalCalls = 0;
            rival = InboxWatchClient.RunAsync(options, rivalOutput, (_, _, _) =>
            { rivalCalls++; return Task.FromResult(Page([Entry(2)])); }, TimeProvider.System);
            int result = await rival.WaitAsync(TimeSpan.FromSeconds(2));
            check(result == 25 && rivalCalls == 0 && !owning.IsCompleted && File.Exists(lockPath) &&
                !File.Exists(Path.Combine(options.DataDirectory, Path.GetFileName(lockPath))) && Directory.GetFiles(options.DataDirectory).Length == 0,
                "WATCH_PRODUCT_ONE_ARM: an actual product arm excludes a second product engine before it polls and survives the rival attempt");
        }
        finally
        {
            ownerRelease.TrySetResult();
            await owning.WaitAsync(TimeSpan.FromSeconds(2));
            if (rival is not null) await rival.WaitAsync(TimeSpan.FromSeconds(2));
        }
        check(owning.Result == 0 && !File.Exists(lockPath) && Directory.GetFiles(options.DataDirectory).Length == 0 &&
            HasMetadata(Encoding.UTF8.GetString(ownerOutput.ToArray()), "unbound"),
            "WATCH_PRODUCT_ARM_RELEASE: the surviving owner finishes normally and its own delete-on-close lock is absent");
        using var current = Process.GetCurrentProcess();
        int refusedCalls = 0;
        var mismatch = await Run(check, root, (_, _, _) => { refusedCalls++; return Task.FromResult(Page([Entry(1)])); },
            options with { Unbound = false, ParentPid = current.Id, ParentStartFileTime = current.StartTime.ToUniversalTime().ToFileTimeUtc() + 1 });
        check(mismatch.Code == 2 && refusedCalls == 0, "WATCH_PARENT_IDENTITY: an existing PID with a different creation time is refused before dispatch");
        string target = Path.Combine(root, "arm-target"), junction = Path.Combine(root, "arm-junction");
        Directory.CreateDirectory(target);
        try
        {
            HandoffClientChecks.CreateJunction(junction, target);
            int calls = 0;
            var linked = await Run(check, root, (_, _, _) => { calls++; return Task.FromResult(Page([Entry(1)])); }, options with { ArmRoot = junction });
            check(linked.Code == 2 && calls == 0 && Directory.GetFiles(target).Length == 0,
                "WATCH_ARM_PLAIN: a linked arm root is refused before any poll or lock write through it");
            string sub = Path.Combine(target, "sub"); Directory.CreateDirectory(sub);
            calls = 0;
            var ancestor = await Run(check, root, (_, _, _) => { calls++; return Task.FromResult(Page([Entry(1)])); },
                options with { ArmRoot = Path.Combine(junction, "sub") });
            check(ancestor.Code == 2 && calls == 0 && Directory.GetFiles(sub).Length == 0,
                "WATCH_ARM_ANCESTOR_PLAIN: a junction above the arm root is refused before polling or writing through it");
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction, false);
            if (Directory.Exists(Path.Combine(target, "sub"))) Directory.Delete(Path.Combine(target, "sub"), false);
            Directory.Delete(target, false);
        }
    }

    private static async Task DeletePendingCleanupAsync(Action<bool, string> check, string root)
    {
        var options = InboxWatchClient.Options.Parse(Arguments(root));
        string path = Path.Combine(options.ArmRoot, InboxWatchClient.ArmKey(options) + ".lock");
        SafeFileHandle? holder = null;
        int? holderOpenError = null;
        bool existedBeforeArm = File.Exists(path), existedDuringArm = false;
        using var output = new MemoryStream();
        int calls = 0;
        try
        {
            int code = await InboxWatchClient.RunAsync(options, output, (_, _, _) =>
            {
                calls++;
                existedDuringArm = File.Exists(path);
                holder = CreateFileW(@"\\?\" + path, 0x80, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                if (holder.IsInvalid) holderOpenError = Marshal.GetLastWin32Error();
                return Task.FromResult(Page([Entry(1)]));
            }, new Clock());
            Console.WriteLine($"WATCH_ATTRIBUTES_HOLDER_OBSERVATION calls={calls} nonnull={holder is not null} invalid={holder?.IsInvalid} invalidWin32={holderOpenError?.ToString() ?? "null"} existedBeforeArm={existedBeforeArm} existedDuringArm={existedDuringArm} existsAfterRun={File.Exists(path)} native={code} pathLength={path.Length} runtime={RuntimeInformation.FrameworkDescription} environment={Environment.Version} drive={Path.GetPathRoot(root)} format={new DriveInfo(Path.GetPathRoot(root)!).DriveFormat}");
            check(calls == 1 && holder is not null && !holder.IsInvalid && File.Exists(path),
                "WATCH_CLEANUP_ATTRIBUTES_HOLDER: an attributes-only holder opens during the real exclusive arm and retains its delete-pending name");
            using var document = JsonDocument.Parse(output.ToArray());
            check(code == 0 && HasMetadata(document.RootElement, "unbound", "failed"),
                "WATCH_CLEANUP_DELETE_PENDING: a visible delete-pending lock reports cleanup failed without changing the mail exit code");
            using var secondOutput = new MemoryStream();
            int secondCalls = 0;
            int second = await InboxWatchClient.RunAsync(options, secondOutput, (_, _, _) =>
            { secondCalls++; return Task.FromResult(Page([Entry(2)])); }, new Clock());
            string secondText = Encoding.UTF8.GetString(secondOutput.ToArray());
            check(second == 2 && secondCalls == 0 && HasReason(secondText, "arm_directory"),
                "WATCH_CLEANUP_DELETE_PENDING_REARM: the retained delete-pending name refuses a second arm before polling");
        }
        finally
        {
            holder?.Dispose();
            Console.WriteLine($"WATCH_ATTRIBUTES_HOLDER_CLOSED lockExists={File.Exists(path)}");
        }
        check(!File.Exists(path), "WATCH_CLEANUP_ATTRIBUTES_RELEASE: closing the exact attributes holder releases the delete-pending lock name");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    private static async Task OutputFailureAsync(Action<bool, string> check, string root)
    {
        foreach (bool flush in new[] { false, true })
        {
            using var output = new FailingOutput(flush);
            int code = await InboxWatchClient.RunAsync(InboxWatchClient.Options.Parse(Arguments(root)), output,
                (_, _, _) => Task.FromResult(Page([Entry(1)])), new Clock());
            check(code == 26 && code != 0 && !Encoding.UTF8.GetString(output.ToArray()).Contains(Private, StringComparison.Ordinal),
                flush ? "WATCH_OUTPUT_FAILURE_FLUSH: an unflushed mail line returns nonzero without exposing the exception" :
                        "WATCH_OUTPUT_FAILURE_WRITE: an unwritten mail line returns nonzero without exposing the exception");
        }
        using var first = new FailFirstOutput();
        int firstCode = await InboxWatchClient.RunAsync(InboxWatchClient.Options.Parse(Arguments(root)), first,
            (_, _, _) => Task.FromResult(Page([Entry(1)])), new Clock());
        check(firstCode == 26 && first.Writes == 1 && first.Length == 0,
            "WATCH_OUTPUT_SINGLE_WRITE: a failed first write is not retried even when the next write would succeed");
    }

    private sealed class FailFirstOutput : MemoryStream
    {
        internal int Writes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++Writes == 1) throw new IOException(Private);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingOutput(bool failOnFlush) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            !failOnFlush ? throw new IOException(Private) : base.WriteAsync(buffer, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new IOException(Private);
    }

    private static async Task EdgeCasesAsync(Action<bool, string> check, string root)
    {
        var options = InboxWatchClient.Options.Parse(Arguments(root));
        foreach (object[] invalid in new[] {
            Enumerable.Range(1,17).Select(index => Entry(index)).ToArray(), new[] { Entry(0) },
            new[] { Entry(1), Entry(1) }, new[] { Entry(1, id: Guid.Empty.ToString("D")) },
            new[] { Entry(1, id: Id(1) + "\n") }, new[] { Entry(1, "Delivered") }, new[] { Entry(1, "accepted") } })
        {
            var rejected = await Run(check, root, (_, _, _) => Task.FromResult(Page(invalid)));
            check(rejected.Code == 22 && rejected.Value.GetProperty("last").GetString() == "malformed_page",
                "WATCH_ENTRY_SHAPE: oversized, nonforward, noncanonical or nonpending entries invalidate the complete scan");
        }
        var shortCount = Page([Entry(1)], pending: 0);
        var lowPending = await Run(check, root, (_, _, _) => Task.FromResult(shortCount));
        check(lowPending.Code == 22, "WATCH_PENDING_COUNT: pendingCount cannot be lower than the page entries");
        var shortContinuation = await Run(check, root, (tool, arguments, token) => Task.FromResult(arguments.TryGetProperty("cursor", out _)
            ? Page([Entry(2)], "Qg==") : Page([Entry(1)], more: true)));
        check(shortContinuation.Code == 22, "WATCH_HASMORE_FULL: a short hasMore page is rejected even when its cursor progresses to valid mail");
        int cursorCalls = 0;
        var noProgress = await Run(check, root, (_, _, _) => { cursorCalls++; return Task.FromResult(Page(Enumerable.Range(1,16).Select(index => Entry(index)).ToArray(), more: true)); }, options with { Cursor = "QQ==" });
        check(noProgress.Code == 22 && cursorCalls == 4, "WATCH_CURSOR_PROGRESS: a full continuation page must change its cursor");
        int calls = 0;
        var crossPage = await Run(check, root, (_, _, _) => Task.FromResult(++calls % 2 == 1
            ? Page(Enumerable.Range(1,16).Select(index => Entry(index)).ToArray(), more: true) : Page([Entry(1)], "Qg==")));
        check(crossPage.Code == 22 && calls == 8, "WATCH_SEQUENCE_ACROSS_PAGES: arrival order stays strictly forward across page boundaries");
        foreach (string alternative in new[] { "structuredContent", "advisories" })
        {
            JsonElement valid = Page([Entry(1)]).Envelope.GetProperty("result");
            JsonElement empty = Page().Envelope.GetProperty("result");
            var envelope = new Dictionary<string, object> { ["status"] = "ok", ["dispatchState"] = "confirmed", ["result"] = empty,
                [alternative] = alternative == "advisories" ? new[] { valid } : valid };
            var ignored = await Run(check, root, (_, _, _) => Task.FromResult(new InboxWatchClient.Reply(0, JsonSerializer.SerializeToElement(envelope))), options with { MaxSeconds = 2 });
            check(ignored.Code == 20 && !ignored.Value.TryGetProperty("ids", out _),
                "WATCH_RESULT_ONLY: a valid-looking alternative page in structured content or advisories never becomes inbox mail");
        }
        string duplicate = Page().Envelope.GetRawText().Replace("\"status\":\"ok\"", "\"status\":\"bad\",\"status\":\"ok\"", StringComparison.Ordinal);
        using (var duplicateDoc = JsonDocument.Parse(duplicate))
        {
            var rejected = await Run(check, root, (_, _, _) => Task.FromResult(new InboxWatchClient.Reply(0, duplicateDoc.RootElement.Clone())));
            check(rejected.Code == 22, "WATCH_UNAMBIGUOUS_FIELDS: duplicate protocol properties are not silently collapsed");
        }
        int partialCalls = 0;
        var unreported = await Run(check, root, (_, _, _) => Task.FromResult(++partialCalls == 1
            ? Page(Enumerable.Range(1,16).Select(index => Entry(index)).ToArray(), more: true) : Error("inbox_cursor_reset_required")));
        check(unreported.Code == 24 && unreported.Value.GetProperty("unreported").GetInt32() == 16 && !unreported.Value.TryGetProperty("ids", out _),
            "WATCH_UNREPORTED: IDs from a partial scan are counted on terminal output but never reported as fully validated mail");
        partialCalls = 0;
        var cleared = await Run(check, root, (_, _, _) => Task.FromResult(++partialCalls switch
        { 1 => Page(Enumerable.Range(1,16).Select(index => Entry(index)).ToArray(), more: true),
          2 => new(12, default), 3 => Page(), _ => Error("inbox_cursor_reset_required") }));
        check(cleared.Code == 24 && cleared.Value.GetProperty("unreported").GetInt32() == 0,
            "WATCH_UNREPORTED_CLEARED: a complete valid scan clears prior partial evidence");
        int pages = 0;
        var bounded = await Run(check, root, (_, _, _) =>
        {
            int start = pages++ * 16 + 1;
            return Task.FromResult(Page(Enumerable.Range(start,16).Select(index => Entry(index)).ToArray(), Convert.ToBase64String(BitConverter.GetBytes(pages)), true, 1024));
        });
        check(bounded.Code == 22 && pages == 256 && bounded.Value.GetProperty("last").GetString() == "page_budget",
            "WATCH_PAGE_BUDGET: a broken continuation chain stops after64 pages per failed scan");
    }

    private static async Task ParentExitAsync(Action<bool, string> check, string root)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(InboxWatchChecks).Assembly.Location); info.ArgumentList.Add("--inbox-watch-parent-child");
        using var child = Process.Start(info) ?? throw new IOException("Synthetic watch parent not started.");
        Console.WriteLine($"WATCH_PARENT_CHILD_START kind=sleep pid={child.Id} filetime={child.StartTime.ToUniversalTime().ToFileTimeUtc()}");
        var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> parentOutput = ReadParentOutputAsync(child.StandardOutput, ready), parentError = child.StandardError.ReadToEndAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var output = new MemoryStream();
        var clock = new SleepClock();
        Task<int>? watch = null;
        try
        {
            bool parentReady = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(3))) == ready.Task &&
                ready.Task.IsCompletedSuccessfully && ready.Task.Result == "INBOX_WATCH_PARENT_READY";
            check(parentReady, "WATCH_PARENT_EXIT_INTERRUPTS: the exact owned parent exiting interrupts a300-second sleep promptly");
            var options = InboxWatchClient.Options.Parse(Arguments(root)) with { Unbound = false, ParentPid = child.Id,
                ParentStartFileTime = child.StartTime.ToUniversalTime().ToFileTimeUtc(), IntervalSeconds = 300, MaxSeconds = 600 };
            watch = InboxWatchClient.RunAsync(options, output, (_, _, _) => Task.FromResult(Page()), clock, stop.Token);
            check(await Task.WhenAny(clock.Sleeping.Task, Task.Delay(TimeSpan.FromSeconds(3))) == clock.Sleeping.Task,
                "WATCH_PARENT_EXIT_INTERRUPTS: the exact owned parent exiting interrupts a300-second sleep promptly");
            child.Kill(); await child.WaitForExitAsync(stop.Token);
            bool prompt = await Task.WhenAny(watch, Task.Delay(TimeSpan.FromSeconds(2))) == watch;
            int code = prompt ? await watch : -1;
            check(prompt && code == 23 && HasReason(Encoding.UTF8.GetString(output.ToArray()), "parent_exited"),
                "WATCH_PARENT_EXIT_INTERRUPTS: the exact owned parent exiting interrupts a300-second sleep promptly");
        }
        finally
        {
            stop.Cancel();
            if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            if (watch is not null) await watch.WaitAsync(TimeSpan.FromSeconds(3));
            await parentOutput.WaitAsync(TimeSpan.FromSeconds(1)); await parentError.WaitAsync(TimeSpan.FromSeconds(1));
            Console.WriteLine($"WATCH_PARENT_CHILD_CLOSED kind=sleep pid={child.Id} native={child.ExitCode} stdoutEof=True stderrEof=True");
        }
    }

    private sealed class SleepClock : TimeProvider
    {
        internal TaskCompletionSource Sleeping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime >= TimeSpan.FromSeconds(299)) Sleeping.TrySetResult();
            return timer;
        }
    }

    private static async Task ProcessAsync(Action<bool, string> check, string root)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        info.ArgumentList.Add(typeof(WorkerApplication).Assembly.Location);
        foreach (string argument in Replace(Arguments(root), "--max-seconds", "0")) info.ArgumentList.Add(argument);
        using var child = Process.Start(info) ?? throw new IOException("Synthetic watch process not started.");
        child.StandardInput.Close();
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            Task ended = child.WaitForExitAsync();
            check(await Task.WhenAny(ended, Task.Delay(TimeSpan.FromSeconds(6))) == ended,
                "WATCH_COMMAND_DISPATCH: the actual compiled command refuses invalid arming with one JSON line and never starts a worker");
            await ended;
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } }
        string output = await stdout.WaitAsync(TimeSpan.FromSeconds(1)), error = await stderr.WaitAsync(TimeSpan.FromSeconds(1));
        check(child.ExitCode == 2 && error.Length == 0 && output.Split('\n').Length == 2 && HasReason(output, "invalid_arguments"),
            "WATCH_COMMAND_DISPATCH: the actual compiled command refuses invalid arming with one JSON line and never starts a worker");
    }

    private static async Task ActualTransportAsync(Action<bool, string> check)
    {
        using var owner = OwnedScratch.Create("inbox-watch-http");
        string key = "";
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(owner.Root, "Synthetic inbox watcher", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, []);
            key = AgentBroker.Worker.Security.BrokerIdentity.KeyNameFor(owner.Root, worker.Runtime.Store.BrokerId);
            int holdRead = 0;
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readAborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            worker.Application.Use(async (context, next) =>
            {
                if (Volatile.Read(ref holdRead) != 0 && context.Request.Path == "/mcp" && context.Items["agentbroker.identity"] is Guid caller && caller == worker.Runtime.Store.BrokerId)
                {
                    context.Request.EnableBuffering();
                    using var requestDocument = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                    context.Request.Body.Position = 0;
                    var query = requestDocument.RootElement;
                    if (query.TryGetProperty("method", out var method) && method.GetString() == "tools/call" &&
                        query.GetProperty("params").GetProperty("name").GetString() == "read_inbox_page")
                    {
                        readStarted.TrySetResult();
                        try
                        {
                            await Task.WhenAny(Task.Delay(Timeout.Infinite, context.RequestAborted), releaseRead.Task);
                            if (context.RequestAborted.IsCancellationRequested) { readAborted.TrySetResult(); return; }
                        }
                        catch (OperationCanceledException) { readAborted.TrySetResult(); return; }
                    }
                }
                await next(context);
            });
            await worker.StartAsync();
            var now = DateTimeOffset.UtcNow;
            var address = Address with { BrokerId = worker.Runtime.Store.BrokerId };
            var target = new BrokerSession(address, "Synthetic watch target", "claude-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle,
                SessionActivity.Busy, now.AddMinutes(1));
            worker.Runtime.Store.Register(target); worker.Runtime.Store.ObserveNativeSession(address, target.Adapter);
            var sender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic sender", false);
            var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, address, Private, now, now.AddMinutes(1));
            worker.Runtime.Store.Accept(message, worker.Runtime.Store.BrokerId);
            var args = Replace(Arguments(owner.Root), "--data", owner.Root);
            args = Replace(args, "--broker-id", address.BrokerId.ToString("D"));
            using var output = new MemoryStream();
            var actualOptions = InboxWatchClient.Options.Parse(args);
            int code = await InboxWatchClient.RunAsync(actualOptions, output, InboxWatchClient.CreateCall(owner.Root), TimeProvider.System);
            string text = Encoding.UTF8.GetString(output.ToArray());
            check(code == 0 && HasId(text, message.Id.ToString("D")) && !text.Contains(Private, StringComparison.Ordinal) && HasMetadata(text, "unbound") &&
                worker.Runtime.Store.FindReceiptWithText(message.Id)?.Status == MessageStatus.Accepted,
                "WATCH_REAL_AUTHENTICATED_TRANSPORT: production RunToolAsync reads real pending metadata without acknowledging or printing text");
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            info.ArgumentList.Add(typeof(WorkerApplication).Assembly.Location);
            foreach (string argument in args) info.ArgumentList.Add(argument);
            using var child = Process.Start(info) ?? throw new IOException("Synthetic authenticated watcher did not start.");
            child.StandardInput.Close();
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
            try
            {
                Task ended = child.WaitForExitAsync();
                check(await Task.WhenAny(ended, Task.Delay(TimeSpan.FromSeconds(8))) == ended,
                    "WATCH_COMMAND_AUTHENTICATED: actual Program dispatch reaches the verified transport and leaves the original mail unacknowledged");
                await ended;
            }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } }
            string actual = await stdout.WaitAsync(TimeSpan.FromSeconds(1)), errors = await stderr.WaitAsync(TimeSpan.FromSeconds(1));
            check(child.ExitCode == 0 && errors.Length == 0 && actual.Split('\n').Length == 2 && !actual.Contains(Private, StringComparison.Ordinal) &&
                HasId(actual, message.Id.ToString("D")) && HasMetadata(actual, "unbound") &&
                worker.Runtime.Store.FindReceiptWithText(message.Id)?.Status == MessageStatus.Accepted,
                "WATCH_COMMAND_AUTHENTICATED: actual Program dispatch reaches the verified transport and leaves the original mail unacknowledged");
            var emptyAddress = address with { SessionId = "claude:" + Guid.NewGuid().ToString("D"), Incarnation = Guid.NewGuid() };
            worker.Runtime.Store.Register(target with { Address = emptyAddress });
            worker.Runtime.Store.ObserveNativeSession(emptyAddress, target.Adapter);
            string launch = Path.Combine(owner.Root, "launch"), renamed = Path.Combine(owner.Root, "launch-renamed");
            Directory.CreateDirectory(launch);
            var emptyArgs = Replace(Replace(Replace(args, "--session-id", emptyAddress.SessionId), "--incarnation", emptyAddress.Incarnation.ToString("D")), "--max-seconds", "2");
            var emptyOptions = InboxWatchClient.Options.Parse(emptyArgs);
            var cwdInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = launch };
            cwdInfo.ArgumentList.Add(typeof(WorkerApplication).Assembly.Location);
            foreach (string argument in emptyArgs) cwdInfo.ArgumentList.Add(argument);
            using var cwdChild = Process.Start(cwdInfo) ?? throw new IOException("Synthetic launch-directory watcher did not start.");
            Task<string> cwdOut = cwdChild.StandardOutput.ReadToEndAsync(), cwdErr = cwdChild.StandardError.ReadToEndAsync();
            string lockPath = Path.Combine(emptyOptions.ArmRoot, InboxWatchClient.ArmKey(emptyOptions) + ".lock");
            var started = Stopwatch.StartNew();
            try
            {
                while (!File.Exists(lockPath) && !cwdChild.HasExited && started.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(10);
                check(File.Exists(lockPath) && !cwdChild.HasExited && !File.Exists(Path.Combine(emptyOptions.DataDirectory, Path.GetFileName(lockPath))), "WATCH_CWD_ARMED: the owned compiled watcher is still armed before testing its launch-directory handle");
                bool moved = false;
                try { Directory.Move(launch, renamed); moved = true; } catch (IOException) { }
                check(moved, "WATCH_RELEASES_LAUNCH_DIRECTORY: the armed compiled watcher does not pin its caller's working directory");
                Task lifetimeEnded = cwdChild.WaitForExitAsync();
                check(await Task.WhenAny(lifetimeEnded, Task.Delay(TimeSpan.FromSeconds(5))) == lifetimeEnded,
                    "WATCH_COMMAND_LIFETIME: the actual authenticated command ends at its explicit short budget without touching mailbox state");
                await lifetimeEnded;
                string ended = await cwdOut.WaitAsync(TimeSpan.FromSeconds(1)), endedError = await cwdErr.WaitAsync(TimeSpan.FromSeconds(1));
                check(cwdChild.ExitCode == 20 && endedError.Length == 0 && HasReason(ended, "lifetime") && HasMetadata(ended, "unbound"),
                    "WATCH_COMMAND_LIFETIME: the actual authenticated command ends at its explicit short budget without touching mailbox state");
            }
            finally
            {
                if (!cwdChild.HasExited) { cwdChild.Kill(true); await cwdChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                await cwdOut.WaitAsync(TimeSpan.FromSeconds(1)); await cwdErr.WaitAsync(TimeSpan.FromSeconds(1));
            }
            var parentInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true, RedirectStandardError = true };
            parentInfo.ArgumentList.Add(typeof(InboxWatchChecks).Assembly.Location); parentInfo.ArgumentList.Add("--inbox-watch-parent-child");
            using var parent = Process.Start(parentInfo) ?? throw new IOException("Synthetic blocked-read parent did not start.");
            Console.WriteLine($"WATCH_PARENT_CHILD_START kind=blocked pid={parent.Id} filetime={parent.StartTime.ToUniversalTime().ToFileTimeUtc()}");
            var parentReady = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string> parentOut = ReadParentOutputAsync(parent.StandardOutput, parentReady), parentErr = parent.StandardError.ReadToEndAsync();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var blockedOutput = new MemoryStream();
            Task<int>? blocked = null;
            try
            {
                bool ready = await Task.WhenAny(parentReady.Task, Task.Delay(TimeSpan.FromSeconds(3))) == parentReady.Task &&
                    parentReady.Task.IsCompletedSuccessfully && parentReady.Task.Result == "INBOX_WATCH_PARENT_READY";
                check(ready, "WATCH_PARENT_CANCELS_BLOCKED_CALL: exact parent exit cancels the authenticated HTTP read and settles the watcher without abandoning its task");
                Volatile.Write(ref holdRead, 1);
                var bound = actualOptions with { Unbound = false, ParentPid = parent.Id, ParentStartFileTime = parent.StartTime.ToUniversalTime().ToFileTimeUtc() };
                blocked = InboxWatchClient.RunAsync(bound, blockedOutput, InboxWatchClient.CreateCall(owner.Root), TimeProvider.System, stop.Token);
                check(await Task.WhenAny(readStarted.Task, Task.Delay(TimeSpan.FromSeconds(3))) == readStarted.Task,
                    "WATCH_PARENT_CANCELS_BLOCKED_CALL: exact parent exit cancels the authenticated HTTP read and settles the watcher without abandoning its task");
                parent.Kill(); await parent.WaitForExitAsync(stop.Token);
                bool ended = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromSeconds(2))) == blocked;
                bool cancelled = await Task.WhenAny(readAborted.Task, Task.Delay(TimeSpan.FromSeconds(1))) == readAborted.Task;
                int exit = ended ? await blocked : -1;
                check(ended && cancelled && exit == 23 && HasMetadata(Encoding.UTF8.GetString(blockedOutput.ToArray()), "bound"),
                    "WATCH_PARENT_CANCELS_BLOCKED_CALL: exact parent exit cancels the authenticated HTTP read and settles the watcher without abandoning its task");
            }
            finally
            {
                Volatile.Write(ref holdRead, 0); releaseRead.TrySetResult(); stop.Cancel();
                if (!parent.HasExited) { parent.Kill(true); await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                if (blocked is not null) await blocked.WaitAsync(TimeSpan.FromSeconds(3));
                await parentOut.WaitAsync(TimeSpan.FromSeconds(1)); await parentErr.WaitAsync(TimeSpan.FromSeconds(1));
                Console.WriteLine($"WATCH_PARENT_CHILD_CLOSED kind=blocked pid={parent.Id} native={parent.ExitCode} stdoutEof=True stderrEof=True");
            }
        }
        finally
        {
            if (key.Length > 0 && CngKey.Exists(key, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var handle = CngKey.Open(key, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); handle.Delete(); }
        }
    }

    private static async Task<string> ReadParentOutputAsync(StreamReader reader, TaskCompletionSource<string?> ready)
    {
        try
        {
            string? first = await reader.ReadLineAsync(); ready.TrySetResult(first);
            return (first ?? "") + "\n" + await reader.ReadToEndAsync();
        }
        catch (Exception error) { ready.TrySetException(error); throw; }
    }

    private static bool HasMetadata(string text, string parent)
    {
        try { using var document = JsonDocument.Parse(text); return HasMetadata(document.RootElement, parent); }
        catch (JsonException) { return false; }
    }
    private static bool HasMetadata(JsonElement value, string parent, string expectedCleanup = "ok") =>
        value.TryGetProperty("parent", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == parent &&
        value.TryGetProperty("cleanup", out var cleanup) && cleanup.ValueKind == JsonValueKind.String && cleanup.GetString() == expectedCleanup;

    private static bool HasReason(string text, string reason)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("reason", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() == reason;
        }
        catch (JsonException) { return false; }
    }

    private static bool HasId(string text, string id)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("ids", out var value) &&
                value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == id);
        }
        catch (JsonException) { return false; }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ClockTimer> timers = [];
        private long ticks;
        private DateTimeOffset utc = new(2026, 11, 12, 1, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() { lock (gate) return utc; }
        internal void AdvanceNext()
        {
            long due;
            lock (gate) { if (timers.Count == 0) return; due = timers.Min(timer => timer.Due); }
            Advance(TimeSpan.FromTicks(Math.Max(0, due - GetTimestamp())));
        }
        internal void UtcJump(TimeSpan value) { lock (gate) utc += value; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ClockTimer(this, callback, state); timer.Change(dueTime, period); return timer; }
        internal void Advance(TimeSpan value)
        {
            ClockTimer[] fire;
            lock (gate)
            {
                ticks += value.Ticks; utc += value;
                fire = timers.Where(timer => timer.Due <= ticks).ToArray();
                foreach (var timer in fire) { timers.Remove(timer); timer.Due = long.MaxValue; }
            }
            foreach (var timer in fire) timer.Fire();
        }
        private sealed class ClockTimer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    owner.timers.Remove(this);
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                    if (Due != long.MaxValue) owner.timers.Add(this);
                }
                return true;
            }
            internal void Fire() => callback(state);
            public void Dispose() { lock (owner.gate) owner.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
