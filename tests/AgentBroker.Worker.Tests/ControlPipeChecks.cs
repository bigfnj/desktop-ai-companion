using AgentBroker.Protocol;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

internal static class ControlPipeChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("Missing test user.");
        string sid = user.Value;
        RawSecurityDescriptor Parse(string owner, string dacl) => new($"O:{owner}G:{sid}D:{dacl}");
        string permitted = $"P(D;;FA;;;NU)(A;;GA;;;{sid})(A;;GA;;;SY)";
        check(ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, permitted), user),
            "control security accepts actual User ownership with protected User/System authority");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse("BA", permitted), user),
            "control security rejects the default Administrators owner even with a User grant");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse("S-1-5-21-111-222-333-444", permitted), user),
            "control security rejects a foreign pipe owner");
        check(!ControlPipeSecurity.IsCurrentUserPipe(new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            user, user, null, null), user), "control security rejects a null DACL");
        check(!ControlPipeSecurity.IsCurrentUserPipe(new RawSecurityDescriptor(ControlFlags.None,
            user, user, null, null), user), "control security rejects an absent DACL");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, $"(A;;GA;;;{sid})(A;;GA;;;SY)"), user),
            "control security requires protection against inherited grants");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, $"P(A;;GA;;;{sid})(A;;GA;;;SY)"), user),
            "control security requires an explicit Network SID denial even for the same account");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, permitted.Replace("(D;;FA;;;NU)", "(D;;GW;;;NU)", StringComparison.Ordinal)), user),
            "partial network write denial is insufficient for the local management boundary");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, permitted.Replace("(D;;FA;;;NU)", "(D;IO;FA;;;NU)", StringComparison.Ordinal)), user),
            "an inherit-only Network denial does not protect the pipe itself");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, $"P(A;;GA;;;{sid})(D;;FA;;;NU)(A;;GA;;;SY)"), user),
            "a Network denial placed after an allow is refused");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, permitted + "(A;;GW;;;WD)"), user),
            "control security rejects Everyone generic write");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, permitted + "(A;;0x00000002;;;AU)"), user),
            "control security rejects foreign explicit write-data rights");
        check(!ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, permitted + "(A;;GA;;;BA)"), user),
            "control management authority excludes an additional administrator group grant");
        check(ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, permitted + "(A;;0x00120000;;;WD)"), user),
            "foreign metadata-read and synchronization grants do not imply write authority");
        check(ControlPipeSecurity.IsCurrentUserPipe(Parse(sid, "P(D;;GW;;;WD)" + permitted[1..]), user),
            "foreign write denials do not add management authority");
        var callback = Parse(sid, permitted);
        callback.DiscretionaryAcl!.InsertAce(callback.DiscretionaryAcl.Count,
            new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 2, user, true, null));
        check(!ControlPipeSecurity.IsCurrentUserPipe(callback, user),
            "control security refuses callback ACEs it cannot evaluate");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string name = "agentbroker-control-test-" + Guid.NewGuid().ToString("N");
        await using (var server = ControlPipeSecurity.CreateServer(name, 1))
        await using (var client = ControlPipeSecurity.CreateClient(name))
        {
            Task connected = server.WaitForConnectionAsync(deadline.Token);
            await client.ConnectAsync(deadline.Token);
            await connected;
            check(ControlPipeSecurity.ValidateConnectedServer(client, Environment.ProcessId) == Environment.ProcessId,
                "the actual connected control server is verified by User SID and kernel server PID");
            bool wrongPidRefused = false;
            try { ControlPipeSecurity.ValidateConnectedServer(client, Environment.ProcessId + 1); }
            catch (IOException) { wrongPidRefused = true; }
            check(wrongPidRefused, "a required control server PID mismatch is refused before sending data");
            await client.WriteAsync(new byte[] { 17 }, deadline.Token);
            var received = new byte[1];
            check(await server.ReadAsync(received, deadline.Token) == 1 && received[0] == 17,
                "explicit User ownership supports a same-user management connection");
        }

        string hostileName = "agentbroker-control-hostile-test-" + Guid.NewGuid().ToString("N");
        var hostileSecurity = new PipeSecurity();
        hostileSecurity.SetOwner(user);
        hostileSecurity.SetAccessRuleProtection(true, false);
        hostileSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        hostileSecurity.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        hostileSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.WriteData, AccessControlType.Allow));
        await using (var server = NamedPipeServerStreamAcl.Create(hostileName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, hostileSecurity))
        await using (var client = ControlPipeSecurity.CreateClient(hostileName))
        {
            Task connected = server.WaitForConnectionAsync(deadline.Token);
            await client.ConnectAsync(deadline.Token);
            await connected;
            bool refused = false;
            try { ControlPipeSecurity.ValidateConnectedServer(client, Environment.ProcessId); }
            catch (UnauthorizedAccessException) { refused = true; }
            check(refused, "an actual same-user process with a foreign-writable pipe is refused before any request bytes");
        }
    }
}
