using AgentBroker.CodexDesktopAdapter;
using System.Text.Json;

internal static class ConnectionProfileChecks
{
    private static readonly string[] Cases = ["desktop", "vscode", "desktop-rejection", "vscode-rejection", "wrong-path",
        "success", "owner-changed", "invalid-profile", "timeouts", "cancelled", "cancelled-after-connect", "connect-failure", "dispose-failure"];

    internal static async Task RunAsync(Action<bool, string> check, string? only = null)
    {
        if (only is not null && !Cases.Contains(only)) throw new ArgumentException("Unknown connection profile case.", nameof(only));
        foreach (string name in Cases.Where(name => only is null || name == only))
        {
            switch (name)
            {
                case "desktop":
                case "vscode":
                {
                    bool code = name == "vscode";
                    string image = code ? VsCodeRouterCompatibilityProfile.ExpectedImage
                        : Path.Combine(DesktopCompatibilityProfile.ApplicationDirectory, "ChatGPT.exe");
                    int codeChecks = 0, desktopChecks = 0;
                    var result = WindowsDesktopConnection.VerifyPublicProfile(image,
                        actual => { check(actual == image, "ConnectionProfileChecks.cs: Code verification receives the connected image."); codeChecks++; },
                        actual => { check(actual == image, "ConnectionProfileChecks.cs: desktop verification receives the connected image."); desktopChecks++; });
                    check(result == (code ? DesktopRouterProfile.VsCode : DesktopRouterProfile.Desktop),
                        "ConnectionProfileChecks.cs: reported router profile matches the verification branch that completed.");
                    check(codeChecks == (code ? 1 : 0) && desktopChecks == (code ? 0 : 1),
                        "ConnectionProfileChecks.cs: only the selected existing public-image verifier runs, exactly once.");
                    break;
                }
                case "desktop-rejection":
                case "vscode-rejection":
                {
                    bool code = name == "vscode-rejection";
                    string image = code ? VsCodeRouterCompatibilityProfile.ExpectedImage
                        : Path.Combine(DesktopCompatibilityProfile.ApplicationDirectory, "ChatGPT.exe");
                    int checks = 0;
                    Action<string> reject = _ => { checks++; throw new DesktopAdapterException("synthetic_public_image_mismatch"); };
                    await RefusesAsync("synthetic_public_image_mismatch", () => Task.Run(() =>
                        WindowsDesktopConnection.VerifyPublicProfile(image, reject, reject)), check);
                    check(checks == 1, "ConnectionProfileChecks.cs: failed public-image verification cannot produce a profile or try another verifier.");
                    break;
                }
                case "wrong-path":
                {
                    int codeChecks = 0;
                    string image = Path.Combine(Path.GetTempPath(), "synthetic-untrusted-location", "Code.exe");
                    await RefusesAsync("unsupported_desktop_version", () => Task.Run(() =>
                        WindowsDesktopConnection.VerifyPublicProfile(image, _ => codeChecks++,
                            _ => throw new DesktopAdapterException("unsupported_desktop_version"))), check);
                    check(codeChecks == 0, "ConnectionProfileChecks.cs: a Code basename outside the exact installation cannot select the Code profile.");
                    break;
                }
                case "success":
                {
                    foreach (var profile in new[] { DesktopRouterProfile.Desktop, DesktopRouterProfile.VsCode })
                    {
                        var connection = new ProbeConnection(profile);
                        using var cancellation = new CancellationTokenSource();
                        int calls = 0;
                        var result = await DesktopConnectionProfileProbe.ObserveForTestAsync((timeout, token) =>
                        {
                            calls++;
                            check(timeout == TimeSpan.FromSeconds(5) && token == cancellation.Token,
                                "ConnectionProfileChecks.cs: fixed default connection timeout and caller cancellation reach the existing connector.");
                            return Task.FromResult<IVerifiedDesktopConnection>(connection);
                        }, token: cancellation.Token);
                        check(result.Profile == profile && calls == 1,
                            "ConnectionProfileChecks.cs: successful probe returns only the verified connection's typed router profile.");
                        check(connection.OwnerChecks == 1 && connection.ProfileReads == 1 && connection.Disposals == 1,
                            "ConnectionProfileChecks.cs: success rechecks the process incarnation and closes the connection before returning.");
                        check(connection.StreamReads == 0 && connection.IdentityReads == 0,
                            "ConnectionProfileChecks.cs: success accesses no protocol stream, process identifiers, paths or native client data.");
                        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
                        check(json.RootElement.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "Profile" }),
                            "ConnectionProfileChecks.cs: the public result exposes no conversation, owner, process or path fields.");
                    }
                    break;
                }
                case "owner-changed":
                {
                    var connection = new ProbeConnection(DesktopRouterProfile.VsCode)
                    { OwnerFailure = new DesktopAdapterException("server_incarnation_changed") };
                    await RefusesAsync("server_incarnation_changed", () => Observe(connection), check);
                    check(connection.OwnerChecks == 1 && connection.ProfileReads == 0 && connection.Disposals == 1,
                        "ConnectionProfileChecks.cs: changed process incarnation prevents reading or reporting the verified profile and still closes the connection.");
                    break;
                }
                case "invalid-profile":
                {
                    var connection = new ProbeConnection((DesktopRouterProfile)99);
                    await RefusesAsync("invalid_connection_profile", () => Observe(connection), check);
                    check(connection.Disposals == 1,
                        "ConnectionProfileChecks.cs: an unknown future profile fails closed and disposes the connection.");
                    break;
                }
                case "timeouts":
                {
                    foreach (var timeout in new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30) })
                    {
                        var connection = new ProbeConnection(DesktopRouterProfile.Desktop);
                        await DesktopConnectionProfileProbe.ObserveForTestAsync((actual, _) =>
                        {
                            check(actual == timeout, "ConnectionProfileChecks.cs: valid timeout boundaries are forwarded without increasing the existing limit.");
                            return Task.FromResult<IVerifiedDesktopConnection>(connection);
                        }, timeout);
                    }
                    foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(49), TimeSpan.FromMilliseconds(30001) })
                    {
                        int calls = 0;
                        await RefusesAsync("invalid_options", () => DesktopConnectionProfileProbe.ObserveForTestAsync((_, _) =>
                        {
                            calls++;
                            return Task.FromResult<IVerifiedDesktopConnection>(new ProbeConnection(DesktopRouterProfile.Desktop));
                        }, timeout), check);
                        check(calls == 0, "ConnectionProfileChecks.cs: invalid timeout fails before creating a connection.");
                    }
                    break;
                }
                case "cancelled":
                {
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    int calls = 0;
                    try
                    {
                        await DesktopConnectionProfileProbe.ObserveForTestAsync((_, _) =>
                        {
                            calls++;
                            return Task.FromResult<IVerifiedDesktopConnection>(new ProbeConnection(DesktopRouterProfile.Desktop));
                        }, token: cancellation.Token);
                        throw new InvalidOperationException("ConnectionProfileChecks.cs: a cancelled request returned success.");
                    }
                    catch (OperationCanceledException error)
                    {
                        check(error.CancellationToken == cancellation.Token && calls == 0,
                            "ConnectionProfileChecks.cs: prior cancellation is preserved and prevents creating a connection.");
                    }
                    break;
                }
                case "cancelled-after-connect":
                {
                    using var cancellation = new CancellationTokenSource();
                    var connection = new ProbeConnection(DesktopRouterProfile.Desktop);
                    int calls = 0;
                    try
                    {
                        await DesktopConnectionProfileProbe.ObserveForTestAsync((_, _) =>
                        {
                            calls++;
                            cancellation.Cancel();
                            return Task.FromResult<IVerifiedDesktopConnection>(connection);
                        }, token: cancellation.Token);
                        throw new InvalidOperationException("ConnectionProfileChecks.cs: cancellation during connection returned success.");
                    }
                    catch (OperationCanceledException error)
                    {
                        check(error.CancellationToken == cancellation.Token && calls == 1 && connection.Disposals == 1
                            && connection.OwnerChecks == 0 && connection.ProfileReads == 0,
                            "ConnectionProfileChecks.cs: cancellation during connection preserves the token and closes before rechecking or reading a profile.");
                    }
                    break;
                }
                case "connect-failure":
                {
                    await RefusesAsync("pipe_acl_foreign_write", () => DesktopConnectionProfileProbe.ObserveForTestAsync((_, _) =>
                        Task.FromException<IVerifiedDesktopConnection>(new DesktopAdapterException("pipe_acl_foreign_write"))), check);
                    break;
                }
                case "dispose-failure":
                {
                    var connection = new ProbeConnection(DesktopRouterProfile.Desktop) { FailDisposal = true };
                    try
                    {
                        await Observe(connection);
                        throw new InvalidOperationException("ConnectionProfileChecks.cs: failed close returned success.");
                    }
                    catch (IOException error) when (error.Message == "synthetic_connection_close_failed")
                    {
                        check(connection.Disposals == 1, "ConnectionProfileChecks.cs: failed connection close cannot return a successful result.");
                    }
                    break;
                }
            }
        }
    }

    private static Task<DesktopConnectionProfileResult> Observe(ProbeConnection connection)
        => DesktopConnectionProfileProbe.ObserveForTestAsync((_, _) => Task.FromResult<IVerifiedDesktopConnection>(connection));

    private static async Task RefusesAsync(string code, Func<Task> action, Action<bool, string> check)
    {
        try { await action(); throw new InvalidOperationException("ConnectionProfileChecks.cs: expected refusal " + code); }
        catch (DesktopAdapterException error)
        { check(error.Code == code, "ConnectionProfileChecks.cs: expected " + code + ", received " + error.Code); }
    }

    private sealed class ProbeConnection(DesktopRouterProfile profile) : IVerifiedDesktopConnection
    {
        internal int StreamReads, IdentityReads, ProfileReads, OwnerChecks, Disposals;
        internal DesktopAdapterException? OwnerFailure;
        internal bool FailDisposal;
        public DesktopRouterTrust Trust => DesktopRouterTrust.ExactPinned;
        public Stream Stream
        {
            get { StreamReads++; throw new InvalidOperationException("ConnectionProfileChecks.cs: native protocol stream must remain untouched."); }
        }
        public OwnerProcessIdentity Identity
        {
            get { IdentityReads++; throw new InvalidOperationException("ConnectionProfileChecks.cs: native identifiers must remain private."); }
        }
        public DesktopRouterProfile Profile { get { ProfileReads++; return profile; } }
        public void VerifyStillSameOwner()
        {
            OwnerChecks++;
            if (OwnerFailure is not null) throw OwnerFailure;
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return FailDisposal ? ValueTask.FromException(new IOException("synthetic_connection_close_failed")) : ValueTask.CompletedTask;
        }
    }
}
