using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DesktopAICompanion.AgentBroker.CodexRelay;

namespace DesktopAICompanion.AgentBroker.RelayTests;

internal static class Program
{
    private const string Version = "relay-test-v1";
    private const string Approval = " {\"id\":\"approval:1\",\"method\":\"item/commandExecution/requestApproval\",\"params\":{\"threadId\":\"thread-a\",\"turnId\":\"turn-a\",\"itemId\":\"item-a\"}}\r\n";
    private static int _checks;

    private static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--synthetic-engine") return await ChildAsync(args).ConfigureAwait(false);
        try
        {
            await RejectWrongHashAsync().ConfigureAwait(false);
            await PreserveAndInjectAsync().ConfigureAwait(false);
            await RejectWrongVersionAsync().ConfigureAwait(false);
            await LateResponseAsync().ConfigureAwait(false);
            await GuiTurnAdmissionAsync().ConfigureAwait(false);
            await EngineExitAsync().ConfigureAwait(false);
            await StderrFailureAsync().ConfigureAwait(false);
            await ControlListenerFailureAsync().ConfigureAwait(false);
            await DisposeWhileRunningAsync().ConfigureAwait(false);
            Console.WriteLine("RESULT=PASS assertions=" + _checks);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            Console.WriteLine("RESULT=FAIL");
            return 1;
        }
    }

    private static void Check(string description, bool condition)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }

    private static async Task RejectWrongHashAsync()
    {
        await using var run = new Rig(hash: new string('0', 64));
        try { await run.Start().ConfigureAwait(false); throw new InvalidOperationException("Wrong hash was accepted."); }
        catch (InvalidOperationException ex) when (ex.Message == "Engine identity is not approved.") { }
        Check("unapproved executable hash is rejected before any child launch", !File.Exists(run.Counter));
    }

    private static async Task PreserveAndInjectAsync()
    {
        string? previous = Environment.GetEnvironmentVariable("AGENTBROKER_RELAY_TEST_MARKER");
        string marker = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("AGENTBROKER_RELAY_TEST_MARKER", marker);
        try
        {
            await using var run = new Rig();
            Task<int> completion = run.Start();
            await run.ConnectAsync().ConfigureAwait(false);
            JsonElement early = await run.ControlAsync(new { operation = "send", messageId = "early", threadId = "thread-a", text = "early", incarnation = "x" }).ConfigureAwait(false);
            Check("injection is closed until the GUI's initialize response is verified", !early.GetProperty("ok").GetBoolean());

            const string initialize = " { \"id\": 1, \"method\" : \"initialize\", \"params\":{} }\r\n";
            run.Input.Add(initialize);
            await run.Output.WaitForAsync("\"userAgent\"").ConfigureAwait(false);
            run.Input.Add("{\"id\":\"ctx\",\"method\":\"test/context\"}\n");
            await run.Output.WaitForAsync("\"id\":\"ctx\"").ConfigureAwait(false);
            JsonElement context = run.Output.Message("ctx").GetProperty("result");
            Check("original engine inherits the exact working directory", context.GetProperty("cwd").GetString() == Environment.CurrentDirectory);
            Check("original engine inherits the environment", context.GetProperty("marker").GetString() == marker);
            Check("argument boundaries, empty values, spaces and quotes survive", context.GetProperty("args").EnumerateArray().Select(e => e.GetString()).SequenceEqual(run.Arguments));
            Check("GUI initialize JSONL reaches engine byte for byte, including whitespace and CRLF", context.GetProperty("initialize").GetString() == Convert.ToBase64String(Encoding.UTF8.GetBytes(initialize)));

            run.Input.Add("{\"id\":\"persisted\",\"method\":\"thread/read\",\"params\":{\"threadId\":\"persisted-only\"}}\n");
            await run.Output.WaitForAsync("\"id\":\"persisted\"").ConfigureAwait(false);
            JsonElement status = await run.ControlAsync(new { operation = "status" }).ConfigureAwait(false);
            string incarnation = status.GetProperty("incarnation").GetString()!;
            Check("a verified version enables the adapter", status.GetProperty("verified").GetBoolean());
            JsonElement notLoaded = await run.ControlAsync(new { operation = "send", messageId = "not-loaded", threadId = "persisted-only", text = "no", incarnation }).ConfigureAwait(false);
            Check("thread/read never turns a persisted record into a loaded target", Reason(notLoaded) == "thread-not-loaded");
            run.Input.Add("{\"id\":\"resume\",\"method\":\"thread/resume\",\"params\":{\"threadId\":\"thread-a\"}}\n");
            await run.Output.WaitForAsync("\"id\":\"resume\"").ConfigureAwait(false);
            JsonElement stale = await run.ControlAsync(new { operation = "send", messageId = "stale", threadId = "thread-a", text = "no", incarnation = "previous-engine" }).ConfigureAwait(false);
            Check("stale relay incarnation cannot target a current thread", Reason(stale) == "stale-incarnation");
            run.Input.Add("{\"id\":\"busy\",\"method\":\"test/active\"}\n");
            await run.Output.WaitForAsync("\"id\":\"busy\"").ConfigureAwait(false);
            JsonElement busy = await run.ControlAsync(new { operation = "send", messageId = "busy", threadId = "thread-a", text = "no", incarnation }).ConfigureAwait(false);
            Check("active threads reject injection instead of competing with GUI turns", Reason(busy) == "thread-not-idle");
            run.Input.Add("{\"id\":\"idle\",\"method\":\"test/idle\"}\n");
            await run.Output.WaitForAsync("\"id\":\"idle\"").ConfigureAwait(false);
            await run.ReconnectAsync().ConfigureAwait(false);
            JsonElement reconnected = await run.ControlAsync(new { operation = "status" }).ConfigureAwait(false);
            Check("a broker IPC disconnect and reconnect retains the original engine incarnation", reconnected.GetProperty("incarnation").GetString() == incarnation);
            JsonElement arbitrary = await run.ControlAsync(new { operation = "turn/start", threadId = "thread-a", text = "no", incarnation }).ConfigureAwait(false);
            Check("local IPC is an allowlisted adapter, not an arbitrary engine RPC proxy", Reason(arbitrary) == "unsupported-operation");
            JsonElement missingId = await run.ControlAsync(new { operation = "send", threadId = "thread-a", text = "no", incarnation }).ConfigureAwait(false);
            Check("injection requires an explicit stable broker message ID", Reason(missingId) == "invalid-request");

            Task<JsonElement> sent = run.ControlAsync(new { operation = "send", messageId = "once", threadId = "thread-a", text = "hello from broker", incarnation });
            await run.Output.WaitForAsync(Approval).ConfigureAwait(false);
            Check("engine approval request passes to the GUI without rewriting", run.Output.Text.Contains(Approval, StringComparison.Ordinal));
            const string decision = " { \"id\" : \"approval:1\", \"result\" : {\"decision\":\"decline\"} }\r\n";
            run.Input.Add(decision);
            JsonElement sentResponse = await sent.ConfigureAwait(false);
            await run.Output.WaitForAsync("test/approvalObserved").ConfigureAwait(false);
            Check("the GUI's original approval decision reaches the engine unchanged", run.Output.Text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(decision)), StringComparison.Ordinal));
            Check("injected response is returned only through broker IPC", sentResponse.GetProperty("ok").GetBoolean() && !run.Output.Text.Contains("agentbroker:", StringComparison.Ordinal));
            Check("engine turn notifications still reach the GUI", run.Output.Text.Contains("turn/started", StringComparison.Ordinal) && run.Output.Text.Contains("turn/completed", StringComparison.Ordinal));
            JsonElement repeated = await run.ControlAsync(new { operation = "send", messageId = "once", threadId = "thread-a", text = "hello from broker", incarnation }).ConfigureAwait(false);
            run.Input.Add("{\"id\":\"count\",\"method\":\"test/context\"}\n");
            await run.Output.WaitForAsync("\"id\":\"count\"").ConfigureAwait(false);
            Check("same-message retries reuse the original admission without starting another turn",
                repeated.GetProperty("requestId").GetString() == sentResponse.GetProperty("requestId").GetString()
                && run.Output.Message("count").GetProperty("result").GetProperty("turns").GetInt32() == 1);
            JsonElement conflict = await run.ControlAsync(new { operation = "send", messageId = "once", threadId = "thread-a", text = "changed", incarnation }).ConfigureAwait(false);
            Check("reusing a message ID for changed content is refused", Reason(conflict) == "message-id-conflict");

            JsonElement refused = await run.ControlAsync(new { operation = "send", messageId = "refused", threadId = "thread-a", text = "engine-refusal", incarnation }).ConfigureAwait(false);
            JsonElement afterError = await run.ControlAsync(new { operation = "send", messageId = "after-error", threadId = "thread-a", text = "no", incarnation }).ConfigureAwait(false);
            Check("an engine error never fabricates idle for the next injection", Reason(refused) == "engine-refused" && Reason(afterError) == "thread-not-idle");
            run.Input.Add("{\"id\":\"interrupted\",\"method\":\"test/interrupted\"}\n");
            await run.Output.WaitForAsync("\"id\":\"interrupted\"").ConfigureAwait(false);
            JsonElement interrupted = await run.ControlAsync(new { operation = "send", messageId = "after-interrupted", threadId = "thread-a", text = "no", incarnation }).ConfigureAwait(false);
            Check("an interrupted completion requires authoritative idle before another injection", Reason(interrupted) == "thread-not-idle");

            run.Input.Add("{\"id\":\"agentbroker:external:1\",\"method\":\"test/echo\"}\n");
            await run.Output.WaitForAsync("\"id\":\"agentbroker:external:1\"").ConfigureAwait(false);
            Check("an unknown response is forwarded even with the relay-like prefix", run.Output.Message("agentbroker:external:1").GetProperty("result").GetProperty("echo").GetBoolean());

            run.Input.Add("{\"id\":\"close\",\"method\":\"test/close\"}\n");
            await run.Output.WaitForAsync("thread/closed").ConfigureAwait(false);
            JsonElement closed = await run.ControlAsync(new { operation = "send", messageId = "closed", threadId = "thread-a", text = "no", incarnation }).ConfigureAwait(false);
            Check("closed engine thread immediately loses its routing target", Reason(closed) == "thread-not-loaded");
            run.Input.Complete();
            Check("GUI EOF allows orderly engine exit and forwards its exit code", await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) == 0);
            Check("exactly one original engine process was launched", File.ReadAllLines(run.Counter).Length == 1);
        }
        finally { Environment.SetEnvironmentVariable("AGENTBROKER_RELAY_TEST_MARKER", previous); }
    }

    private static async Task RejectWrongVersionAsync()
    {
        await using var run = new Rig(version: "unapproved-version");
        Task<int> completion = run.Start();
        await run.ConnectAsync().ConfigureAwait(false);
        run.Input.Add("{\"id\":1,\"method\":\"initialize\"}\n");
        await run.Output.WaitForAsync("userAgent").ConfigureAwait(false);
        JsonElement status = await run.ControlAsync(new { operation = "status" }).ConfigureAwait(false);
        Check("a mismatched initialize version disables injection while preserving GUI transport", !status.GetProperty("verified").GetBoolean() && Reason(status) == "engine-version-mismatch");
        run.Input.Complete();
        Check("version mismatch never launches a second verification engine", await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) == 0 && File.ReadAllLines(run.Counter).Length == 1);
    }

    private static async Task EngineExitAsync()
    {
        await using var run = new Rig();
        Task<int> completion = run.Start();
        await run.ConnectAsync().ConfigureAwait(false);
        run.Input.Add("{\"id\":1,\"method\":\"test/exit\"}\n");
        Check("engine exit is propagated while GUI input is still open", await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) == 23);
    }

    private static async Task StderrFailureAsync()
    {
        await using var run = new Rig();
        run.Errors.FailWrites = true;
        Task<int> completion = run.Start();
        await run.ConnectAsync().ConfigureAwait(false);
        run.Input.Add("{\"id\":1,\"method\":\"test/stderr\"}\n");
        try { await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); throw new InvalidOperationException("Expected stderr failure."); }
        catch (IOException) { }
        Check("a failed stderr forwarding sink terminates the relay lifetime", completion.IsFaulted);
        Check("stderr failure cleans up only its launched engine", run.EngineHasExited());
    }

    private static async Task ControlListenerFailureAsync()
    {
        await using var run = new Rig();
        await using var occupied = new NamedPipeServerStream(run.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task<int> completion = run.Start();
        try { await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); throw new InvalidOperationException("Expected listener failure."); }
        catch (IOException) { }
        Check("an unavailable control listener cannot silently leave a relay running", completion.IsFaulted);
        Check("control listener failure cleans up the launched engine", run.EngineHasExited());
    }

    private static async Task DisposeWhileRunningAsync()
    {
        await using var run = new Rig();
        Task<int> completion = run.Start();
        await run.ConnectAsync().ConfigureAwait(false);
        run.Input.Add("{\"id\":1,\"method\":\"initialize\"}\n");
        await run.Output.WaitForAsync("userAgent").ConfigureAwait(false);
        await run.DisposeRelayAsync().ConfigureAwait(false);
        Check("disposing an active relay completes its lifetime before destroying process handles", await completion.ConfigureAwait(false) == 130);
        await run.DisposeRelayAsync().ConfigureAwait(false);
        Check("relay disposal is idempotent and leaves no launched engine", run.EngineHasExited());
    }

    private static async Task LateResponseAsync()
    {
        await using var run = new Rig(responseTimeout: TimeSpan.FromMilliseconds(150));
        Task<int> completion = run.Start();
        await run.ConnectAsync().ConfigureAwait(false);
        run.Input.Add("{\"id\":1,\"method\":\"initialize\"}\n");
        run.Input.Add("{\"id\":2,\"method\":\"thread/resume\"}\n");
        await run.Output.WaitForAsync("\"id\":2").ConfigureAwait(false);
        JsonElement status = await run.ControlAsync(new { operation = "status" }).ConfigureAwait(false);
        JsonElement timedOut = await run.ControlAsync(new { operation = "send", messageId = "slow", threadId = "thread-a", text = "slow", incarnation = status.GetProperty("incarnation").GetString() }).ConfigureAwait(false);
        Check("an unanswered injected turn reports an explicit response timeout", Reason(timedOut) == "response-timeout");
        run.Input.Add("{\"id\":\"approval:1\",\"result\":{\"decision\":\"decline\"}}\n");
        await run.Output.WaitForAsync("turn/completed").ConfigureAwait(false);
        Check("a late owned response stays out of the GUI after broker timeout", !run.Output.Text.Contains("agentbroker:", StringComparison.Ordinal));
        run.Input.Complete();
        Check("timed-out broker traffic does not prevent clean shutdown", await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) == 0);
    }

    private static async Task GuiTurnAdmissionAsync()
    {
        await using var run = new Rig();
        Task<int> completion = run.Start();
        await run.ConnectAsync().ConfigureAwait(false);
        run.Input.Add("{\"id\":1,\"method\":\"initialize\"}\n");
        run.Input.Add("{\"id\":2,\"method\":\"thread/resume\"}\n");
        run.Input.Add("{\"id\":\"gui-turn\",\"method\":\"turn/start\",\"params\":{\"threadId\":\"thread-a\",\"input\":[{\"type\":\"text\",\"text\":\"GUI message\"}]}}\n");
        await run.Output.WaitForAsync(Approval).ConfigureAwait(false);
        JsonElement status = await run.ControlAsync(new { operation = "status" }).ConfigureAwait(false);
        JsonElement response = await run.ControlAsync(new { operation = "send", messageId = "competing", threadId = "thread-a", text = "broker message", incarnation = status.GetProperty("incarnation").GetString() }).ConfigureAwait(false);
        Check("a GUI turn written at the shared stdin boundary closes broker admission", Reason(response) == "thread-not-idle");
        run.Input.Add("{\"id\":\"approval:1\",\"result\":{\"decision\":\"decline\"}}\n");
        await run.Output.WaitForAsync("\"id\":\"gui-turn\"").ConfigureAwait(false);
        Check("the GUI turn response remains owned by and visible to the GUI", run.Output.Message("gui-turn").GetProperty("result").GetProperty("turn").GetProperty("id").GetString() == "turn-a");
        run.Input.Complete();
        Check("competing broker admission does not disrupt original GUI shutdown", await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) == 0);
    }

    private static string? Reason(JsonElement response)
        => response.TryGetProperty("reason", out var reason) ? reason.GetString() : null;

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _scratch = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
        private readonly string _pipeName = "AgentBroker.Relay.Test." + Guid.NewGuid().ToString("N");
        private readonly RelaySession _relay;
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        internal readonly InputStream Input = new();
        internal readonly OutputStream Output = new();
        internal readonly OutputStream Errors = new();
        internal string Counter => Path.Combine(_scratch, "launches.log");
        internal string PipeName => _pipeName;
        internal IReadOnlyList<string> Arguments { get; }

        internal Rig(string? hash = null, string version = Version, TimeSpan? responseTimeout = null)
        {
            Directory.CreateDirectory(_scratch);
            Arguments = new[] { "--synthetic-engine", "--counter", Counter, "", "two words", "quote\"literal", "dollar$literal", "backtick`literal" };
            string executable = Environment.ProcessPath!;
            using var file = File.OpenRead(executable);
            _relay = new RelaySession(new RelayOptions
            {
                EnginePath = executable, EngineSha256 = hash ?? Convert.ToHexString(SHA256.HashData(file)),
                EngineVersion = version, PipeName = _pipeName, EngineArguments = Arguments,
                ResponseTimeout = responseTimeout ?? TimeSpan.FromSeconds(5), ExitTimeout = TimeSpan.FromSeconds(2),
            });
        }

        internal Task<int> Start() => _relay.RunAsync(Input, Output, Errors, _stop.Token);
        internal ValueTask DisposeRelayAsync() => _relay.DisposeAsync();
        internal bool EngineHasExited()
        {
            if (!File.Exists(Counter)) return true; // A listener can fail before the child enters Main.
            int pid = int.Parse(File.ReadAllLines(Counter).Single(), System.Globalization.CultureInfo.InvariantCulture);
            try { using var process = Process.GetProcessById(pid); return process.HasExited; }
            catch (ArgumentException) { return true; }
        }

        internal async Task ConnectAsync()
        {
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await _pipe.ConnectAsync(5000, _stop.Token).ConfigureAwait(false);
            _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        }

        internal async Task ReconnectAsync()
        {
            _reader?.Dispose();
            _pipe?.Dispose();
            await ConnectAsync().ConfigureAwait(false);
        }

        internal async Task<JsonElement> ControlAsync(object request)
        {
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
            await _pipe!.WriteAsync(line, _stop.Token).ConfigureAwait(false);
            await _pipe.FlushAsync(_stop.Token).ConfigureAwait(false);
            string? response = await _reader!.ReadLineAsync(_stop.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(response ?? throw new IOException("No adapter response."));
            return json.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _reader?.Dispose();
            _pipe?.Dispose();
            Input.Complete();
            await _relay.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
            string scratch = Path.GetFullPath(_scratch);
            string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch")) + Path.DirectorySeparatorChar;
            if (!scratch.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Scratch cleanup escaped its test root.");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed class InputStream : Stream
    {
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;
        internal void Add(string line) => _frames.Writer.TryWrite(Encoding.UTF8.GetBytes(line));
        internal void Complete() => _frames.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        {
            while (_current == null || _offset == _current.Length)
            {
                try { _current = await _frames.Reader.ReadAsync(cancellation).ConfigureAwait(false); _offset = 0; }
                catch (ChannelClosedException) { return 0; }
            }
            int count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class OutputStream : Stream
    {
        private readonly MemoryStream _bytes = new();
        private readonly object _gate = new();
        internal bool FailWrites;
        internal string Text { get { lock (_gate) return Encoding.UTF8.GetString(_bytes.ToArray()); } }
        internal async Task WaitForAsync(string text)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!Text.Contains(text, StringComparison.Ordinal)) await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
        internal JsonElement Message(string id)
        {
            foreach (string line in Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("id", out var found) && found.ValueKind == JsonValueKind.String && found.GetString() == id)
                    return json.RootElement.Clone();
            }
            throw new InvalidOperationException("Expected GUI response missing.");
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation = default)
        {
            if (FailWrites) throw new IOException("Synthetic output sink failure.");
            lock (_gate) _bytes.Write(bytes.Span);
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellation) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) { lock (_gate) _bytes.Write(b, o, c); }
    }

    private static async Task<int> ChildAsync(string[] args)
    {
        File.AppendAllText(args[2], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        byte[]? initialize = null;
        JsonElement? pending = null;
        int turns = 0;
        await foreach (byte[] frame in ReadChildFrames(input).ConfigureAwait(false))
        {
            using var json = JsonDocument.Parse(frame);
            JsonElement root = json.RootElement;
            string? method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            object? id = root.TryGetProperty("id", out var identifier) ? identifier.Clone() : null;
            object? result = null;
            if (method == "initialize") { initialize = frame; result = new { userAgent = "synthetic/" + Version + " test-platform" }; }
            else if (method == "thread/resume" || method == "thread/read")
                result = new { thread = new { id = method == "thread/read" ? "persisted-only" : "thread-a", status = new { type = "idle" } } };
            else if (method == "test/context") result = new
            {
                cwd = Environment.CurrentDirectory, args, turns,
                marker = Environment.GetEnvironmentVariable("AGENTBROKER_RELAY_TEST_MARKER"),
                initialize = Convert.ToBase64String(initialize!),
            };
            else if (method == "test/echo") result = new { echo = true };
            else if (method is "test/active" or "test/idle")
            {
                await ChildWriteAsync(output, new { method = "thread/status/changed", @params = new { threadId = "thread-a", status = new { type = method == "test/active" ? "active" : "idle" } } }).ConfigureAwait(false);
                result = new { };
            }
            else if (method == "test/interrupted")
            {
                await ChildWriteAsync(output, new { method = "turn/completed", @params = new { threadId = "thread-a", turn = new { id = "interrupted", status = "interrupted" } } }).ConfigureAwait(false);
                result = new { };
            }
            else if (method == "test/close")
            {
                await ChildWriteAsync(output, new { method = "thread/closed", @params = new { threadId = "thread-a" } }).ConfigureAwait(false);
                result = new { };
            }
            else if (method == "test/exit") return 23;
            else if (method == "test/stderr")
            {
                await Console.OpenStandardError().WriteAsync(Encoding.UTF8.GetBytes("synthetic stderr\n")).ConfigureAwait(false);
                result = new { };
            }
            else if (method == "turn/start")
            {
                turns++;
                if (root.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("input", out var inputs)
                    && inputs.GetArrayLength() != 0 && inputs[0].GetProperty("text").GetString() == "engine-refusal")
                {
                    await ChildWriteAsync(output, new { id, error = new { code = -32000, message = "synthetic refusal" } }).ConfigureAwait(false);
                    continue;
                }
                pending = root.Clone();
                await ChildWriteAsync(output, new { method = "turn/started", @params = new { threadId = "thread-a", turn = new { id = "turn-a", status = "inProgress" } } }).ConfigureAwait(false);
                await output.WriteAsync(Encoding.UTF8.GetBytes(Approval)).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                continue;
            }
            else if (method == null && identifier.ValueKind == JsonValueKind.String && identifier.GetString() == "approval:1" && pending != null)
            {
                await ChildWriteAsync(output, new { method = "test/approvalObserved", @params = new { raw = Convert.ToBase64String(frame) } }).ConfigureAwait(false);
                await ChildWriteAsync(output, new { id = pending.Value.GetProperty("id").Clone(), result = new { turn = new { id = "turn-a", status = "completed", items = Array.Empty<object>() } } }).ConfigureAwait(false);
                await ChildWriteAsync(output, new { method = "turn/completed", @params = new { threadId = "thread-a", turn = new { id = "turn-a", status = "completed" } } }).ConfigureAwait(false);
                pending = null;
                continue;
            }
            if (method != null) await ChildWriteAsync(output, new { id, result }).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task ChildWriteAsync(Stream output, object value)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n")).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<byte[]> ReadChildFrames(Stream input)
    {
        byte[] one = new byte[1];
        using var frame = new MemoryStream();
        while (await input.ReadAsync(one).ConfigureAwait(false) != 0)
        {
            frame.WriteByte(one[0]);
            if (one[0] != (byte)'\n') continue;
            yield return frame.ToArray();
            frame.SetLength(0);
        }
    }
}
