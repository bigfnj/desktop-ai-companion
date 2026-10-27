using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AgentBroker.ClaudeAdapter;
using AgentBroker.ClaudeSmokeProbe;

// The reviewed pins live in the embedded pin document (lane broker-pins); select one by its version.
static EngineProfile Pinned(string version) => EngineProfile.Verified.Single(pin => pin.Version == version);
int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++; Console.WriteLine("PASS: " + description);
}
void Reject(Action action, string description)
{
    try { action(); throw new Exception("Unexpectedly accepted: " + description); }
    catch (Exception e) when (e is ArgumentException or InvalidDataException) { Check(true, description); }
}

const string pipe = @"\\.\pipe\cc-msg-0123456789abcdef0123456789abcdef";
const string token = "0123456789abcdef0123456789abcdef"; // Synthetic in-memory input, never a file fixture.
const ulong start = 134_000_000_000_000_000;
string home = @"C:\SyntheticUser";
var profile = Pinned("2.1.292");
string session = "11111111-2222-4333-8444-555555555555";
NativeTarget Target() => new(pipe, 12345, @"C:\SyntheticPublic\claude.exe", profile.Sha256, profile.Version, start, session);
NativeMessage Message(RecipientMode mode = RecipientMode.Prompting, NativeInboundSetting inbound = NativeInboundSetting.Unspecified)
    => new(Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), session, "Synthetic message with \"quotes\"\nand text.", mode, inbound, Activity: NativeActivity.Idle);
byte[] Credential() => Encoding.UTF8.GetBytes("{\"peerToken\":\"" + token + "\",\"procStartFt\":\"" + start + "\",\"pidDomain\":\"win32:synthetic\"}");

try
{
    var standard = CanonicalPeerTarget.Parse(pipe);
    var variants = new[]
    {
        @"\\.\PIPE\CC-MSG-0123456789ABCDEF0123456789ABCDEF",
        @"//./pipe/cc-msg-0123456789abcdef0123456789abcdef",
        @"\\?\pipe\cc-msg-0123456789abcdef0123456789abcdef",
    };
    foreach (string variant in variants)
        Check(CanonicalPeerTarget.Parse(variant).CanonicalPath == standard.CanonicalPath, "native pipe spelling canonicalizes before hashing");
    string[] invalid = { @"\\other\pipe\cc-msg-0123456789abcdef0123456789abcdef", @"\\?\pipe/cc-msg-0123456789abcdef0123456789abcdef", pipe + ".", pipe + " ", pipe + @"\other", @"\\.\pipe\arbitrary", @"\\.\pipe\.." };
    foreach (string value in invalid) Reject(() => CanonicalPeerTarget.Parse(value), "malformed, remote, or non-message pipe is refused");
    var keyTarget = CanonicalPeerTarget.Derive(pipe, 12345, home);
    Check(keyTarget.FullPath == @"C:\SyntheticUser\.claude\sessions\12345." +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(standard.CanonicalPath))).ToLowerInvariant() + ".key",
        "credential target uses PID and canonical-pipe digest, with no enumeration or access");
    var local = CanonicalPeerTarget.Parse(@"\\.\pipe\LOCAL\CC-MSG-0123456789ABCDEF0123456789ABCDEF");
    Check(local.CanonicalPath == @"\\.\pipe\local\cc-msg-0123456789abcdef0123456789abcdef", "LOCAL namespace is part of the canonical digest");
    Check(CanonicalPeerTarget.Derive(pipe, 12345, home, @"C:\Synthetic\Cafe" + "\u0301").FullPath.Contains("Caf\u00e9", StringComparison.Ordinal), "config directory follows native NFC normalization");
    Reject(() => CanonicalPeerTarget.Derive(pipe, 0, home), "zero server PID cannot select credentials");
    var smoke = new SmokeTarget(Target(), keyTarget, NativeActivity.Idle);
    Check(SmokeTargets.SelectExact(session, [smoke]).Native == Target(), "pure smoke selection binds the exact recipient UUID without any filesystem access");
    Reject(() => SmokeTargets.SelectExact("22222222-2222-4333-8444-555555555555", [smoke]), "smoke target selection refuses a different recipient session");
    Reject(() => SmokeTargets.SelectExact(session, [smoke, smoke]), "ambiguous native smoke ownership is refused without choosing a fallback");
    SmokeTargets.VerifyExpected(smoke, 12345, start, keyTarget.FullPath);
    Check(true, "exact smoke preflight preserves the reviewed PID/native creation/derived-path binding");
    Reject(() => SmokeTargets.VerifyExpected(smoke, 12346, start, keyTarget.FullPath), "smoke refuses a PID different from the reviewed target");
    Reject(() => SmokeTargets.VerifyExpected(smoke, 12345, start + 1, keyTarget.FullPath), "smoke refuses a different native process incarnation");
    Reject(() => SmokeTargets.VerifyExpected(smoke, 12345, start, keyTarget.FullPath + ".other"), "smoke refuses a credential target path different from the reviewed derivation");
    Reject(() => SmokeTargets.VerifyExpected(smoke with { Activity = NativeActivity.Unknown }, 12345, start, keyTarget.FullPath), "smoke refuses unknown native activity before credential access");
    int attributeChecks = 0;
    CredentialPathPolicy.RejectReparseComponents(keyTarget.FullPath, _ => { attributeChecks++; return FileAttributes.Normal; });
    Check(attributeChecks >= 4, "pure reparse policy inspects the file and each ancestor without filesystem access");
    try
    {
        CredentialPathPolicy.RejectReparseComponents(keyTarget.FullPath,
            component => Path.GetFileName(component) == ".claude" ? FileAttributes.ReparsePoint : FileAttributes.Normal);
        throw new Exception("Reparse ancestor accepted.");
    }
    catch (IOException) { Check(true, "pure reparse policy rejects an ancestor junction supplied by an in-memory provider"); }
    Check(CredentialPathPolicy.IsBoundedCredentialFile(FileAttributes.Archive, 1, 4096), "bounded single-link ordinary credential-file metadata is accepted by the pure guard");
    Check(!CredentialPathPolicy.IsBoundedCredentialFile(FileAttributes.Archive, 2, 4096), "credential-file guard refuses hard links before reading bytes");
    Check(!CredentialPathPolicy.IsBoundedCredentialFile(FileAttributes.Directory, 1, 0), "credential-file guard refuses directory handles");
    Check(!CredentialPathPolicy.IsBoundedCredentialFile(FileAttributes.ReparsePoint, 1, 0), "credential-file guard refuses reparse handles");
    Check(!CredentialPathPolicy.IsBoundedCredentialFile(FileAttributes.Device, 1, 0), "credential-file guard refuses device handles");
    Check(!CredentialPathPolicy.IsBoundedCredentialFile(FileAttributes.Archive, 1, 4097), "credential-file guard refuses oversized native files");
    var testUser = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
    var otherUser = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
    RawSecurityDescriptor PipeSecurity(string owner, string extra = "") => new($"O:{owner}G:{testUser}D:(A;;GA;;;{testUser})(A;;GA;;;SY)(A;;GA;;;BA){extra}");
    Check(NativePipeSecurityPolicy.IsCurrentUserPipe(PipeSecurity(testUser.Value), testUser), "native pipe security binds the current user SID independently of the token's default Owner SID");
    Check(!NativePipeSecurityPolicy.IsCurrentUserPipe(PipeSecurity(otherUser.Value), testUser), "foreign pipe ownership fails before native credentials");
    Check(!NativePipeSecurityPolicy.IsCurrentUserPipe(PipeSecurity(testUser.Value, "(A;;GW;;;WD)"), testUser), "world-writable native pipes fail before credentials");
    Check(!NativePipeSecurityPolicy.IsCurrentUserPipe(PipeSecurity(testUser.Value, "(A;;GW;;;AU)"), testUser), "authenticated-user write access does not grant native pipe trust");
    Check(!NativePipeSecurityPolicy.IsCurrentUserPipe(PipeSecurity(testUser.Value, $"(A;;GW;;;{otherUser})"), testUser), "arbitrary foreign SID write grants are refused");
    Check(NativePipeSecurityPolicy.IsCurrentUserPipe(PipeSecurity(testUser.Value, "(A;;GR;;;WD)"), testUser), "read-only foreign pipe metadata access does not count as a write grant");
    var nullDacl = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, testUser, testUser, null, null);
    Check(!NativePipeSecurityPolicy.IsCurrentUserPipe(nullDacl, testUser), "null native pipe DACL never grants trust");
    string imageScratch = Path.Combine(AppContext.BaseDirectory, "scratch", "public-image-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(imageScratch);
    string publicImage = Path.Combine(imageScratch, "synthetic-public.exe");
    byte[] publicBytes = Encoding.UTF8.GetBytes("Synthetic public image attestation input, never executed.");
    File.WriteAllBytes(publicImage, publicBytes);
    using (var locked = new LockedPublicImage(publicImage))
    {
        Check(locked.Sha256 == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(publicBytes)),
            "one connection hashes its exact retained public image handle");
        locked.VerifyPath(publicImage);
        Check(true, "reinspection verifies the retained immutable image identity without another hash");
        try { using var writer = new FileStream(publicImage, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); throw new InvalidOperationException("Write sharing was allowed."); }
        catch (IOException) { Check(true, "retained public image denies writes before and through authentication"); }
        try { File.Move(publicImage, publicImage + ".moved"); throw new InvalidOperationException("Replacement was allowed."); }
        catch (IOException) { Check(true, "retained public image denies replacement while a native connection owns it"); }
        try { locked.VerifyPath(publicImage + ".different"); throw new InvalidOperationException("Different path was accepted."); }
        catch (IOException) { Check(true, "reinspection refuses a different native public image path"); }
    }
    File.WriteAllBytes(publicImage, publicBytes);
    Check(true, "disposing the native image attestation releases its write restriction");
    using (var held = new FileStream(publicImage, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
    {
        try { using var rejected = new LockedPublicImage(publicImage); throw new InvalidOperationException("Existing writer was allowed."); }
        catch (IOException) { Check(true, "an already writable public image cannot become a trusted immutable attestation"); }
    }
    File.Delete(publicImage);

    byte[] Metadata(string extra = "") => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        messagingSocketPath = pipe, sessionId = session, procStartFt = start.ToString(),
        kind = "interactive", status = "idle", entrypoint = "claude-vscode"
    }).TrimEnd('}') + extra + "}");
    var metadata = NativeSessionMetadata.Parse("12345.json", Metadata());
    Check(metadata.ProcessId == 12345 && metadata.CanonicalPipe == standard.CanonicalPath &&
        metadata.SessionId == session && metadata.StartFileTime == start && metadata.Status == "idle",
        "public numeric registry JSON parses only candidate identity and status hints in memory");
    foreach (string filename in new[] { "01.json", "1.json", "0.json", "-1.json", "2147483648.json", "12345.json/other", "12345.key" })
        Reject(() => NativeSessionMetadata.Parse(filename, Metadata()), "registry parser refuses noncanonical or protected-style filenames without accessing them");
    Reject(() => NativeSessionMetadata.Parse("12345.json", Metadata(",\"sessionId\":\"" + session + "\"")), "duplicate registry identity fields fail closed");
    Reject(() => NativeSessionMetadata.Parse("12345.json", Metadata(",\"procStart\":\"legacy\"")), "mixed registry process-start domains fail closed");
    Reject(() => NativeSessionMetadata.Parse("12345.json", new byte[NativeSessionMetadata.MaximumRecordBytes + 1]), "registry parser enforces the public 256 KiB read bound");
    Reject(() => NativeSessionMetadata.Parse("12345.json", Encoding.UTF8.GetBytes("{broken")), "malformed registry bytes are rejected without disclosure");
    byte[] alternateField = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Metadata()).Replace("procStartFt", "procStart", StringComparison.Ordinal));
    var alternate = NativeSessionMetadata.Parse("12345.json", alternateField);
    Check(alternate.StartFileTime == start && alternate.CreationField == "procStart", "metadata supports an exclusive procStart hint without guessing its timestamp domain");
    Check(alternate.StartFileTime != start + 504911232000000000UL, "a metadata creation hint cannot match .NET ticks through epoch conversion");
    Reject(() => NativeSessionMetadata.Parse("12345.json", Metadata(",\"updatedAt\":-1")), "invalid metadata freshness timestamps fail closed");
    Check(NativeSessionMetadata.Parse("12345.json", Metadata(",\"logPath\":\"untrusted-ignored-path\",\"name\":\"ignored display name\"")) == metadata,
        "registry parser ignores display and log paths without following or exporting them");
    var createdAt = DateTimeOffset.FromFileTime((long)start);
    var observedNow = createdAt.AddDays(3);
    var idleFromYesterday = metadata with { UpdatedAt = createdAt.AddDays(1), ActivityObservedAt = createdAt.AddDays(1) };
    Check(idleFromYesterday.HasPlausibleActivityTimestamp(observedNow),
        "native status changes are not mistaken for heartbeats and a verified long-idle incarnation stays discoverable");
    Check(!(metadata with { UpdatedAt = observedNow.AddMinutes(1) }).HasPlausibleActivityTimestamp(observedNow),
        "future registry timestamps do not grant native readiness");
    Check(!(metadata with { UpdatedAt = createdAt.AddMinutes(-1) }).HasPlausibleActivityTimestamp(observedNow),
        "registry activity from before native process creation is refused");
    Check(!metadata.HasPlausibleActivityTimestamp(observedNow), "missing native activity timestamp remains unknown");
    Check(!(idleFromYesterday with { ActivityObservedAt = createdAt.AddDays(2) }).HasPlausibleActivityTimestamp(observedNow),
        "activity newer than the registry publication is refused");

    byte[] valid = Credential();
    using (var parsed = PeerCredential.ParseAndConsume(valid))
        Check(valid.All(b => b == 0) && parsed.ProcessStartFileTime == start.ToString(), "successful credential parsing consumes and wipes the input bytes");
    var disposableCredential = PeerCredential.ParseAndConsume(Credential());
    // Security invariant: inspect only our synthetic allocation, not any file, process or real token.
    byte[] retainedToken = (byte[])typeof(PeerCredential).GetField("token", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(disposableCredential)!;
    disposableCredential.Dispose();
    Check(retainedToken.All(b => b == 0), "disposing credentials wipes the retained token allocation");
    string[] invalidRecords =
    {
        "{}", "[]", "{broken", "{\"peerToken\":null}",
        "{\"peerToken\":\"" + token.ToUpperInvariant() + "\"}",
        "{\"peerToken\":\"" + token + "\",\"peerToken\":\"" + token + "\"}",
        "{\"peerToken\":\"" + token + "\",\"unknown\":\"value\"}",
        "{\"peerToken\":\"" + token + "\",\"procStart\":\"legacy\",\"procStartFt\":\"1\"}",
        "{\"peerToken\":\"" + token + "\",\"procStartFt\":\"-1\"}",
        "{\"peerToken\":\"" + token + "\",\"procStart\":\"legacy\"}",
        "{\"peerToken\":\"" + token + "\",\"procStart\":\"0\"}",
        "{\"peerToken\":\"" + token + "\",\"procStart\":\"01\"}",
        "{\"peerToken\":\"" + token + "\",\"procStartFt\":\"01\"}",
        "{\"peerToken\":\"" + token + "\",\"procStartFt\":\"+1\"}",
        "{\"peerToken\":\"" + token + "\",\"procStartFt\":\" 1\"}",
        "{\"peerToken\":\"" + token + "\"} {}",
    };
    foreach (string bad in invalidRecords)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(bad);
        Reject(() => PeerCredential.ParseAndConsume(bytes), "invalid or ambiguous credential record is refused");
        Check(bytes.All(b => b == 0), "failed credential parsing wipes its input");
    }
    byte[] oversize = Enumerable.Repeat((byte)'x', 4097).ToArray();
    Reject(() => PeerCredential.ParseAndConsume(oversize), "credential parser enforces the 4096-byte limit");
    Check(oversize.All(b => b == 0), "oversized credential input is wiped");

    var identity = new NativeProcessIdentity(12345, Target().ImagePath, profile.Sha256, start, true);
    var factory = new FakeFactory(identity);
    var source = new MemorySource(Credential);
    var adapter = new NativePeerAdapter(factory, source);
    var submitted = await adapter.SendAsync(Target(), Message(), home);
    Check(submitted.Status == SubmissionStatus.SubmittedUnconfirmed && submitted.Reason == "native_write_only", "successful native writes report only SubmittedUnconfirmed");
    Check(factory.Connects == 1 && factory.Last!.Reinspections == 1 && source.Reads == 1, "server identity is verified around one credential read and one native connection");
    Check(source.Last!.All(b => b == 0), "adapter relinquishes and wipes credential source bytes");
    var frames = factory.Last!.Output.Snapshots;
    Check(frames.Count == 2, "auth precedes exactly one minimal user frame");
    using (var auth = JsonDocument.Parse(frames[0]))
        Check(auth.RootElement.GetProperty("type").GetString() == "auth" && auth.RootElement.GetProperty("token").GetString() == token, "native auth envelope uses peer token only");
    using (var user = JsonDocument.Parse(frames[1]))
    {
        var root = user.RootElement;
        Check(root.GetProperty("uuid").GetString() == Message().MessageId.ToString("D") && root.GetProperty("session_id").GetString() == session,
            "native message keeps stable caller UUID and exact recipient session");
        Check(root.GetProperty("message").GetProperty("content").GetString() == Message().Text, "message content stays data through JSON escaping");
        Check(root.EnumerateObject().Count() == 4 && !root.TryGetProperty("from_mode", out _) && !root.TryGetProperty("from", out _),
            "native message never forges origin, privilege class, selfSent, or permission mode");
    }
    Check(factory.Last.Output.BackingBuffers.All(bytes => bytes.All(b => b == 0)), "temporary outbound auth and user byte buffers are wiped after send");
    Check(factory.Last.Disposed, "native connection is disposed after submission");

    foreach (var changed in new[] { identity with { ProcessId = 7 }, identity with { StartFileTime = start + 1 }, identity with { IsCurrentUser = false }, identity with { ImagePath = @"C:\Other\claude.exe" }, identity with { ImageSha256 = new string('0', 64) } })
    {
        var fake = new FakeFactory(changed); var memory = new MemorySource(Credential);
        var result = await new NativePeerAdapter(fake, memory).SendAsync(Target(), Message(), home);
        Check(result.Status == SubmissionStatus.Refused && memory.Reads == 0 && fake.Last!.Output.Snapshots.Count == 0,
            "wrong native PID/start/user/image/version identity refuses before any credential read or write");
    }
    var changedAfterRead = new FakeFactory(identity) { Reinspection = identity with { StartFileTime = start + 1 } };
    var after = await new NativePeerAdapter(changedAfterRead, new MemorySource(Credential)).SendAsync(Target(), Message(), home);
    Check(after.Status == SubmissionStatus.Refused && changedAfterRead.Last!.Output.Snapshots.Count == 0, "identity change after credential parsing prevents token transmission");
    var staleSource = new MemorySource(() => Encoding.UTF8.GetBytes("{\"peerToken\":\"" + token + "\",\"procStartFt\":\"1\"}"));
    var staleFactory = new FakeFactory(identity);
    Check((await new NativePeerAdapter(staleFactory, staleSource).SendAsync(Target(), Message(), home)).Status == SubmissionStatus.Refused && staleFactory.Last!.Output.Snapshots.Count == 0,
        "credential creation FILETIME must match the exact connected process");
    var alternateCredentialSource = new MemorySource(() => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Credential()).Replace("procStartFt", "procStart", StringComparison.Ordinal)));
    Check((await new NativePeerAdapter(new FakeFactory(identity), alternateCredentialSource).SendAsync(Target(), Message(), home)).Status == SubmissionStatus.SubmittedUnconfirmed,
        "exclusive procStart credential is accepted only when its canonical value equals the connected native FILETIME");
    var ticksSource = new MemorySource(() => Encoding.UTF8.GetBytes("{\"peerToken\":\"" + token + "\",\"procStart\":\"" + (start + 504911232000000000UL) + "\"}"));
    var ticksFactory = new FakeFactory(identity);
    Check((await new NativePeerAdapter(ticksFactory, ticksSource).SendAsync(Target(), Message(), home)).Status == SubmissionStatus.Refused && ticksFactory.Last!.Output.Snapshots.Count == 0,
        "procStart credentials never gain authority through .NET-tick epoch conversion");

    foreach (var input in new[] { Message(RecipientMode.Unknown), Message(RecipientMode.BypassPermissions), Message(RecipientMode.Prompting, NativeInboundSetting.Hold), Message(RecipientMode.Prompting, NativeInboundSetting.Deny) })
    {
        var fake = new FakeFactory(identity); var memory = new MemorySource(Credential);
        var result = await new NativePeerAdapter(fake, memory).SendAsync(Target(), input, home);
        Check(result.Status is SubmissionStatus.Held or SubmissionStatus.Refused && fake.Connects == 0 && memory.Reads == 0,
            "native unknown/bypass/explicit hold-or-deny policy does not get silently promoted");
    }
    var acceptedFactory = new FakeFactory(identity);
    Check((await new NativePeerAdapter(acceptedFactory, new MemorySource(Credential)).SendAsync(Target(), Message(RecipientMode.BypassPermissions, NativeInboundSetting.Accept), home)).Status == SubmissionStatus.SubmittedUnconfirmed,
        "verified native explicit accept may allow a bypass recipient without altering its mode");
    foreach (var mode in new[] { RecipientMode.Unknown, RecipientMode.BypassPermissions })
    {
        var delegatedFactory = new FakeFactory(identity);
        var delegated = await new NativePeerAdapter(delegatedFactory, new MemorySource(Credential)).SendAsync(Target(),
            Message(mode) with { DelegateNativeAdmission = true }, home);
        using var auth = JsonDocument.Parse(delegatedFactory.Last!.Output.Snapshots[0]);
        using var user = JsonDocument.Parse(delegatedFactory.Last!.Output.Snapshots[1]);
        Check(delegated.Status == SubmissionStatus.SubmittedUnconfirmed && user.RootElement.EnumerateObject().Count() == 4 &&
            auth.RootElement.GetProperty("token").GetString() == token &&
            !user.RootElement.TryGetProperty("from_mode", out _) && !user.RootElement.TryGetProperty("origin", out _),
            "explicitly delegated native admission sends ordinary peer frames without asserting a privilege class or selfSent");
    }
    foreach (var activity in new[] { NativeActivity.Unknown, NativeActivity.Busy, NativeActivity.Shell, NativeActivity.Waiting })
    {
        var inactiveFactory = new FakeFactory(identity); var inactiveSource = new MemorySource(Credential);
        var result = await new NativePeerAdapter(inactiveFactory, inactiveSource).SendAsync(Target(),
            Message(RecipientMode.Unknown) with { Activity = activity, DelegateNativeAdmission = true }, home);
        Check(result.Status == SubmissionStatus.Held && result.Reason == "native_recipient_not_idle" && inactiveFactory.Connects == 0 && inactiveSource.Reads == 0,
            "delegated native admission does not promote unknown or active registry activity to idle");
    }
    foreach (var setting in new[] { NativeInboundSetting.Hold, NativeInboundSetting.Deny })
    {
        var explicitFactory = new FakeFactory(identity);
        var result = await new NativePeerAdapter(explicitFactory, new MemorySource(Credential)).SendAsync(Target(),
            Message(RecipientMode.Unknown, setting) with { DelegateNativeAdmission = true }, home);
        Check(result.Status is SubmissionStatus.Held or SubmissionStatus.Refused && explicitFactory.Connects == 0,
            "delegation retains an explicitly supplied local native hold or deny");
    }
    var unsupportedFactory = new FakeFactory(identity); var unsupportedSource = new MemorySource(Credential);
    Check((await new NativePeerAdapter(unsupportedFactory, unsupportedSource).SendAsync(Target() with { EngineVersion = "2.1.308" }, Message(), home)).Reason == "unsupported_engine_profile"
        && unsupportedFactory.Connects == 0 && unsupportedSource.Reads == 0, "unverified newer VS Code 2.1.308 profile fails closed without protected access");
    Check(EngineProfile.Find("2.1.304", profile.Sha256) is null && EngineProfile.Find("2.1.292", Pinned("2.1.304").Sha256) is null,
        "engine version and public hash pins cannot be mixed between profiles");
    var vscode = Pinned("2.1.304");
    var vscodeTarget = Target() with { EngineVersion = vscode.Version, ImageSha256 = vscode.Sha256 };
    var vscodeFactory = new FakeFactory(identity with { ImageSha256 = vscode.Sha256 });
    Check((await new NativePeerAdapter(vscodeFactory, new MemorySource(Credential)).SendAsync(vscodeTarget, Message(), home)).Status == SubmissionStatus.SubmittedUnconfirmed,
        "independently verified VS Code 2.1.304 profile uses the same minimal native peer protocol in a fake transport");
    var updatedVscode = Pinned("2.1.306");
    var updatedTarget = Target() with { EngineVersion = updatedVscode.Version, ImageSha256 = updatedVscode.Sha256 };
    var updatedFactory = new FakeFactory(identity with { ImageSha256 = updatedVscode.Sha256 });
    Check((await new NativePeerAdapter(updatedFactory, new MemorySource(Credential)).SendAsync(updatedTarget, Message(), home)).Status == SubmissionStatus.SubmittedUnconfirmed,
        "independently verified VS Code 2.1.306 profile retains the peer-only native protocol through a fake transport");
    Check(EngineProfile.Find(updatedVscode.Version, vscode.Sha256) is null && EngineProfile.Find(vscode.Version, updatedVscode.Sha256) is null,
        "updated native profiles cannot borrow another client version's hash pin");
    Check(EngineProfile.IsSupportedPublicVersion("2.1.306.0") && EngineProfile.IsSupportedPublicVersion("2.1.304") && EngineProfile.IsSupportedPublicVersion("2.1.292.0"),
        "cheap public-version preflight accepts the exact verified native release spellings for subsequent hash validation");
    foreach (string? version in new string?[] { null, "", "2.1", "2.1.308.0", "2.1.306.1", "unrecognized" })
        Check(!EngineProfile.IsSupportedPublicVersion(version), "unverified or malformed public version metadata cannot trigger native image trust");
    var broken = new FakeFactory(identity) { FailUserWrite = true };
    Check((await new NativePeerAdapter(broken, new MemorySource(Credential)).SendAsync(Target(), Message(), home)).Reason == "native_write_uncertain",
        "a write failure after user submission begins reports uncertainty, never a delivery ACK");
    Check(broken.Last!.Output.BackingBuffers.All(bytes => bytes.All(b => b == 0)), "exceptional outbound paths also wipe temporary byte buffers");
    var largeFactory = new FakeFactory(identity);
    var largeMessage = Message() with { Text = new string('x', NativePeerAdapter.MaximumEnvelopeTextBytes) };
    Check((await new NativePeerAdapter(largeFactory, new MemorySource(Credential)).SendAsync(Target(), largeMessage, home)).Status == SubmissionStatus.SubmittedUnconfirmed,
        "bounded128KiB formatted native envelope is sent whole without truncation through a fake transport");
    using (var largeFrame = JsonDocument.Parse(largeFactory.Last!.Output.Snapshots[1]))
        Check(largeFrame.RootElement.GetProperty("message").GetProperty("content").GetString() == largeMessage.Text,
            "maximum native envelope content retains every character");
    var tooLargeFactory = new FakeFactory(identity); var tooLargeSource = new MemorySource(Credential);
    Check((await new NativePeerAdapter(tooLargeFactory, tooLargeSource).SendAsync(Target(), largeMessage with { Text = largeMessage.Text + "x" }, home)).Status == SubmissionStatus.Refused &&
        tooLargeFactory.Connects == 0 && tooLargeSource.Reads == 0, "oversized native envelope refuses before transport or credentials");
    Console.WriteLine("RESULT=PASS assertions=" + checks);
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); Console.WriteLine("RESULT=FAIL"); return 1; }

sealed class MemorySource(Func<byte[]> bytes) : IPeerCredentialSource
{
    internal int Reads;
    internal byte[]? Last;
    public byte[] Read(CredentialTarget target) { Reads++; return Last = bytes(); }
}

sealed class FakeFactory(NativeProcessIdentity identity) : INativePeerConnectionFactory
{
    internal int Connects;
    internal FakeConnection? Last;
    internal NativeProcessIdentity? Reinspection;
    internal bool FailUserWrite;
    public Task<INativePeerConnection> ConnectAsync(string pipeName, CancellationToken cancellation)
    {
        Connects++; Last = new FakeConnection(identity, Reinspection ?? identity, FailUserWrite);
        return Task.FromResult<INativePeerConnection>(Last);
    }
}

sealed class FakeConnection(NativeProcessIdentity identity, NativeProcessIdentity reinspection, bool fail) : INativePeerConnection
{
    internal readonly CaptureStream Output = new(fail);
    internal int Reinspections;
    internal bool Disposed;
    public bool EnforcesCurrentUser => true;
    public NativeProcessIdentity Identity => identity;
    public Stream Outbound => Output;
    public NativeProcessIdentity Reinspect() { Reinspections++; return reinspection; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

sealed class CaptureStream(bool failUser) : Stream
{
    internal readonly List<byte[]> Snapshots = [];
    internal readonly List<byte[]> BackingBuffers = [];
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> value, CancellationToken cancellation = default)
    {
        if (MemoryMarshal.TryGetArray(value, out ArraySegment<byte> segment) && segment.Array is not null) BackingBuffers.Add(segment.Array);
        Snapshots.Add(value.ToArray());
        if (failUser && Snapshots.Count == 2) throw new IOException("Synthetic partial write.");
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
    public override void SetLength(long n) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
