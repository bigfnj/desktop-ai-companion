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
            seen[0] == Path.GetFullPath(path) && seen.Contains(Path.GetFullPath(home)) && seen.Last() == Path.GetPathRoot(Path.GetFullPath(path)),
            "ENGINE_PATH_EVERY_COMPONENT: the lock file and every directory above it to the root are checked");
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
    }

    // The engines a holder may be, from one synthetic snapshot: PIDs 10..14 are codex.exe under different parents.
    private static void EngineSelectionChecks(Action<bool, string> check)
    {
        string desktopApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps",
            "OpenAI.Codex_26.1.2.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");
        var processes = new Dictionary<uint, (uint Parent, string Name)>
        {
            [10] = (100, "codex.exe"), [11] = (101, "codex.exe"), [12] = (102, "codex.exe"), [13] = (103, "codex.exe"), [14] = (104, "codex.exe"),
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
            _ => (@"C:\Synthetic\other.exe", 100, true),
        };
        var engines = WindowsClientProcesses.SelectCodexEngines(processes, Metadata);
        check(engines.Contains(new CodexEngine(10, 500, CodexEngineHost.Code)),
            "ENGINE_SELECT_CODE: a codex.exe under a running Code.exe is a Code engine, with its PID carried");
        check(engines.Contains(new CodexEngine(11, 600, CodexEngineHost.Desktop)),
            "ENGINE_SELECT_DESKTOP: a codex.exe under the Codex package's ChatGPT.exe is the desktop engine");
        check(!engines.Any(engine => engine.Pid == 12), "ENGINE_SELECT_DESKTOP_PACKAGE: a ChatGPT.exe outside the Codex package owns no engine");
        check(!engines.Any(engine => engine.Pid == 13), "ENGINE_SELECT_REUSED_PARENT: a parent created after its engine owns no engine");
        check(!engines.Any(engine => engine.Pid == 14) && engines.Count == 2, "ENGINE_SELECT_OTHER_PARENT: a codex.exe under any other parent is not an engine");
        var otherUser = WindowsClientProcesses.SelectCodexEngines(processes, pid => Metadata(pid) with { SameUser = pid != 10 });
        check(!otherUser.Any(engine => engine.Pid == 10), "ENGINE_SELECT_SAME_USER: an engine of another user is not recognised");
        check(WindowsClientProcesses.SelectCodeOwnedEngines(processes, Metadata).Single(engine => engine.Image == @"C:\Synthetic\codex.exe" && engine.CreationFileTime == 500).Pid == 10,
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
        foreach (var engine in engines) Console.WriteLine("engine pid=" + engine.Pid + " host=" + engine.Host);
        var verdicts = WindowsCodexEngineBindingSource.Instance.Observe(threads);
        foreach (var thread in threads)
        {
            var raw = WindowsCodexEngineBindingSource.Query(CodexEngineBinding.LockPath(Path.GetDirectoryName(locks)!, thread));
            Console.WriteLine(thread.ToString("D") + " bound=" + verdicts[thread].Bound + " reason=" + verdicts[thread].Reason +
                " state=" + raw.State + " holders=" + string.Join(",", raw.Holders.Select(holder => holder.Pid)));
        }
        return 0;
    }
}
