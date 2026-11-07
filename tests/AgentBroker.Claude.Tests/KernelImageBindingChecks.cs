using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentBroker.ClaudeAdapter;
using Microsoft.Win32.SafeHandles;

// All paths, process/image leases, credentials and writes here are synthetic and in memory.
// Only the OS leaves are fake: the production connection constructs and reinspects the production binding.
internal static class KernelImageBindingChecks
{
    private const string L = "KernelImageBindingChecks.cs: ";
    private const string Image = @"C:\SyntheticPublic\claude.exe";
    private const string Displaced = @"C:\SyntheticDisplaced\claude.exe";
    private const string CaseVariant = @"C:\SyntheticPublic\CLAUDE.exe";
    private const string Pipe = @"\\.\pipe\cc-msg-0123456789abcdef0123456789abcdef";
    private const string Session = "11111111-2222-4333-8444-555555555555";
    private const string TolerantHash = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
    private const ulong Start = 134_000_000_000_000_000;

    internal static async Task RunAsync(Action<bool, string> check)
    {
        KernelQuery(check);
        var pin = EngineProfile.Verified[0];
        var target = new NativeTarget(Pipe, 12345, Image, pin.Sha256, pin.Version, Start, Session);

        var duringAcquire = new Factory(pin.Sha256, [Image, Displaced]);
        var unread = new Credentials(duringAcquire);
        var refused = await new NativePeerAdapter(duringAcquire, unread).SendAsync(target, Message(), @"C:\SyntheticUser");
        check(IsPathRefusal(refused) && unread.Reads == 0 && duringAcquire.Output.Frames.Count == 0,
            L + "post-lease kernel path disagreement refuses by name before credentials or authentication");
        check(duringAcquire.Process.PathReads == 2 && duringAcquire.Acquires == 1 &&
            duringAcquire.Events.IndexOf("image.acquire") > duringAcquire.Events.IndexOf("kernel.path.1") &&
            duringAcquire.Events.IndexOf("image.acquire") < duringAcquire.Events.IndexOf("kernel.path.2"),
            L + "WITNESS: the production binding queries the kernel on both sides of image acquisition");
        check(DisposedOnce(duringAcquire),
            L + "post-lease refusal releases the acquired process and image leases exactly once");

        var wrongLease = new Factory(pin.Sha256, [Image, Displaced]) { LeasePath = Displaced };
        var wrongLeaseCredentials = new Credentials(wrongLease);
        var wrongLeaseResult = await new NativePeerAdapter(wrongLease, wrongLeaseCredentials).SendAsync(target, Message(), @"C:\SyntheticUser");
        check(IsPathRefusal(wrongLeaseResult) && wrongLeaseCredentials.Reads == 0 && wrongLease.Output.Frames.Count == 0,
            L + "an image lease for a different initial kernel path refuses before credentials");
        check(DisposedOnce(wrongLease),
            L + "initial image-lease disagreement releases both owned leases on constructor failure");

        var beforeVerify = new Factory(pin.Sha256, [Image, Image, Displaced]);
        await ReinspectionRefusal(check, target, beforeVerify, expectedReads: 3, expectedVerifications: 0,
            "before held-path verification");
        var afterVerify = new Factory(pin.Sha256, [Image, Image, Image, Displaced]);
        await ReinspectionRefusal(check, target, afterVerify, expectedReads: 4, expectedVerifications: 1,
            "after held-path verification");

        var caseAcquire = new Factory(pin.Sha256, [Image, CaseVariant]);
        var caseAcquireCredentials = new Credentials(caseAcquire);
        var caseAcquireResult = await new NativePeerAdapter(caseAcquire, caseAcquireCredentials).SendAsync(target, Message(), @"C:\SyntheticUser");
        check(IsPathRefusal(caseAcquireResult) && caseAcquireCredentials.Reads == 0 && caseAcquire.Output.Frames.Count == 0 &&
            DisposedOnce(caseAcquire),
            L + "case-variant post-lease kernel path refuses by ordinal identity before credentials");
        var caseReinspect = new Factory(pin.Sha256, [Image, Image, CaseVariant]);
        var caseReinspectCredentials = new Credentials(caseReinspect);
        var caseReinspectResult = await new NativePeerAdapter(caseReinspect, caseReinspectCredentials).SendAsync(target, Message(), @"C:\SyntheticUser");
        check(IsPathRefusal(caseReinspectResult) && caseReinspectCredentials.Reads == 1 && caseReinspectCredentials.BytesWiped &&
            caseReinspect.Output.Frames.Count == 0 && caseReinspect.Process.PathReads == 3 && DisposedOnce(caseReinspect),
            L + "case-variant reinspection kernel path refuses by ordinal identity before authentication");

        var pinned = new Factory(pin.Sha256, [Image]);
        var pinnedCredentials = new Credentials(pinned);
        var neverAsked = new Trust(pinned, accepts: false);
        var sentPinned = await new NativePeerAdapter(pinned, pinnedCredentials, neverAsked).SendAsync(target, Message(), @"C:\SyntheticUser");
        check(sentPinned.Status == SubmissionStatus.SubmittedUnconfirmed && TwoOrderedFrames(pinned),
            L + "matching kernel/image identity preserves pinned authentication then one user frame");
        check(pinnedCredentials.Reads == 1 && pinnedCredentials.BytesWiped && pinnedCredentials.LeasesAlive && neverAsked.Calls == 0,
            L + "pinned agreement reads one synthetic credential with leases held and never asks tolerant trust");
        check(CompleteBindingWitness(pinned),
            L + "WITNESS: pinned writes use the production four-query binding with both leases alive inside each write");
        check(DisposedOnce(pinned) && DisposedAfterWrites(pinned),
            L + "pinned success releases process and image leases only after both frame writes");
        await pinned.LastConnection!.DisposeAsync();
        check(DisposedOnce(pinned),
            L + "disposing the actual production connection again does not release process image or pipe twice");

        var tolerantTarget = target with { EngineVersion = "2.1.999", ImageSha256 = TolerantHash };
        var tolerant = new Factory(TolerantHash, [Image]);
        var tolerantCredentials = new Credentials(tolerant);
        var accepting = new Trust(tolerant, accepts: true);
        var sentTolerant = await new NativePeerAdapter(tolerant, tolerantCredentials, accepting).SendAsync(tolerantTarget, Message(), @"C:\SyntheticUser");
        check(sentTolerant.Status == SubmissionStatus.SubmittedUnconfirmed && TwoOrderedFrames(tolerant),
            L + "matching kernel/image identity preserves update-tolerant authentication then one user frame");
        check(tolerantCredentials.Reads == 1 && tolerantCredentials.BytesWiped && tolerantCredentials.LeasesAlive &&
            accepting.Calls == 1 && tolerant.Events.IndexOf("publisher") < tolerant.Events.IndexOf("credential.read"),
            L + "tolerant agreement verifies the publisher before one synthetic credential read with leases held");
        check(CompleteBindingWitness(tolerant),
            L + "WITNESS: tolerant writes use the production four-query binding with both leases alive inside each write");
        check(DisposedOnce(tolerant) && DisposedAfterWrites(tolerant),
            L + "tolerant success releases process and image leases only after both frame writes");

        var strict = new Factory(TolerantHash, [Image]);
        var strictCredentials = new Credentials(strict);
        var strictResult = await new NativePeerAdapter(strict, strictCredentials).SendAsync(tolerantTarget, Message(), @"C:\SyntheticUser");
        check(strictResult.Reason == "unsupported_engine_profile" && strict.Connects == 0 && strictCredentials.Reads == 0 &&
            strict.Output.Frames.Count == 0,
            L + "strict mode still refuses an unpinned image before invoking the kernel/image seam");
    }

    private static void KernelQuery(Action<bool, string> check)
    {
        NativeTokenQueries(check);
        var ops = new KernelOperations();
        bool queried = false;
        string? first = null, second = null;
        bool metadata = false;
        try
        {
            using var process = new KernelProcessImage(12345, ops);
            first = process.ImagePath; second = process.ImagePath; queried = true;
            metadata = process.ProcessId == 12345 && process.StartFileTime == Start && !process.HasExited && process.IsCurrentUser;
        }
        // A mutant that bypasses the injectable query calls the real API with this deliberately invalid handle.
        // Convert that expected native error to this specific witness failure, rather than an unlabeled outer crash.
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or ArgumentException) { }
        check(queried && first == Image && second == Image && ops.Queries == 2 && ops.ExactHandle && ops.QueryFlagsZero,
            L + "WITNESS: each ImagePath read invokes the supplied kernel query on the same process handle with flags zero");
        check(metadata && ops.Opens == 1 && ops.ProcessId == 12345 && ops.Access == 0x1000 &&
            ops.ElevationReads == 1 && ops.StartReads == 1 && ops.ExitReads == 1 && ops.UserReads == 1 && ops.ExactHandle,
            L + "production kernel construction opens limited-query only and reads token and metadata from that retained handle");
        check(ops.Handle.IsClosed,
            L + "production kernel success closes its retained process handle after all metadata reads");
        var deniedQuery = new KernelOperations { QueryAllowed = false };
        bool refused = false;
        using (var process = new KernelProcessImage(12345, deniedQuery))
        {
            try { _ = process.ImagePath; }
            catch (Win32Exception) { refused = true; }
        }
        check(refused && deniedQuery.Queries == 1 && deniedQuery.Handle.IsClosed,
            L + "a refused kernel image query fails closed without a MainModule or pathname fallback");

        var elevated = new KernelOperations { Elevated = true };
        bool elevatedRefused = false;
        try { using var process = new KernelProcessImage(12345, elevated); }
        catch (Win32Exception error) when (error.NativeErrorCode == 5) { elevatedRefused = true; }
        check(elevatedRefused && elevated.Opens == 1 && elevated.Access == 0x1000 && elevated.ElevationReads == 1 &&
            elevated.Queries == 0 && elevated.StartReads == 0 && elevated.Handle.IsClosed,
            L + "limited-query open success with an elevated token is refused by access-denied before image metadata and closes the handle");

        check(KernelProcessImage.NormalizePath(@"\\?\C:\SyntheticPublic\claude.exe") == Image &&
            KernelProcessImage.NormalizePath(Image) == Image,
            L + "kernel path normalization strips a local extended prefix and preserves an ordinary local executable path");
        bool invalidRefused = true;
        foreach (string invalid in new[] { @"\\server\share\claude.exe", @"\\?\UNC\server\share\claude.exe", @"relative\claude.exe", @"C:\SyntheticPublic\claude.txt" })
        {
            try { _ = KernelProcessImage.NormalizePath(invalid); invalidRefused = false; }
            catch (IOException) { }
        }
        check(invalidRefused,
            L + "kernel path normalization refuses UNC extended-UNC relative and non-executable paths");
    }

    private static void NativeTokenQueries(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: NATIVE_TOKEN_ELEVATION_PRODUCT_EXECUTED");
        foreach (string scenario in new[] { "OPEN_FAILURE", "QUERY_FAILURE", "SHORT_READ", "OVERSIZED_READ", "ELEVATED" })
        {
            var ops = new KernelOperations();
            var tokens = new TokenOperations(ops, scenario);
            ops.NativeElevation = new WindowsKernelProcessOperations(tokens.Open, tokens.Query);
            using var retainedCleanup = ops.Handle;
            using var tokenCleanup = tokens.Token;
            bool refused = false;
            try { using var process = new KernelProcessImage(12345, ops); }
            catch (Win32Exception error) when (error.NativeErrorCode == 5) { refused = true; }
            check(refused && ops.Queries == 0 && ops.StartReads == 0,
                L + "TOKEN_NATIVE_" + scenario + "_FAIL_CLOSED: actual native token flow refuses before process metadata");
            check(ops.Handle.IsClosed && tokens.Token.IsClosed,
                L + "TOKEN_NATIVE_" + scenario + "_DISPOSES: rejection closes the retained process and any returned token wrapper");
            check(tokens.Opens == 1 && tokens.ExactProcess && tokens.Access == 0x0008 &&
                tokens.Queries == (scenario == "OPEN_FAILURE" ? 0 : 1) && tokens.ExactToken &&
                (scenario == "OPEN_FAILURE" || tokens.InformationClass == 20 && tokens.Bytes == sizeof(uint)),
                L + "TOKEN_NATIVE_" + scenario + "_SHAPE: TOKEN_QUERY and one DWORD TokenElevation use the same retained handles");
        }
        var unelevated = new KernelOperations();
        var success = new TokenOperations(unelevated, "UNELEVATED");
        unelevated.NativeElevation = new WindowsKernelProcessOperations(success.Open, success.Query);
        using var successTokenCleanup = success.Token;
        using (var process = new KernelProcessImage(12345, unelevated))
            check(success.Opens == 1 && success.Queries == 1 && success.ExactProcess && success.ExactToken &&
                success.Access == 0x0008 && success.InformationClass == 20 && success.Bytes == sizeof(uint) &&
                success.Token.IsClosed && !unelevated.Handle.IsClosed,
                L + "TOKEN_NATIVE_UNELEVATED: exact successful DWORD query closes its token while retaining the process");
        check(unelevated.Handle.IsClosed,
            L + "TOKEN_NATIVE_SUCCESS_DISPOSES: accepted process ownership lasts until its normal disposal");
    }

    private static async Task ReinspectionRefusal(Action<bool, string> check, NativeTarget target, Factory factory,
        int expectedReads, int expectedVerifications, string stage)
    {
        var credentials = new Credentials(factory);
        var result = await new NativePeerAdapter(factory, credentials).SendAsync(target, Message(), @"C:\SyntheticUser");
        check(IsPathRefusal(result) && factory.Output.Frames.Count == 0,
            L + "kernel path change " + stage + " at reinspection refuses by name before the auth frame");
        check(credentials.Reads == 1 && credentials.BytesWiped && credentials.LeasesAlive,
            L + "reinspection refusal " + stage + " consumes exactly one synthetic credential and wipes its bytes");
        check(factory.Process.PathReads == expectedReads && factory.Lease?.Verifications == expectedVerifications,
            L + "WITNESS: reinspection " + stage + " executes the expected production kernel-query boundary");
        check(DisposedOnce(factory),
            L + "reinspection refusal " + stage + " releases process and image leases exactly once");
    }

    private static bool IsPathRefusal(SubmissionResult result)
        => result.Status == SubmissionStatus.Refused && result.Reason == "native_image_path_mismatch";
    private static bool DisposedOnce(Factory factory)
        => factory.Process.Disposals == 1 && factory.Lease?.Disposals == 1 && factory.Pipe.Disposals == 1;
    private static bool DisposedAfterWrites(Factory factory)
        => factory.Events.IndexOf("process.dispose") > factory.Events.IndexOf("write.2") &&
            factory.Events.IndexOf("image.dispose") > factory.Events.IndexOf("write.2");
    private static bool CompleteBindingWitness(Factory factory)
        => factory.Process.PathReads == 4 && factory.Lease?.Verifications == 1 &&
            factory.Output.LeasesAlive.SequenceEqual([true, true]) && factory.Pipe.PidReads == 4 && factory.Pipe.SecurityChecks == 2 &&
            factory.Events.Contains("connection.identity_observed") &&
            factory.Events.IndexOf("connection.identity_observed") < factory.Events.IndexOf("credential.read");
    private static bool TwoOrderedFrames(Factory factory)
    {
        if (factory.Output.Frames.Count != 2) return false;
        using var auth = JsonDocument.Parse(factory.Output.Frames[0]);
        using var user = JsonDocument.Parse(factory.Output.Frames[1]);
        return auth.RootElement.GetProperty("type").GetString() == "auth" &&
            user.RootElement.GetProperty("type").GetString() == "user";
    }
    private static NativeMessage Message() => new(Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        Session, "Synthetic kernel-bound peer delivery.", RecipientMode.Prompting, Activity: NativeActivity.Idle);

    private sealed class KernelOperations : IKernelProcessOperations
    {
        internal readonly SafeProcessHandle Handle = new(new IntPtr(1234), ownsHandle: false);
        internal int Opens, ProcessId, Queries, ElevationReads, StartReads, ExitReads, UserReads;
        internal uint Access;
        internal bool ExactHandle = true, QueryFlagsZero = true, QueryAllowed = true, Elevated;
        internal WindowsKernelProcessOperations? NativeElevation;
        public SafeProcessHandle Open(int processId, uint access)
        { Opens++; ProcessId = processId; Access = access; return Handle; }
        private void Observe(SafeProcessHandle handle) => ExactHandle &= ReferenceEquals(handle, Handle) && !handle.IsClosed;
        public bool QueryImagePath(SafeProcessHandle handle, uint flags, StringBuilder path, ref uint length)
        {
            Observe(handle); Queries++; QueryFlagsZero &= flags == 0;
            if (!QueryAllowed) { Marshal.SetLastPInvokeError(5); return false; }
            path.Clear().Append(Image); length = (uint)Image.Length; return true;
        }
        public ulong StartFileTime(SafeProcessHandle handle) { Observe(handle); StartReads++; return Start; }
        public uint ExitCode(SafeProcessHandle handle) { Observe(handle); ExitReads++; return 259; }
        public bool IsCurrentUser(SafeProcessHandle handle) { Observe(handle); UserReads++; return true; }
        public bool IsElevated(SafeProcessHandle handle) { Observe(handle); ElevationReads++; return NativeElevation?.IsElevated(handle) ?? Elevated; }
    }

    private sealed class TokenOperations(KernelOperations process, string scenario)
    {
        // Invalid synthetic wrapper owns no OS token; these delegates are the only leaves used by the product method.
        internal readonly SafeAccessTokenHandle Token = new(IntPtr.Zero);
        internal int Opens, Queries, InformationClass;
        internal uint Access, Bytes;
        internal bool ExactProcess = true, ExactToken = true;
        internal bool Open(SafeProcessHandle handle, uint access, out SafeAccessTokenHandle token)
        {
            Opens++; Access = access; ExactProcess &= ReferenceEquals(handle, process.Handle) && !handle.IsClosed;
            token = Token; Marshal.SetLastPInvokeError(87);
            return scenario != "OPEN_FAILURE";
        }
        internal bool Query(SafeAccessTokenHandle token, int informationClass, out uint information, uint bytes, out uint returned)
        {
            Queries++; InformationClass = informationClass; Bytes = bytes;
            ExactToken &= ReferenceEquals(token, Token) && !token.IsClosed;
            information = scenario == "ELEVATED" ? 2u : 0u;
            returned = scenario == "SHORT_READ" ? 3u : scenario == "OVERSIZED_READ" ? 8u : sizeof(uint);
            Marshal.SetLastPInvokeError(87);
            return scenario != "QUERY_FAILURE";
        }
    }

    private sealed class Process(List<string> events, string[] paths) : INativeImageProcess
    {
        internal int PathReads, Disposals;
        internal bool Disposed;
        public int ProcessId => 12345;
        public ulong StartFileTime => Start;
        public bool IsCurrentUser => true;
        public bool HasExited => false;
        public string ImagePath
        {
            get
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                events.Add("kernel.path." + ++PathReads);
                return paths[Math.Min(PathReads - 1, paths.Length - 1)];
            }
        }
        public void Dispose() { Disposals++; Disposed = true; events.Add("process.dispose"); }
    }
    private sealed class ImageLease(List<string> events, string path, string hash) : INativeImageLease
    {
        internal int Verifications, Disposals;
        internal bool Disposed;
        public string ImagePath => path;
        public string Sha256 => hash;
        public void VerifyPath(string observedPath)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Verifications++; events.Add("image.verify");
            if (!string.Equals(observedPath, path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Synthetic held image path mismatch.");
        }
        public void Dispose() { Disposals++; Disposed = true; events.Add("image.dispose"); }
    }
    private sealed class Factory : INativePeerConnectionFactory
    {
        internal readonly List<string> Events = [];
        internal readonly Process Process;
        internal readonly Output Output;
        internal readonly PipeLease Pipe;
        internal ImageLease? Lease;
        internal INativePeerConnection? LastConnection;
        internal string LeasePath = Image;
        internal int Acquires, Connects;
        private readonly string hash;
        internal Factory(string hash, string[] paths)
        {
            this.hash = hash; Process = new(Events, paths); Output = new(this); Pipe = new(this);
        }
        internal bool LeasesAlive => !Process.Disposed && Lease is { Disposed: false } && Pipe.Disposals == 0;
        public async Task<INativePeerConnection> ConnectAsync(string pipeName, CancellationToken cancellation)
        {
            Connects++;
            var factory = new WindowsNativePeerConnectionFactory(
                (_, token) => { token.ThrowIfCancellationRequested(); Events.Add("pipe.connect"); return Task.FromResult<INativePipeLease>(Pipe); },
                pid => { Events.Add("process.open"); if (pid != Process.ProcessId) throw new IOException("Synthetic PID mismatch."); return Process; },
                path => { Acquires++; Events.Add("image.acquire"); return Lease = new ImageLease(Events, LeasePath, hash); },
                stage => Events.Add("connection." + stage));
            return LastConnection = await factory.ConnectAsync(pipeName, cancellation);
        }
    }
    private sealed class PipeLease(Factory factory) : INativePipeLease
    {
        internal int PidReads, SecurityChecks, Disposals;
        public int ServerProcessId { get { PidReads++; factory.Events.Add("pipe.pid"); return 12345; } }
        public Stream Outbound => factory.Output;
        public void ValidateSecurity() { SecurityChecks++; factory.Events.Add("pipe.security"); }
        public void Dispose() { Disposals++; factory.Events.Add("pipe.dispose"); }
    }
    private sealed class Credentials(Factory factory) : IPeerCredentialSource
    {
        internal int Reads;
        internal bool LeasesAlive;
        private byte[]? last;
        internal bool BytesWiped => last is not null && last.All(value => value == 0);
        public byte[] Read(CredentialTarget target)
        {
            Reads++; factory.Events.Add("credential.read"); LeasesAlive = factory.LeasesAlive;
            return last = Encoding.UTF8.GetBytes("{\"peerToken\":\"0123456789abcdef0123456789abcdef\",\"procStartFt\":\"" + Start + "\"}");
        }
    }
    private sealed class Trust(Factory factory, bool accepts) : IPublisherImageTrust
    {
        internal int Calls;
        public bool IsExpectedPublisher(string imagePath, string imageSha256)
        {
            Calls++; factory.Events.Add("publisher");
            return accepts && imagePath == Image && imageSha256 == TolerantHash && factory.LeasesAlive;
        }
    }
    private sealed class Output(Factory factory) : Stream
    {
        internal readonly List<byte[]> Frames = [];
        internal readonly List<bool> LeasesAlive = [];
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> value, CancellationToken cancellation = default)
        {
            LeasesAlive.Add(factory.LeasesAlive); Frames.Add(value.ToArray());
            factory.Events.Add("write." + Frames.Count);
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellation) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
