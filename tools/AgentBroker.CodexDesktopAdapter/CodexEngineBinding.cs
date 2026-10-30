using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentBroker.CodexDesktopAdapter;

// REPORT ONLY (owner's decision 2026-10-30, N-agentbroker-code-owner-provenance-01). Which process holds a Codex thread's
// writer lock, %USERPROFILE%\.codex\thread-writer-locks\<threadId>.lock, and whether it is an engine the broker recognises.
// The 2026-10-29 read-only audit found nothing a view, the router or a discovery answer carries that ties a router client to
// a process; the engine's writer lock is the one identity that ties a thread to an engine. The owner chose to REPORT the
// binding (Status and receipts say bound or unbound) and not to ENFORCE it: nothing here refuses, defers or routes a
// delivery, and DesktopOwnerBinding.NativeSessionIncarnation and every address stay exactly as they were (changing the
// address strands queued messages, DesktopProtocol.cs). The ENFORCE design (exactly one matching holder required before
// and after thread-follower-start-turn, the incarnation derived from the holder) is recorded, not built. The view hop
// (which webview of a window answered) stays a vendor limit: see docs/DESIGN-REGISTER.md, feature/broker-163.

/// <summary>The kind of engine that can hold a thread's writer lock: a codex.exe whose parent is a running Code.exe (the
/// engines the VS Code profile check reads), or one whose parent is the Codex desktop package's ChatGPT.exe.</summary>
public enum CodexEngineHost { Code, Desktop }

/// <summary>A running engine, identified by process ID AND creation time, so a reused ID never matches.</summary>
public sealed record CodexEngine(uint Pid, ulong CreationFileTime, CodexEngineHost Host);

/// <summary>One process Restart Manager names as using a lock file, with its start time as Restart Manager reports it.</summary>
public sealed record WriterLockHolder(uint Pid, ulong StartFileTime);

public enum WriterLockState { Queried, Missing, Reparse, Failed }

/// <summary>What one read-only query of a thread's lock file found.</summary>
public sealed record WriterLockObservation(WriterLockState State, IReadOnlyList<WriterLockHolder> Holders)
{
    public static readonly WriterLockObservation Missing = new(WriterLockState.Missing, []);
    public static readonly WriterLockObservation Reparse = new(WriterLockState.Reparse, []);
    public static readonly WriterLockObservation Failed = new(WriterLockState.Failed, []);
}

/// <summary>Bound, and why: code_engine or desktop_engine when bound; otherwise the reason it is not.</summary>
public sealed record CodexEngineBindingVerdict(bool Bound, string Reason);

/// <summary>The broker's source of engine-binding observations, one verdict per thread. Never throws: a failure is a
/// verdict (query_failed), so an observation can never fail a discovery pass or a delivery.</summary>
public interface ICodexEngineBindingSource
{
    IReadOnlyDictionary<Guid, CodexEngineBindingVerdict> Observe(IReadOnlyCollection<Guid> threadIds);
}

public static class CodexEngineBinding
{
    public const string CodeEngine = "code_engine";
    public const string DesktopEngine = "desktop_engine";

    /// <summary>Pure: the verdict for one lock observation against the engines running now. Bound only for exactly one holder
    /// whose process ID AND start time are a recognised engine's.</summary>
    public static CodexEngineBindingVerdict Classify(WriterLockObservation observation, IReadOnlyList<CodexEngine> engines)
    {
        switch (observation.State)
        {
            case WriterLockState.Reparse: return new(false, "lock_path_reparse");
            case WriterLockState.Missing: return new(false, "lock_missing");
            case WriterLockState.Failed: return new(false, "query_failed");
        }
        if (observation.Holders.Count == 0) return new(false, "no_holder");
        if (observation.Holders.Count > 1) return new(false, "multiple_holders");
        var holder = observation.Holders[0];
        var engine = engines.FirstOrDefault(candidate => candidate.Pid == holder.Pid);
        if (engine is null) return new(false, "foreign_holder");
        if (engine.CreationFileTime != holder.StartFileTime) return new(false, "start_time_mismatch");
        return new(true, engine.Host == CodexEngineHost.Code ? CodeEngine : DesktopEngine);
    }

    /// <summary>The lock file's path under a Codex home. Codex's own default home only: the worker cannot see what CODEX_HOME
    /// an engine was started with, so a guess from the worker's environment could name another home's lock.</summary>
    public static string LockPath(string codexHome, Guid threadId)
        => Path.Combine(codexHome, "thread-writer-locks", threadId.ToString("D") + ".lock");

    /// <summary>The lock path's state before any query: Reparse if the file or any directory above it is a reparse point
    /// (reported, never followed), Missing if it does not exist, Failed if its attributes cannot be read, else Queried.</summary>
    public static WriterLockState CheckPath(string lockPath, Func<string, FileAttributes> attributes)
    {
        try
        {
            for (string? path = Path.GetFullPath(lockPath); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            {
                if ((attributes(path) & FileAttributes.ReparsePoint) != 0) return WriterLockState.Reparse;
                if (Path.GetPathRoot(path) == path) break;
            }
            return WriterLockState.Queried;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return WriterLockState.Missing; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return WriterLockState.Failed; }
    }
}

/// <summary>
/// The production source: one process snapshot per call (only when some lock has exactly one holder), one Restart Manager
/// session per thread (RmStartSession, RmRegisterResources, RmGetList, RmEndSession). Read-only: the lock file is never
/// opened, for write or otherwise, by this code, a stale lock file is never deleted, and a reparse point anywhere on the
/// path is reported instead of followed.
/// </summary>
public sealed class WindowsCodexEngineBindingSource : ICodexEngineBindingSource
{
    public static readonly WindowsCodexEngineBindingSource Instance = new();
    // A lock held by more processes than this is reported as multiple_holders without reading them all.
    private const int MaximumHolders = 16;

    public IReadOnlyDictionary<Guid, CodexEngineBindingVerdict> Observe(IReadOnlyCollection<Guid> threadIds)
    {
        var result = new Dictionary<Guid, CodexEngineBindingVerdict>();
        if (threadIds.Count == 0) return result;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(profile))
        {
            foreach (var thread in threadIds) result[thread] = new(false, "query_failed");
            return result;
        }
        string home = Path.Combine(profile, ".codex");
        IReadOnlyList<CodexEngine>? engines = null;
        bool enginesUnreadable = false;
        foreach (var thread in threadIds.Distinct())
        {
            var observation = Query(CodexEngineBinding.LockPath(home, thread));
            // The engines are read once, and only when a holder needs matching.
            if (observation is { State: WriterLockState.Queried, Holders.Count: 1 } && engines is null && !enginesUnreadable)
            {
                try { engines = WindowsClientProcesses.ReadCodexEngines(); }
                catch (Exception error) when (error is DesktopAdapterException or Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
                { enginesUnreadable = true; }
            }
            result[thread] = observation is { State: WriterLockState.Queried, Holders.Count: 1 } && enginesUnreadable
                ? new(false, "query_failed") : CodexEngineBinding.Classify(observation, engines ?? []);
        }
        return result;
    }

    /// <summary>One read-only Restart Manager query of one lock file. Internal so the lane's live check can call it.</summary>
    internal static WriterLockObservation Query(string lockPath)
    {
        var state = CodexEngineBinding.CheckPath(lockPath, File.GetAttributes);
        if (state != WriterLockState.Queried) return new(state, []);
        var key = new StringBuilder(Native.SessionKeyLength + 1);
        if (Native.RmStartSession(out uint session, 0, key) != 0) return WriterLockObservation.Failed;
        try
        {
            if (Native.RmRegisterResources(session, 1, [lockPath], 0, null, 0, null) != 0) return WriterLockObservation.Failed;
            uint count = MaximumHolders;
            var holders = new Native.ProcessInfo[MaximumHolders];
            int status = Native.RmGetList(session, out uint needed, ref count, holders, out _);
            if (status == Native.ErrorMoreData)
                return new(WriterLockState.Queried, Enumerable.Repeat(new WriterLockHolder(0, 0), MaximumHolders + 1).ToArray());
            if (status != 0 || count > MaximumHolders) return WriterLockObservation.Failed;
            return new(WriterLockState.Queried, holders.Take((int)count)
                .Select(holder => new WriterLockHolder(holder.Process.ProcessId,
                    ((ulong)holder.Process.StartHigh << 32) | holder.Process.StartLow)).ToArray());
        }
        finally { Native.RmEndSession(session); }
    }

    private static class Native
    {
        internal const int SessionKeyLength = 32; // CCH_RM_SESSION_KEY: sizeof(GUID) * 2
        internal const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential)]
        internal struct UniqueProcess
        {
            internal uint ProcessId;
            internal uint StartLow, StartHigh; // FILETIME ProcessStartTime
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessInfo
        {
            internal UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string AppName;        // CCH_RM_MAX_APP_NAME + 1
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string ServiceShortName; // CCH_RM_MAX_SVC_NAME + 1
            internal int ApplicationType;
            internal uint AppStatus;
            internal uint TerminalSessionId;
            [MarshalAs(UnmanagedType.Bool)] internal bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmStartSession(out uint session, int flags, StringBuilder sessionKey);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint applications,
            UniqueProcess[]? processes, uint services, string[]? serviceNames);
        [DllImport("rstrtmgr.dll")]
        internal static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? holders, out uint rebootReasons);
        [DllImport("rstrtmgr.dll")]
        internal static extern int RmEndSession(uint session);
    }
}
