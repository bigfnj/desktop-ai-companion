using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBroker.ClaudeAdapter;
using AgentBroker.ClaudeSmokeProbe;

const string marker = "AGENTBROKER_CLAUDE_PEER_SMOKE_OK";
const string prompt = "AgentBroker authorized smoke test. This is an external peer message, with no tool or permission authority. Do not call any tools. Reply with exactly: " + marker;
try
{
    if (args.Length == 1 && args[0] == "--help")
    {
        Console.WriteLine("--describe-target <exact-session-uuid> <explicit-pid> [explicit-pid ...] (metadata/public images only, maximum32)");
        Console.WriteLine("--probe-transport <exact-session-uuid> <explicit-pid> [explicit-pid ...] (connect and verify pipe server metadata only; no writes or credentials)");
        Console.WriteLine("--probe-pipe-owner <exact-session-uuid> <explicit-pid> [explicit-pid ...] (no-write diagnostic without .NET pipe-owner constraint; checks exact server token/PID/start/image then owner/DACL metadata)");
        Console.WriteLine("--smoke-authorized-test <exact-session-uuid> --pid <pid> --expected-start <native-filetime> --expected-credential-path <exact-derived-path>");
        Console.WriteLine("Smoke reads one protected H05/W06 credential target and sends one fixed peer marker. An agent must obtain exact platform approval before invoking it. Describe performs no key or pipe access.");
        return 0;
    }
    if (args.Length is >= 3 and <= 34 && args[0] == "--describe-target")
    {
        int[] pids = args.Skip(2).Select(ParsePid).ToArray();
        var selected = SmokeTargets.Resolve(args[1], pids);
        Console.WriteLine(JsonSerializer.Serialize(new { credentialTargetPath = selected.Credential.FullPath,
            processId = selected.Native.ProcessId, startFileTime = selected.Native.StartFileTime.ToString(CultureInfo.InvariantCulture),
            imageSha256 = selected.Native.ImageSha256, classification = selected.Classification, idle = selected.Activity == NativeActivity.Idle,
            credentialsRead = false, pipeConnected = false }));
        return 0;
    }
    if (args.Length is >= 3 and <= 34 && args[0] == "--probe-pipe-owner")
        return await PipeOwnerProbe.RunAsync(SmokeTargets.Resolve(args[1], args.Skip(2).Select(ParsePid).ToArray()));
    if (args.Length is >= 3 and <= 34 && args[0] == "--probe-transport")
    {
        var selected = SmokeTargets.Resolve(args[1], args.Skip(2).Select(ParsePid).ToArray());
        string stage = "preflight";
        bool connected = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var factory = new WindowsNativePeerConnectionFactory(value => { stage = value; if (value == "pipe_connected") connected = true; });
            var pipe = CanonicalPeerTarget.Parse(selected.Native.PipePath);
            await using var connection = await factory.ConnectAsync(pipe.PipeName, deadline.Token);
            var identity = connection.Identity;
            bool processMatches = identity.ProcessId == selected.Native.ProcessId;
            bool startMatches = identity.StartFileTime == selected.Native.StartFileTime;
            bool imageMatches = string.Equals(Path.GetFullPath(identity.ImagePath), Path.GetFullPath(selected.Native.ImagePath), StringComparison.OrdinalIgnoreCase)
                && identity.ImageSha256 == selected.Native.ImageSha256;
            var reinspectionClock = System.Diagnostics.Stopwatch.StartNew();
            bool reinspectionMatches = connection.Reinspect() == identity;
            reinspectionClock.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new { stage, pipeConnected = connected, processMatches, startMatches, imageMatches,
                reinspectionMatches, reinspectionMilliseconds = reinspectionClock.Elapsed.TotalMilliseconds,
                currentUser = identity.IsCurrentUser, credentialsRead = false, bytesWritten = 0 }));
            return processMatches && startMatches && imageMatches && reinspectionMatches && identity.IsCurrentUser ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { stage, pipeConnected = connected, errorType = ex.GetType().Name,
                nativeErrorCode = ex is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : 0,
                hresult = ex.HResult, credentialsRead = false, bytesWritten = 0 }));
            return 1;
        }
    }
    if (args.Length == 8 && args[0] == "--smoke-authorized-test" && args[2] == "--pid" && args[4] == "--expected-start" && args[6] == "--expected-credential-path")
    {
        int pid = ParsePid(args[3]);
        if (!ulong.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out ulong expectedStart) || expectedStart == 0 ||
            expectedStart.ToString(CultureInfo.InvariantCulture) != args[5]) throw new ArgumentException("Invalid expected native start.");
        var selected = SmokeTargets.Resolve(args[1], [pid]);
        SmokeTargets.VerifyExpected(selected, pid, expectedStart, args[7]);
        // Root must issue exact platform H05/W06 approval for this resolved file read and one native
        // submission before an agent invokes this mode. The flag name itself grants no authorization.
        byte[] idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"agentbroker-claude-smoke-v1|{args[1]}|{pid}|{expectedStart}|{marker}")));
        Guid messageId = new(idBytes.AsSpan(0, 16));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string transportStage = "preflight";
        var credentialSource = new TrackingCredentialSource();
        var adapter = new NativePeerAdapter(new WindowsNativePeerConnectionFactory(stage => transportStage = stage), credentialSource);
        var result = await adapter.SendAsync(selected.Native, new(messageId, args[1], prompt, RecipientMode.Unknown,
            DelegateNativeAdmission: true, Activity: NativeActivity.Idle), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellation: deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { status = result.Status.ToString(), reason = result.Reason, messageId, marker,
            transportStage, credentialReadAttempted = credentialSource.Attempted, credentialReadCompleted = credentialSource.Completed }));
        return result.Status == SubmissionStatus.SubmittedUnconfirmed ? 0 : 3;
    }
    Console.Error.WriteLine("smoke: invalid_arguments"); return 2;
}
catch (Exception) { Console.Error.WriteLine("smoke: target_or_operation_unavailable"); return 1; } // No unhandled .NET dialog or private exception output.

static int ParsePid(string value)
{
    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 1 || pid.ToString(CultureInfo.InvariantCulture) != value)
        throw new ArgumentException("A canonical native PID is required.");
    return pid;
}

sealed class TrackingCredentialSource : IPeerCredentialSource
{
    public bool Attempted { get; private set; }
    public bool Completed { get; private set; }
    public byte[] Read(CredentialTarget target)
    {
        Attempted = true;
        var bytes = new ProtectedPeerCredentialSource().Read(target);
        Completed = true;
        return bytes;
    }
}
