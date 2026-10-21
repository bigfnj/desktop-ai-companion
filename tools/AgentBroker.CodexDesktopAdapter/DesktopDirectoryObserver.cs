using System.Text.Json.Nodes;

namespace AgentBroker.CodexDesktopAdapter;

/// <summary>
/// Long-lived verified metadata observer. Never follows history, submits input, or starts/resumes a thread.
/// Its connection is cryptographically pinned once and process incarnation is checked before every snapshot/write.
/// </summary>
public sealed class DesktopDirectoryObserver : IAsyncDisposable
{
    private readonly DesktopAdapter owner;
    private readonly IDesktopConnection connection;
    private readonly Guid clientId;
    private readonly DesktopDirectoryIndex index;
    private readonly object indexGate = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task readerTask, heartbeatTask;
    private int disposed, closed;

    internal DesktopDirectoryObserver(DesktopAdapter owner, IDesktopConnection connection, Guid clientId,
        DesktopDirectoryIndex index, TimeSpan heartbeatInterval)
    {
        this.owner = owner; this.connection = connection; this.clientId = clientId; this.index = index;
        readerTask = ReadAsync(lifetime.Token);
        heartbeatTask = RefreshMetadataAsync(heartbeatInterval, lifetime.Token);
    }

    public static async Task<DesktopDirectoryObserver> ConnectAsync(CancellationToken token = default)
    {
        var adapter = await DesktopAdapter.ConnectAsync(token: token);
        return await WarmUpAsync(adapter.CreateDirectoryObserver(TimeSpan.FromSeconds(30)), TimeSpan.FromSeconds(1), token);
    }

    internal static async Task<DesktopDirectoryObserver> ConnectForTestAsync(IDesktopConnection connection,
        TimeSpan heartbeatInterval, CancellationToken token = default)
    {
        var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new(), token);
        return await WarmUpAsync(adapter.CreateDirectoryObserver(heartbeatInterval), TimeSpan.FromMilliseconds(50), token);
    }

    private static async Task<DesktopDirectoryObserver> WarmUpAsync(DesktopDirectoryObserver observer,
        TimeSpan window, CancellationToken token)
    {
        try { await Task.Delay(window, token); observer.Snapshot(); return observer; }
        catch { await observer.DisposeAsync(); throw; }
    }

    public IReadOnlyList<DesktopDirectorySession> Snapshot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (readerTask.IsCompleted || heartbeatTask.IsCompleted)
        {
            _ = readerTask.Exception; _ = heartbeatTask.Exception;
            throw new DesktopAdapterException("directory_observer_disconnected");
        }
        connection.VerifyStillSameOwner();
        lock (indexGate) return index.Snapshot();
    }

    private async Task ReadAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                var frame = await DesktopFrames.ReadAsync(connection.Stream, token);
                switch (DesktopFrames.Text(frame, "type"))
                {
                    case "broadcast":
                        lock (indexGate) index.Capture(frame, clientId);
                        break;
                    case "client-discovery-request":
                        await WriteAsync(new()
                        {
                            ["type"] = "client-discovery-response", ["requestId"] = DesktopFrames.Id(frame, "requestId").ToString("D"),
                            ["response"] = new JsonObject { ["canHandle"] = false }
                        }, token);
                        break;
                    default: throw new DesktopAdapterException("unexpected_directory_observer_frame");
                }
            }
        }
        finally { lifetime.Cancel(); await CloseAsync(); }
    }

    private async Task RefreshMetadataAsync(TimeSpan interval, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(token))
            {
                IReadOnlyList<DesktopDirectorySession> rows;
                lock (indexGate) rows = index.Snapshot();
                // At most the existing 256 known IDs per production 30-second interval. No blind IDs,
                // following=true subscription, snapshot request, history request or model input is sent.
                foreach (var row in rows.Take(DesktopDirectoryIndex.MaximumSessions))
                    await WriteAsync(new()
                    {
                        ["type"] = "broadcast", ["method"] = "thread-stream-following-status-requested", ["version"] = 1,
                        ["sourceClientId"] = clientId.ToString("D"),
                        ["params"] = new JsonObject { ["hostId"] = "local", ["conversationId"] = row.ConversationId.ToString("D") }
                    }, token);
            }
        }
        finally { lifetime.Cancel(); await CloseAsync(); }
    }

    private async Task WriteAsync(JsonObject frame, CancellationToken token)
    {
        await writer.WaitAsync(token);
        try { connection.VerifyStillSameOwner(); await DesktopFrames.WriteAsync(connection.Stream, frame, token); }
        finally { writer.Release(); }
    }

    private async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) await owner.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        var completion = Task.WhenAll(readerTask, heartbeatTask);
        try { await CloseAsync(); await completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is OperationCanceledException or IOException or DesktopAdapterException or ObjectDisposedException) { }
        catch (TimeoutException) { } // Closing a broken transport must not block worker shutdown indefinitely.
        finally
        {
            if (completion.IsCompleted) { _ = completion.Exception; lifetime.Dispose(); }
            else _ = FinishDisposalAsync(completion);
        }
        // The writer remains managed: a canceled asynchronous writer can still release it safely.
    }

    private async Task FinishDisposalAsync(Task completion)
    {
        try { await completion; } catch { /* Both background failures are observed after bounded caller disposal. */ }
        finally { lifetime.Dispose(); }
    }
}

public sealed partial class DesktopAdapter
{
    internal DesktopDirectoryObserver CreateDirectoryObserver(TimeSpan heartbeatInterval)
        => new(this, connection, clientId, directory, heartbeatInterval);
}
