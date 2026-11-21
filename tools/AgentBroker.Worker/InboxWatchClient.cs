using AgentBroker.Core;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentBroker.Worker;

/// <summary>Opt-in metadata-only inbox notification for a supported background-task host. Never acknowledges mail.</summary>
public static class InboxWatchClient
{
    public const string Command = "--watch-inbox";
    internal const int PageLimit = 16, PageBudget = 64, FailureBudget = 4;
    internal const long MaximumSeconds = 31_536_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly Regex CursorPattern = new(@"\A[A-Za-z0-9+/=]{1,2048}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ToolCode = new(@"\A(?:An error occurred invoking 'read_inbox_page': )?([a-z_]{1,64})\z", RegexOptions.CultureInvariant);

    public sealed record Options(string DataDirectory, SessionAddress Session, string ArmRoot, long MaxSeconds,
        string? Cursor, IReadOnlySet<string> SeenIds, int IntervalSeconds = 15, int ParentPid = 0,
        long ParentStartFileTime = 0, bool Unbound = false)
    {
        public static Options Parse(string[] args)
        {
            if (args.Length == 0 || args[0] != Command) throw new ArgumentException("invalid_arguments");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool unbound = false;
            for (int index = 1; index < args.Length; index++)
            {
                if (args[index] == "--unbound" && !unbound) { unbound = true; continue; }
                if (args[index] is not ("--data" or "--broker-id" or "--session-id" or "--incarnation" or "--arm-root" or
                    "--max-seconds" or "--cursor" or "--seen-ids" or "--interval-seconds" or "--parent-pid" or "--parent-start") ||
                    index + 1 >= args.Length || !values.TryAdd(args[index], args[++index])) throw new ArgumentException("invalid_arguments");
            }
            string Required(string key) => values.TryGetValue(key, out string? value) ? value : throw new ArgumentException("invalid_arguments");
            string data = Required("--data"), root = Required("--arm-root"), broker = Required("--broker-id"),
                session = Required("--session-id"), incarnation = Required("--incarnation");
            if (!CanonicalPath(data) || !CanonicalPath(root) || !CanonicalGuid(broker) || !CanonicalGuid(incarnation) ||
                !session.StartsWith("claude:", StringComparison.Ordinal) || !CanonicalGuid(session[7..]) ||
                !Positive(Required("--max-seconds"), MaximumSeconds, out long maximum)) throw new ArgumentException("invalid_arguments");
            string? cursor = values.GetValueOrDefault("--cursor");
            if (cursor is not null && !CursorPattern.IsMatch(cursor)) throw new ArgumentException("invalid_arguments");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string supplied = values.GetValueOrDefault("--seen-ids", "");
            if (supplied.Length > 0)
            {
                string[] ids = supplied.Split(',');
                if (ids.Length > 64 || ids.Any(id => !CanonicalGuid(id))) throw new ArgumentException("invalid_arguments");
                seen.UnionWith(ids);
            }
            if (!Positive(values.GetValueOrDefault("--interval-seconds", "15"), 300, out long interval)) throw new ArgumentException("invalid_arguments");
            bool hasPid = values.ContainsKey("--parent-pid"), hasStart = values.ContainsKey("--parent-start");
            if (hasPid != hasStart || hasPid == unbound) throw new ArgumentException("invalid_arguments");
            long pid = 0, start = 0;
            if (hasPid && (!Positive(values["--parent-pid"], int.MaxValue, out pid) ||
                !Positive(values["--parent-start"], long.MaxValue, out start))) throw new ArgumentException("invalid_arguments");
            return new(data, new(Guid.Parse(broker), session, Guid.Parse(incarnation)), root, maximum,
                cursor, seen, (int)interval, (int)pid, start, unbound);
        }
    }

    internal sealed record Reply(int ExitCode, JsonElement Envelope);
    internal delegate Task<Reply> Call(string tool, JsonElement arguments, CancellationToken cancellation);
    private sealed record Entry(long Sequence, string Id);
    private sealed record Page(Entry[] Entries, string Cursor, bool More, long Pending);
    private sealed record Scan(bool Ok, string? Terminal, string? Failure, List<string> Ids, long Pending, string? FinalCursor);
    internal sealed record Outcome(int ExitCode, string Reason, string? Source = null, IReadOnlyList<string>? Ids = null,
        long? Pending = null, int Failures = 0, string? Last = null, int Unreported = 0);

    internal static bool CanonicalGuid(string value) => Guid.TryParseExact(value, "D", out Guid parsed) &&
        parsed != Guid.Empty && parsed.ToString("D") == value;

    private static bool Positive(string value, long maximum, out long parsed) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0 && parsed <= maximum;

    private static bool CanonicalPath(string value)
    {
        try
        {
            return value.Length is > 3 and <= 1024 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\' &&
                !Path.EndsInDirectorySeparator(value) && value.IndexOfAny(['*', '?', '"', '<', '>', '|', '/']) < 0 &&
                value.IndexOf(':', 2) < 0 && Path.GetFullPath(value) == value;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    public static async Task<int> RunCommandAsync(string[] args, Stream output, CancellationToken cancellation = default)
    {
        Options options;
        try { options = Options.Parse(args); }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { return await EmitAsync(new(2, "invalid_arguments"), output, "unbound", "ok", TimeProvider.System); }
        string previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            // This opt-in command can live for hours. Release the caller's working directory rather than pinning its repo.
            Directory.SetCurrentDirectory(options.ArmRoot);
        }
        catch (Exception) { return await EmitAsync(new(2, "arm_directory"), output, options.Unbound ? "unbound" : "bound", "ok", TimeProvider.System); }
        try
        {
            return await RunAsync(options, output, CreateCall(options.DataDirectory), TimeProvider.System, cancellation);
        }
        finally
        {
            // The caller may have renamed/deleted its old directory while the separate CLI ran. It is not needed again.
            try { Directory.SetCurrentDirectory(previousDirectory); } catch (Exception) { }
        }
    }

    // Shared authenticated adapter, also used when the typed engine runs in process; no working-directory change here.
    internal static Call CreateCall(string directory) => async (tool, arguments, token) =>
    {
        using var result = new MemoryStream();
        int code = await McpCallClient.RunToolAsync(directory, tool, arguments, result, token);
        using var document = JsonDocument.Parse(result.ToArray());
        return new(code, document.RootElement.Clone());
    };

    internal static string ArmKey(Options options) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        options.DataDirectory.ToUpperInvariant() + "/" + options.Session.BrokerId.ToString("D") + "/" + options.Session.SessionId + "/" +
        options.Session.Incarnation.ToString("D")))).ToLowerInvariant();

    private static bool PlainChain(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static bool PlainLock(string path)
    {
        try { return (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0; }
        catch (FileNotFoundException) { return true; }
    }

    private static bool ParentAlive(Options options) => options.Unbound || WorkerLifetime.ParentIsAlive(options.ParentPid, options.ParentStartFileTime);

    internal static async Task<int> RunAsync(Options options, Stream output, Call call, TimeProvider clock,
        CancellationToken cancellation = default)
    {
        string parentMode = options.Unbound ? "unbound" : "bound", cleanup = "ok";
        FileStream? arm = null;
        string? lockPath = null;
        Process? parent = null;
        using var monitorStop = new CancellationTokenSource();
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task monitor = Task.CompletedTask;
        Outcome outcome;
        try
        {
            if (!ParentAlive(options)) outcome = new(2, "parent_identity");
            else
            {
                if (!options.Unbound)
                {
                    parent = Process.GetProcessById(options.ParentPid);
                    _ = parent.SafeHandle;
                    if (parent.HasExited || parent.StartTime.ToUniversalTime().ToFileTimeUtc() != options.ParentStartFileTime)
                        throw new ArgumentException("parent_identity");
                    monitor = MonitorParentAsync(parent, stopped, monitorStop.Token);
                }
                outcome = new(2, "arm_directory");
                if (Directory.Exists(options.DataDirectory) && Directory.Exists(options.ArmRoot) && PlainChain(options.ArmRoot))
                {
                    lockPath = Path.Combine(options.ArmRoot, ArmKey(options) + ".lock");
                    if (PlainLock(lockPath))
                    {
                        try { arm = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                        { return await EmitAsync(new(25, "already_armed"), output, parentMode, cleanup, clock); }
                        outcome = await WatchAsync(options, call, clock, stopped.Token);
                    }
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { outcome = new(arm is null ? 2 : 26, arm is null && !options.Unbound ? "parent_identity" : "internal_error"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { outcome = new(arm is null ? 2 : 26, arm is null ? "arm_directory" : "internal_error"); }
        catch (Exception) { outcome = new(26, "internal_error"); }
        finally
        {
            monitorStop.Cancel();
            await monitor;
            parent?.Dispose();
            if (arm is not null)
            {
                try { arm.Dispose(); if (File.Exists(lockPath)) cleanup = "failed"; }
                catch (Exception) { cleanup = "failed"; }
            }
        }
        return await EmitAsync(outcome, output, parentMode, cleanup, clock);
    }

    private static async Task MonitorParentAsync(Process parent, CancellationTokenSource stopped, CancellationToken token)
    {
        try { await parent.WaitForExitAsync(token); stopped.Cancel(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { stopped.Cancel(); }
    }

    private static async Task<Outcome> WatchAsync(Options options, Call call, TimeProvider clock, CancellationToken stopping)
    {
        long began = clock.GetTimestamp();
        TimeSpan maximum = TimeSpan.FromSeconds(options.MaxSeconds);
        TimeSpan Remaining() => maximum - clock.GetElapsedTime(began);
        string? StopReason() => !ParentAlive(options) ? "parent_exited" : Remaining() <= TimeSpan.Zero ? "lifetime" : stopping.IsCancellationRequested ? "cancelled" : null;
        string? baseCursor = options.Cursor;
        string phase = baseCursor is null ? "baseline" : "arrival";
        int failures = 0;
        string? last = null;
        var unreported = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            Scan scan = await ScanAsync(options, baseCursor, call, clock, stopping, Remaining, StopReason);
            if (!scan.Ok) unreported.UnionWith(scan.Ids);
            if (scan.Terminal is { } terminal)
                return new(TerminalCode(terminal), terminal, Failures: failures, Last: last, Unreported: unreported.Count);
            if (scan.Ok)
            {
                failures = 0; last = null; unreported.Clear();
                if (scan.Ids.Count > 0) return new(0, "mail", phase, scan.Ids, scan.Pending);
                if (phase == "baseline") { baseCursor = scan.FinalCursor; phase = "arrival"; }
            }
            else
            {
                last = scan.Failure;
                if (++failures >= FailureBudget) return new(22, "failure_budget", Failures: failures, Last: last, Unreported: unreported.Count);
            }
            if (StopReason() is { } before) return new(TerminalCode(before), before, Failures: failures, Last: last, Unreported: unreported.Count);
            TimeSpan remainingToSleep = Remaining();
            if (remainingToSleep <= TimeSpan.Zero) return new(20, "lifetime", Failures: failures, Last: last, Unreported: unreported.Count);
            TimeSpan delay = remainingToSleep < TimeSpan.FromSeconds(options.IntervalSeconds) ? remainingToSleep : TimeSpan.FromSeconds(options.IntervalSeconds);
            try { await Task.Delay(delay, clock, stopping); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            if (StopReason() is { } after) return new(TerminalCode(after), after, Failures: failures, Last: last, Unreported: unreported.Count);
        }
    }

    private static async Task<Scan> ScanAsync(Options options, string? start, Call call, TimeProvider clock,
        CancellationToken stopping, Func<TimeSpan> remaining, Func<string?> stopReason)
    {
        string? cursor = start;
        var ids = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        long previous = 0;
        for (int pageIndex = 0; pageIndex < PageBudget; pageIndex++)
        {
            if (stopReason() is { } stop) return new(false, stop, null, ids, 0, null);
            Reply reply;
            try
            {
                TimeSpan budget = remaining() < TimeSpan.FromSeconds(40) ? remaining() : TimeSpan.FromSeconds(40);
                if (budget <= TimeSpan.Zero) return new(false, "lifetime", null, ids, 0, null);
                using var deadline = new CancellationTokenSource(budget, clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, deadline.Token);
                reply = await call("read_inbox_page", JsonSerializer.SerializeToElement(new { session = options.Session, limit = PageLimit, cursor }, Json), linked.Token);
            }
            catch (OperationCanceledException)
            { return new(false, stopReason(), "transport_error", ids, 0, null); }
            catch (Exception) { return new(false, null, "internal", ids, 0, null); }
            if (stopReason() is { } ended) return new(false, ended, null, ids, 0, null);
            if (reply.ExitCode == 10)
            {
                bool valid = ExactString(reply.Envelope, "status", "tool_error") && ExactString(reply.Envelope, "dispatchState", "confirmed");
                return new(false, valid ? ToolReason(reply.Envelope) : null, valid ? null : "malformed_tool_error", ids, 0, null);
            }
            if (reply.ExitCode != 0)
                return new(false, null, reply.ExitCode switch { 2 => "invalid_request", 11 => "rpc_error", 12 => "http_error", 69 => "transport_error", 74 => "output_error", _ => "unexpected_exit" }, ids, 0, null);
            if (!ExactString(reply.Envelope, "status", "ok")) return new(false, null, "status_not_ok", ids, 0, null);
            if (!ExactString(reply.Envelope, "dispatchState", "confirmed")) return new(false, null, "dispatch_unconfirmed", ids, 0, null);
            if (!Property(reply.Envelope, "result", out var result) || !TryPage(result, out Page? page))
                return new(false, null, "malformed_page", ids, 0, null);
            foreach (Entry entry in page!.Entries)
            {
                if (entry.Sequence <= previous) return new(false, null, "malformed_page", ids, 0, null);
                previous = entry.Sequence;
            }
            if (page.More && page.Cursor == cursor) return new(false, null, "malformed_page", ids, 0, null);
            foreach (Entry entry in page.Entries)
                if (unique.Add(entry.Id) && !options.SeenIds.Contains(entry.Id)) ids.Add(entry.Id);
            if (!page.More) return new(true, null, null, ids, page.Pending, page.Cursor);
            cursor = page.Cursor;
        }
        return new(false, null, "page_budget", ids, 0, null);
    }

    private static bool Property(JsonElement value, string name, out JsonElement result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.Object) return false;
        bool found = false;
        foreach (var property in value.EnumerateObject())
            if (property.Name == name) { if (found) return false; found = true; result = property.Value; }
        return found;
    }

    private static bool ExactString(JsonElement value, string name, string expected) =>
        Property(value, name, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() == expected;

    private static bool TryPage(JsonElement value, out Page? page)
    {
        page = null;
        if (!Property(value, "entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > PageLimit ||
            !Property(value, "nextCursor", out var next) || next.ValueKind != JsonValueKind.String || !CursorPattern.IsMatch(next.GetString()!) ||
            !Property(value, "hasMore", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            more.GetBoolean() && entries.GetArrayLength() != PageLimit ||
            !Property(value, "pendingCount", out var pending) || pending.ValueKind != JsonValueKind.Number || !pending.TryGetInt64(out long count) || count < entries.GetArrayLength()) return false;
        var checkedEntries = new List<Entry>();
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (!Property(entry, "arrivalSequence", out var sequence) || sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out long serial) || serial < 1 ||
                !Property(entry, "receipt", out var receipt) || !Property(receipt, "message", out var message) ||
                !Property(message, "id", out var id) || id.ValueKind != JsonValueKind.String || !CanonicalGuid(id.GetString()!) ||
                !Property(receipt, "status", out var status) || status.ValueKind != JsonValueKind.String ||
                status.GetString() is not ("Accepted" or "Submitting" or "Submitted" or "Uncertain")) return false;
            checkedEntries.Add(new(serial, id.GetString()!));
        }
        page = new(checkedEntries.ToArray(), next.GetString()!, more.GetBoolean(), count);
        return true;
    }

    private static string ToolReason(JsonElement envelope)
    {
        string code = Property(envelope, "result", out var result) && result.ValueKind == JsonValueKind.String &&
            ToolCode.Match(result.GetString()!) is { Success: true } match ? match.Groups[1].Value : "";
        return code switch { "inbox_cursor_reset_required" => "cursor_reset", "session_unavailable" => "session_unavailable", "invalid_inbox_cursor" => "invalid_inbox_cursor", _ => "tool_error" };
    }

    private static int TerminalCode(string reason) => reason switch { "lifetime" or "cancelled" => 20, "parent_exited" => 23, "cursor_reset" => 24, _ => 21 };

    private static async Task<int> EmitAsync(Outcome outcome, Stream output, string parent, string cleanup, TimeProvider clock)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = outcome.ExitCode == 0 ? "mail" : "ended", reason = outcome.Reason, exitCode = outcome.ExitCode,
            source = outcome.Source, newCount = outcome.Ids?.Count, more = outcome.Ids?.Count > PageLimit,
            pendingCount = outcome.Pending, ids = outcome.Ids?.Take(PageLimit).ToArray(),
            last = outcome.Last, failures = outcome.Failures, unreported = outcome.Unreported,
            at = clock.GetUtcNow(), parent, cleanup
        }, Json);
        try { await output.WriteAsync(bytes); await output.WriteAsync("\n"u8.ToArray()); await output.FlushAsync(); }
        catch (Exception) { return 26; }
        return outcome.ExitCode;
    }
}
