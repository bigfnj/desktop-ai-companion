using System.IO.Pipes;
using System.Security.Principal;
using AgentBroker.ClaudeAdapter;
using AgentBroker.ClaudeSmokeProbe;

internal static class NativeBoundaryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var record = check;
        check = (value, label) => record(value, "NativeBoundaryChecks.cs: " + label);
        Console.WriteLine("WITNESS: CLAUDE_NATIVE_BOUNDARY_EXECUTED");
        const string pipe = @"\\.\pipe\cc-msg-0123456789abcdef0123456789abcdef";
        foreach (string suffix in new[] { "\n", "\r\n", "\nignored" })
        {
            bool refused = false;
            try { CanonicalPeerTarget.Parse(pipe + suffix); }
            catch (ArgumentException) { refused = true; }
            check(refused, "CLAUDE_PIPE_ABSOLUTE_END: trailing newlines cannot become a canonical credential target");
        }
        check(CanonicalPeerTarget.Parse(pipe).CanonicalPath == pipe,
            "CLAUDE_PIPE_CANONICAL_WITNESS: the ordinary message pipe still canonicalizes");
        foreach (var profile in EngineProfile.Verified)
        {
            var target = new NativeTarget(pipe, 12345, @"C:\SyntheticPublic\claude.exe", profile.Sha256,
                profile.Version, 134_000_000_000_000_000, Guid.NewGuid().ToString("D"));
            var smoke = new SmokeTarget(target, CanonicalPeerTarget.Derive(pipe, 12345, @"C:\SyntheticUser"), NativeActivity.Idle);
            check(smoke.Classification == "verified_native_runtime",
                "CLAUDE_SMOKE_ORIGIN: every reviewed executable reports verified runtime without guessing its installer from version");
        }

        // Exercise the production OS connection against an owned synthetic pipe, without metadata or credentials.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string name = "agentbroker-sqos-check-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task waiting = server.WaitForConnectionAsync(timeout.Token);
        using var client = await WindowsNativePeerConnectionFactory.ConnectWindowsAsync(name, timeout.Token);
        Console.WriteLine("CLAUDE_PIPE_STAGE: production client connected");
        await waiting.WaitAsync(timeout.Token);
        Console.WriteLine("CLAUDE_PIPE_STAGE: synthetic server accepted");
        var received = new byte[1];
        Task read = server.ReadExactlyAsync(received, timeout.Token).AsTask();
        await client.Outbound.WriteAsync(new byte[] { 1 }, timeout.Token);
        Console.WriteLine("CLAUDE_PIPE_STAGE: client write completed");
        await read.WaitAsync(timeout.Token);
        Console.WriteLine("CLAUDE_PIPE_STAGE: server read completed");
        TokenImpersonationLevel level = TokenImpersonationLevel.None;
        // Resolve WindowsIdentity's managed dependencies before entering an identification-only token. Loading an
        // assembly is a filesystem operation and is deliberately unavailable while the server holds that token.
        using (var processIdentity = WindowsIdentity.GetCurrent()) { _ = processIdentity.ImpersonationLevel; }
        server.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            level = identity.ImpersonationLevel;
        });
        check(received[0] == 1, "CLAUDE_PIPE_SQOS_WITNESS: the production pipe method transfers a synthetic byte");
        check(level == TokenImpersonationLevel.Identification,
            "CLAUDE_PIPE_IDENTIFICATION: the server receives identification without client impersonation authority");
    }
}
