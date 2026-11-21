using AgentBroker.CodexDesktopAdapter;

// Lane broker-163, N-agentbroker-code-owner-provenance-01 (REPORT ONLY, owner's decision 2026-10-30): who holds a Codex
// thread's writer lock, and whether it is a recognised engine. Synthetic holders, engines, paths and processes only; each
// label names the branch of CodexEngineBinding or WindowsClientProcesses a mutation must break.
internal static class EngineBindingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        ClassifierChecks(check);
        PathChecks(check);
        EngineSelectionChecks(check);
        AdditionalHomeChecks(check);
    }

    private static void AdditionalHomeChecks(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: CODEX_HOME_PRODUCT_EXECUTED");
        string home = Path.Combine(Path.GetTempPath(), "agentbroker-additional-home");
        string defaultHome = Path.Combine(Path.GetTempPath(), "agentbroker-default-home");
        var normalized = CodexHomePaths.NormalizeAdditional([home + Path.DirectorySeparatorChar, home.ToUpperInvariant()]);
        check(normalized.SequenceEqual([Path.GetFullPath(home)]) && CodexHomePaths.IsValidOptional(""),
            "HOME_EXPLICIT_NORMALIZED: explicit local homes are normalized and deduplicated; no additional home preserves the default");
        foreach (string invalid in new[] { "relative", @"\\server\share\codex", @"\\?\C:\codex", @"C:\x\..\codex",
            @"C:\x\.\codex", @"C:\codex:stream", "C:\\bad\npath", @"C:\" + new string('x', 510) })
            check(!CodexHomePaths.IsValidOptional(invalid),
                "HOME_LOCAL_ABSOLUTE: relative, network, device, traversal, stream, control and oversized paths are refused");
        bool tooMany = false;
        try { CodexHomePaths.NormalizeAdditional(Enumerable.Repeat(home, 5)); } catch (ArgumentException) { tooMany = true; }
        check(tooMany, "HOME_BOUND: more than four supplied additional homes is refused even when duplicate");
        var held = new WriterLockObservation(WriterLockState.Queried, [new(41, 100)]);
        CodexEngine[] engines = [new(41, 100, CodexEngineHost.Code)];
        check(CodexEngineBinding.ClassifyHomes([WriterLockObservation.Missing, held], engines).Bound,
            "HOME_ADDITIONAL_BOUND: a holder in the configured home can bind while the default home is absent");
        check(CodexEngineBinding.ClassifyHomes([held, held], engines) is { Bound: false, Reason: "ambiguous_homes" },
            "HOME_AMBIGUOUS: two occupied homes never choose one as the conversation owner");
        check(CodexEngineBinding.ClassifyHomes([held, WriterLockObservation.Reparse], engines).Reason == "lock_path_reparse" &&
            CodexEngineBinding.ClassifyHomes([held, WriterLockObservation.Failed], engines).Reason == "query_failed",
            "HOME_UNAVAILABLE: an unsafe or unreadable configured home cannot be reported as a unique known owner");
        string[] homes = [defaultHome, home];
        var seen = new List<string>();
        int snapshots = 0;
        Guid first = Guid.Parse("01234567-89ab-4cde-8f01-23456789abcd");
        Guid second = Guid.Parse("01234567-89ab-4cde-8f01-23456789abce");
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        var found = WindowsCodexEngineBindingSource.ObserveAtHomes([first, second], homes,
            path => { seen.Add(path); return path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? held : WriterLockObservation.Missing; },
            () => { snapshots++; return new(engines, new HashSet<uint>()); }, () => now, default);
        string[] expected = [CodexEngineBinding.LockPath(defaultHome, first), CodexEngineBinding.LockPath(home, first),
            CodexEngineBinding.LockPath(defaultHome, second), CodexEngineBinding.LockPath(home, second)];
        check(found.Count == 2 && found.Values.All(value => value.Bound && value.ObservedAt == now) && snapshots == 1 && seen.SequenceEqual(expected),
            "HOME_ACTUAL_SCAN: each thread queries every explicit home while sharing one engine snapshot");
        int emptySnapshots = 0;
        var absent = WindowsCodexEngineBindingSource.ObserveAtHomes([first], homes, _ => WriterLockObservation.Missing,
            () => { emptySnapshots++; return new(engines, new HashSet<uint>()); }, () => now, default);
        check(absent[first] is { Bound: false, Reason: "lock_missing" } && emptySnapshots == 0,
            "HOME_EMPTY_SCAN: an entirely missing lock stays missing and needs no engine snapshot");
        using var cancellation = new CancellationTokenSource();
        int incompleteSnapshots = 0;
        var incomplete = WindowsCodexEngineBindingSource.ObserveAtHomes([first], homes,
            _ => { cancellation.Cancel(); return held; },
            () => { incompleteSnapshots++; return new(engines, new HashSet<uint>()); }, () => now, cancellation.Token);
        check(incomplete.Count == 0 && incompleteSnapshots == 0,
            "HOME_SCAN_BUDGET: cancellation during a home's scan never publishes a partial unique binding");
    }

    private static void ClassifierChecks(Action<bool, string> check)
    {
        var code = new CodexEngine(4100, 1000, CodexEngineHost.Code);
        var desktop = new CodexEngine(4200, 2000, CodexEngineHost.Desktop);
        CodexEngine[] engines = [code, desktop];
        CodexEngineBindingVerdict Held(params WriterLockHolder[] holders) =>
            CodexEngineBinding.Classify(new(WriterLockState.Queried, holders), engines);

        check(Held(new WriterLockHolder(4100, 1000)) == new CodexEngineBindingVerdict(true, "code_engine"),
            "ENGINE_BOUND_CODE: one holder that is a Code-owned engine by PID and start time is bound to it");
        check(Held(new WriterLockHolder(4200, 2000)) == new CodexEngineBindingVerdict(true, "desktop_engine"),
            "ENGINE_BOUND_DESKTOP: one holder that is the desktop app's engine is bound to it");
        check(Held() == new CodexEngineBindingVerdict(false, "no_holder"), "ENGINE_NO_HOLDER: a lock nobody holds is unbound no_holder");
        check(Held(new WriterLockHolder(4100, 1000), new WriterLockHolder(4200, 2000)) == new CodexEngineBindingVerdict(false, "multiple_holders"),
            "ENGINE_MULTIPLE_HOLDERS: two holders, even two recognised engines, are unbound multiple_holders");
        check(Held(new WriterLockHolder(30000, 3000)) == new CodexEngineBindingVerdict(false, "foreign_holder"),
            "ENGINE_FOREIGN_HOLDER: a holder that is no recognised engine (another process, a standalone codex) is unbound foreign_holder");
        check(Held(new WriterLockHolder(4100, 1001)) == new CodexEngineBindingVerdict(false, "start_time_mismatch"),
            "ENGINE_START_TIME_MISMATCH: an engine's PID with another start time (a reused ID) is unbound start_time_mismatch");
        check(CodexEngineBinding.Classify(WriterLockObservation.Reparse, engines) == new CodexEngineBindingVerdict(false, "lock_path_reparse"),
            "ENGINE_LOCK_REPARSE: a reparse point on the lock path is reported, never followed");
        check(CodexEngineBinding.Classify(WriterLockObservation.Missing, engines) == new CodexEngineBindingVerdict(false, "lock_missing"),
            "ENGINE_LOCK_MISSING: a thread with no lock file is unbound lock_missing");
        check(CodexEngineBinding.Classify(WriterLockObservation.Failed, engines) == new CodexEngineBindingVerdict(false, "query_failed"),
            "ENGINE_QUERY_FAILED: a lock query that failed is unbound query_failed");
        check(CodexEngineBinding.Classify(new(WriterLockState.Queried, [new(4100, 1000)]), []) == new CodexEngineBindingVerdict(false, "foreign_holder"),
            "ENGINE_NONE_RUNNING: with no recognised engine running, any holder is foreign");
        // Review of 25a0c65, finding 4: an engine-like process that could not be read makes only a lock IT holds unverifiable.
        var unreadable = new HashSet<uint> { 4300 };
        check(CodexEngineBinding.Classify(new(WriterLockState.Queried, [new(4300, 3000)]), engines, unreadable) == new CodexEngineBindingVerdict(false, "query_failed") &&
            CodexEngineBinding.Classify(new(WriterLockState.Queried, [new(4100, 1000)]), engines, unreadable) == new CodexEngineBindingVerdict(true, "code_engine"),
            "ENGINE_UNREADABLE_HOLDER: a lock held by an unreadable process is query_failed, and every other lock is still classified");
        // Review of d9c2967, item 6: a selection cut at its bound cannot call an unmatched holder foreign.
        check(CodexEngineBinding.Classify(new(WriterLockState.Queried, [new(30000, 3000)]), engines, null, incomplete: true) == new CodexEngineBindingVerdict(false, "query_failed"),
            "ENGINE_INCOMPLETE_UNMATCHED: with an incomplete engine set, an unmatched holder is query_failed, not foreign_holder");
    }

    private static void PathChecks(Action<bool, string> check)
    {
        string home = Path.Combine(Path.GetTempPath(), "agentbroker-synthetic-codex-home");
        Guid thread = Guid.Parse("01234567-89ab-4cde-8f01-23456789abcd");
        string path = CodexEngineBinding.LockPath(home, thread);
        check(path == Path.Combine(home, "thread-writer-locks", "01234567-89ab-4cde-8f01-23456789abcd.lock"),
            "ENGINE_LOCK_PATH: the lock is thread-writer-locks\\<threadId>.lock under the Codex home");
        var seen = new List<string>();
        FileAttributes Regular(string candidate) { seen.Add(candidate); return FileAttributes.Normal; }
        check(CodexEngineBinding.CheckPath(path, Regular) == WriterLockState.Queried && seen.Count >= 3 &&
            seen[0] == Path.GetPathRoot(Path.GetFullPath(path)) && seen.Contains(Path.GetFullPath(home)) && seen.Last() == Path.GetFullPath(path),
            "ENGINE_PATH_EVERY_COMPONENT: every directory from the root down to the lock file, and the file, are checked");
        string linkedDirectory = Path.Combine(home, "thread-writer-locks");
        check(CodexEngineBinding.CheckPath(path, candidate => candidate == Path.GetFullPath(linkedDirectory) ? FileAttributes.Directory | FileAttributes.ReparsePoint
            : FileAttributes.Normal) == WriterLockState.Reparse,
            "ENGINE_PATH_REPARSE_PARENT: a reparse point on a directory above the lock is reported as lock_path_reparse");
        check(CodexEngineBinding.CheckPath(path, candidate => candidate == Path.GetFullPath(path) ? FileAttributes.ReparsePoint : FileAttributes.Normal) == WriterLockState.Reparse,
            "ENGINE_PATH_REPARSE_FILE: a lock file that is itself a reparse point is reported, not followed");
        check(CodexEngineBinding.CheckPath(path, _ => throw new FileNotFoundException("synthetic")) == WriterLockState.Missing &&
            CodexEngineBinding.CheckPath(path, _ => throw new DirectoryNotFoundException("synthetic")) == WriterLockState.Missing,
            "ENGINE_PATH_MISSING: a missing lock file or directory is lock_missing");
        check(CodexEngineBinding.CheckPath(path, _ => throw new UnauthorizedAccessException("synthetic")) == WriterLockState.Failed,
            "ENGINE_PATH_UNREADABLE: attributes that cannot be read are query_failed, never Queried");
        // Review of 25a0c65, finding 6: a missing lock under a junctioned Codex home is the junction's finding, not lock_missing.
        string linkedHome = Path.GetFullPath(home);
        check(CodexEngineBinding.CheckPath(path, candidate => candidate == linkedHome ? FileAttributes.Directory | FileAttributes.ReparsePoint
            : candidate == Path.GetFullPath(path) ? throw new FileNotFoundException("synthetic") : FileAttributes.Directory) == WriterLockState.Reparse,
            "ENGINE_PATH_REPARSE_BEFORE_MISSING: a reparse point above a missing lock is reported as lock_path_reparse");
    }

    // The engines a holder may be, from one synthetic snapshot: PIDs 10..14 are codex.exe under different parents.
    private static void EngineSelectionChecks(Action<bool, string> check)
    {
        string desktopApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps",
            "OpenAI.Codex_26.1.2.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");
        var processes = new Dictionary<uint, (uint Parent, string Name)>
        {
            [10] = (100, "codex.exe"), [11] = (101, "codex.exe"), [12] = (102, "codex.exe"), [13] = (103, "codex.exe"), [14] = (104, "codex.exe"),
            [15] = (100, "codex.exe"),
            [100] = (1, "Code.exe"), [101] = (1, "ChatGPT.exe"), [102] = (1, "ChatGPT.exe"), [103] = (1, "Code.exe"), [104] = (1, "pwsh.exe"),
        };
        (string Image, ulong Created, bool SameUser) Metadata(uint pid) => pid switch
        {
            10 => (@"C:\Synthetic\codex.exe", 500, true), 100 => (@"C:\Synthetic\Code.exe", 400, true),
            11 => (@"C:\Synthetic\codex.exe", 600, true), 101 => (desktopApp, 300, true),
            // A ChatGPT.exe outside the Codex package is not the desktop app.
            12 => (@"C:\Synthetic\codex.exe", 700, true), 102 => (@"C:\Synthetic\Elsewhere\ChatGPT.exe", 300, true),
            // A Code.exe parent created after its engine is a reused parent ID.
            13 => (@"C:\Synthetic\codex.exe", 200, true), 103 => (@"C:\Synthetic\Code.exe", 900, true),
            // An engine that exited between the snapshot and the read (or another user's, or an elevated VS Code's).
            15 => throw new DesktopAdapterException("vscode_process_identity_unverified"),
            _ => (@"C:\Synthetic\other.exe", 100, true),
        };
        var set = WindowsClientProcesses.SelectCodexEngines(processes, Metadata);
        var engines = set.Engines;
        check(set.Unreadable.SetEquals([15u]) && engines.Any(engine => engine.Pid == 10),
            "ENGINE_SELECT_UNREADABLE_ALONE: an engine-like process that cannot be read is set aside alone; the readable engines are still recognised");
        check(engines.Contains(new CodexEngine(10, 500, CodexEngineHost.Code)),
            "ENGINE_SELECT_CODE: a codex.exe under a running Code.exe is a Code engine, with its PID carried");
        check(engines.Contains(new CodexEngine(11, 600, CodexEngineHost.Desktop)),
            "ENGINE_SELECT_DESKTOP: a codex.exe under the Codex package's ChatGPT.exe is the desktop engine");
        check(!engines.Any(engine => engine.Pid == 12), "ENGINE_SELECT_DESKTOP_PACKAGE: a ChatGPT.exe outside the Codex package owns no engine");
        check(!engines.Any(engine => engine.Pid == 13), "ENGINE_SELECT_REUSED_PARENT: a parent created after its engine owns no engine");
        check(!engines.Any(engine => engine.Pid == 14) && engines.Count == 2, "ENGINE_SELECT_OTHER_PARENT: a codex.exe under any other parent is not an engine");
        check(!set.Incomplete, "ENGINE_SELECT_COMPLETE: a selection inside its bound is complete");
        var crowd = new Dictionary<uint, (uint Parent, string Name)> { [100] = (1, "Code.exe") };
        for (uint pid = 1000; pid < 1000 + WindowsClientProcesses.MaximumEngineCandidates + 20; pid++) crowd[pid] = (100, "codex.exe");
        var capped = WindowsClientProcesses.SelectCodexEngines(crowd, pid => pid == 100 ? (@"C:\Synthetic\Code.exe", 400UL, true) : (@"C:\Synthetic\codex.exe", 500UL, true));
        check(capped.Incomplete && capped.Engines.Count == WindowsClientProcesses.MaximumEngineCandidates,
            "ENGINE_SELECT_INCOMPLETE: a selection that stops at its bound says it is incomplete");
        var otherUser = WindowsClientProcesses.SelectCodexEngines(processes, pid => Metadata(pid) with { SameUser = pid != 10 }).Engines;
        check(!otherUser.Any(engine => engine.Pid == 10), "ENGINE_SELECT_SAME_USER: an engine of another user is not recognised");
        // The profile check's own selection stays strict (an unreadable engine refuses it), so it reads the set without 15.
        var readable = processes.Where(pair => pair.Key != 15).ToDictionary(pair => pair.Key, pair => pair.Value);
        check(WindowsClientProcesses.SelectCodeOwnedEngines(readable, Metadata).Single(engine => engine.Image == @"C:\Synthetic\codex.exe" && engine.CreationFileTime == 500).Pid == 10,
            "ENGINE_CODE_OWNED_PID: CodeOwnedEngine now carries the engine's PID");
    }

    // Read-only live observation for the lane's evidence (never part of a gate): every GUID-named lock in the default Codex
    // home, queried once each with Restart Manager. Prints thread, verdict and the raw holders; reads nothing else.
    internal static int Live()
    {
        string locks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "thread-writer-locks");
        var threads = Directory.Exists(locks)
            ? Directory.EnumerateFiles(locks, "*.lock").Select(Path.GetFileNameWithoutExtension)
                .Select(name => Guid.TryParseExact(name, "D", out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToArray()
            : [];
        Console.WriteLine("locks=" + threads.Length);
        var engines = WindowsClientProcesses.ReadCodexEngines();
        foreach (var engine in engines.Engines) Console.WriteLine("engine pid=" + engine.Pid + " host=" + engine.Host);
        Console.WriteLine("unreadable=" + engines.Unreadable.Count);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var verdicts = WindowsCodexEngineBindingSource.Instance.Observe(threads, CancellationToken.None);
        Console.WriteLine("observe ms=" + timer.ElapsedMilliseconds);
        foreach (var thread in threads)
        {
            var raw = WindowsCodexEngineBindingSource.Query(CodexEngineBinding.LockPath(Path.GetDirectoryName(locks)!, thread));
            Console.WriteLine(thread.ToString("D") + " bound=" + verdicts[thread].Bound + " reason=" + verdicts[thread].Reason +
                " state=" + raw.State + " holders=" + string.Join(",", raw.Holders.Select(holder => holder.Pid)));
        }
        return 0;
    }
}
