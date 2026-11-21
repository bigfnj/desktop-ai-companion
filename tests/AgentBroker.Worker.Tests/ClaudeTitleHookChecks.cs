using AgentBroker.Core;
using AgentBroker.Worker;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class ClaudeTitleHookChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Private = "SYNTHETIC_PRIVATE_PROMPT_TRANSCRIPT_SUMMARY";
    private const string Title = "Custom user title 雪";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: CLAUDE_TITLE_HOOK_EXECUTED");
        await RegistryAsync(check);
        await HookAsync(check);
        await ProcessAsync(check);
    }

    private static async Task RegistryAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("claude-title");
        var address = new SessionAddress(fixture.Store.BrokerId, "claude:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var row = new BrokerSession(address, "Existing native label", "claude-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle,
            SessionActivity.Idle, fixture.Clock.GetUtcNow().AddMinutes(1));
        fixture.Store.Register(row); fixture.Store.ObserveNativeSession(address, row.Adapter);
        fixture.Tools.SetSessionDeclaration(address, "Manual declared name", "Manual note", "rate_limited", ttlMinutes: 1);
        fixture.Tools.SetSessionTitle(address, Title);
        var listed = (await fixture.Tools.ListSessions(default)).Single(value => value.Address == address);
        check(listed.Label == row.Label && listed.LocalTitle?.Title == Title && listed.LocalTitle.Source == "local_client_custom_title" &&
            listed.LocalDeclaration is { DisplayName: "Manual declared name", Availability: "rate_limited" } &&
            fixture.Store.Directory().Single(value => value.Address == address) == row,
            "CLAUDE_TITLE_SEPARATE: custom title is untrusted local metadata and never replaces native identity, manual declaration or availability");
        fixture.Context.Items["agentbroker.identity"] = Guid.NewGuid();
        check((await fixture.Tools.ListSessions(default)).All(value => value.LocalTitle is null),
            "CLAUDE_TITLE_LOCAL_ONLY: paired callers never receive local custom titles");
        fixture.Context.Items["agentbroker.identity"] = fixture.Store.BrokerId;
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        check(fixture.Runtime.SessionTitles.Snapshot(fixture.Store.IsVerifiedNativeSession).Count == 0,
            "CLAUDE_TITLE_PROOF_REQUIRED: a discovery lapse hides the title without inventing native presence");
        row = row with { LeaseUntil = fixture.Clock.GetUtcNow().AddMinutes(1) };
        fixture.Store.Register(row); fixture.Store.ObserveNativeSession(address, row.Adapter);
        listed = (await fixture.Tools.ListSessions(default)).Single(value => value.Address == address);
        check(listed.LocalTitle?.Title == Title && listed.LocalDeclaration is null,
            "CLAUDE_TITLE_NO_DECLARATION_TTL: the same verified incarnation retains its title after manual availability expires");
        var successor = row with { Address = address with { Incarnation = Guid.NewGuid() } };
        fixture.Store.Register(successor); fixture.Store.ObserveNativeSession(successor.Address, successor.Adapter);
        check((await fixture.Tools.ListSessions(default)).Single(value => value.Address == successor.Address).LocalTitle is null,
            "CLAUDE_TITLE_EXACT_INCARNATION: a successor never inherits its predecessor's title");
        var generic = fixture.Register("Ordinary mailbox");
        check(ToolFixture.Refused(() => fixture.Tools.SetSessionTitle(generic.Address, Title), "native_session_unverified"),
            "CLAUDE_TITLE_GENERIC_REFUSED: generic mailboxes cannot acquire native title eligibility");
        var unverified = row with { Address = new(address.BrokerId, "claude:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()) };
        fixture.Store.Register(unverified);
        check(ToolFixture.Refused(() => fixture.Tools.SetSessionTitle(unverified.Address, Title), "native_session_unverified"),
            "CLAUDE_TITLE_REGISTRATION_NOT_PROOF: a native-looking registration without matching adapter proof is refused");
        fixture.Tools.SetSessionTitle(successor.Address, Title);
        check(fixture.Tools.SetSessionTitle(successor.Address, "") is null &&
            (await fixture.Tools.ListSessions(default)).Single(value => value.Address == successor.Address).LocalTitle is null,
            "CLAUDE_TITLE_CLEAR: explicit empty title clears only the title metadata");
        foreach (string invalid in new[] { new string('x', 161), "title\nline", new string((char)0xD800, 1) })
            check(ToolFixture.Refused(() => fixture.Tools.SetSessionTitle(successor.Address, invalid), "invalid_session_title"),
                "CLAUDE_TITLE_TEXT_BOUND: oversized, control or malformed Unicode titles are refused");
        var registry = new SessionTitleRegistry();
        for (int index = 0; index < SessionTitleRegistry.MaximumTitles; index++) registry.Set(
            new(address.BrokerId, "claude:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), "Bounded", fixture.Clock.GetUtcNow(), _ => true);
        bool limited = false;
        try { registry.Set(new(address.BrokerId, "claude:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), "Overflow", fixture.Clock.GetUtcNow(), _ => true); }
        catch (BrokerException error) { limited = error.Code == "session_title_limit"; }
        check(limited && registry.Snapshot(_ => true).Count == SessionTitleRegistry.MaximumTitles,
            "CLAUDE_TITLE_CAPACITY: current title metadata cannot grow beyond its configured bound");
    }

    private static async Task HookAsync(Action<bool, string> check)
    {
        string id = "11111111-2222-4333-8444-abcdefabcdef";
        var address = new SessionAddress(Guid.NewGuid(), "claude:" + id, Guid.NewGuid());
        ClaudeTitleHook.Reply Reply(object result, int code = 0, string status = "ok") =>
            new(code, JsonSerializer.SerializeToElement(new { status, result }, Json));
        async Task<(int Code, string Output, string Error)> Run(string text, ClaudeTitleHook.Call call, CancellationToken token = default)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var output = new MemoryStream(); using var error = new MemoryStream();
            int code = await ClaudeTitleHook.RunAsync(input, output, error, call, token);
            string stdout = Encoding.UTF8.GetString(output.ToArray()), stderr = Encoding.UTF8.GetString(error.ToArray());
            check(code == 0 && stdout == "{\"suppressOutput\":true}\n" && !stdout.Contains(Private) && !stderr.Contains(Private) &&
                !stdout.Contains(Title) && !stderr.Contains(Title),
                "CLAUDE_HOOK_NEVER_BLOCKS_OR_LEAKS: all outcomes exit zero with fixed output and no custom title, prompt or path");
            return (code, stdout, stderr);
        }
        foreach (string eventName in new[] { "SessionStart", "UserPromptSubmit" })
        {
            var calls = new List<string>();
            JsonElement? sent = null;
            var run = await Run(JsonSerializer.Serialize(new { hook_event_name = eventName, session_id = id, session_title = Title,
                transcript_path = Private, prompt = Private, summary = Private }), (tool, arguments, _) =>
            {
                calls.Add(tool);
                if (tool == "whoami") return Task.FromResult(Reply(new { address, adapter = "claude-native" }));
                sent = arguments.Clone(); return Task.FromResult(Reply(new { }));
            });
            check(calls.SequenceEqual(["whoami", "set_session_title"]) && sent is { } values &&
                values.GetProperty("title").GetString() == Title && values.GetProperty("session").GetProperty("sessionId").GetString() == address.SessionId &&
                !values.GetRawText().Contains(Private, StringComparison.Ordinal) && run.Error == "agentbroker-title-hook: updated\n",
                "CLAUDE_HOOK_CUSTOM_ONLY: documented events forward only their custom title after exact native identity matching");
        }
        foreach (var identity in new[] { new { address = address with { SessionId = "claude:" + Guid.NewGuid().ToString("D") }, adapter = "claude-native" },
            new { address, adapter = "codex-native" } })
        {
            int calls = 0;
            await Run(JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = id, session_title = Title }), (tool, _, _) =>
            { calls++; return Task.FromResult(Reply(identity)); });
            check(calls == 1, "CLAUDE_HOOK_EXACT_WHOAMI: a different native conversation or adapter cannot receive a title update");
        }
        string? cleared = null;
        await Run(JsonSerializer.Serialize(new { hook_event_name = "UserPromptSubmit", session_id = id, prompt = Private, summary = Private,
            transcript_path = Private, title = Private }), (tool, arguments, _) =>
        {
            if (tool == "whoami") return Task.FromResult(Reply(new { address, adapter = "claude-native" }));
            cleared = arguments.GetProperty("title").GetString(); return Task.FromResult(Reply(new { }));
        });
        check(cleared == "", "CLAUDE_HOOK_NO_FALLBACK: missing custom title clears the hint and never derives one from prompt, summary, path or other title fields");
        foreach (string invalid in new[] { "{", "[]",
            JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = id, session_title = Title,
                unused = new string('x', ClaudeTitleHook.MaximumInputBytes) }),
            JsonSerializer.Serialize(new { hook_event_name = "PostToolUse", session_id = id, session_title = Title }),
            JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = id.ToUpperInvariant(), session_title = Title }),
            JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = Guid.Empty.ToString("D"), session_title = Title }),
            JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = id, session_title = "bad\ncontrol" }),
            "{\"hook_event_name\":\"SessionStart\",\"session_id\":\"" + id + "\",\"SESSION_ID\":\"" + id + "\"}" })
        {
            int calls = 0;
            await Run(invalid, (_, _, _) => { calls++; throw new IOException(Private); });
            check(calls == 0, "CLAUDE_HOOK_INVALID_INPUT: unsupported, malformed, ambiguous or oversized hook input never invokes the broker");
        }
        var elapsed = Stopwatch.StartNew();
        int timedCalls = 0;
        var timed = await Run(JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = id, session_title = Title }), async (_, _, token) =>
        { timedCalls++; await Task.Delay(Timeout.Infinite, token); throw new IOException(Private); });
        check(timedCalls == 1 && elapsed.Elapsed < TimeSpan.FromSeconds(3) && timed.Error == "agentbroker-title-hook: unavailable\n",
            "CLAUDE_HOOK_DEADLINE: a stalled broker cannot hold up Claude beyond the fixed short deadline");
    }

    private static async Task ProcessAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "absent-title-broker-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string argument in new[] { typeof(WorkerApplication).Assembly.Location, ClaudeTitleHook.Command, "--data", root }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic hook process did not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = Guid.NewGuid().ToString("D"),
            session_title = Title, prompt = Private, transcript_path = Private }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        string stdout = await output, stderr = await error;
        check(process.ExitCode == 0 && stdout == "{\"suppressOutput\":true}\n" && stderr == "agentbroker-title-hook: unavailable\n" &&
            !Directory.Exists(root),
            "CLAUDE_HOOK_PROCESS_CONTRACT: actual opt-in command exits zero on unavailable broker without tempfiles or private output");
    }
}
