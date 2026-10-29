using AgentBroker.Core;
using AgentBroker.ClaudeAdapter;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentBroker.Worker.Adapters;

public enum ClaudeDiscoveryProvenance { Unverified, ExactPinned, PublisherSignedObservation }

public sealed record ClaudeNativeCandidate(NativeTarget Target, string? Status)
{
    // The historical constructor grants no authority to an unpinned target.
    public ClaudeDiscoveryProvenance Provenance { get; init; } =
        EngineProfile.Find(Target.EngineVersion, Target.ImageSha256) is not null
            ? ClaudeDiscoveryProvenance.ExactPinned : ClaudeDiscoveryProvenance.Unverified;
    public bool CanDeliver => Provenance == ClaudeDiscoveryProvenance.ExactPinned &&
        EngineProfile.Find(Target.EngineVersion, Target.ImageSha256) is not null;
    public Guid NativeSessionIncarnation
    {
        get
        {
            string pipe = CanonicalPeerTarget.Parse(Target.PipePath).CanonicalPath;
            string identity = string.Create(CultureInfo.InvariantCulture, $"claude-peer-v1|{Target.ProcessId}|{Target.StartFileTime}|{pipe}|{Target.SessionId}");
            return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
        }
    }
}

public interface IClaudeNativeProvider : IAsyncDisposable
{
    IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => Array.Empty<AdapterDiscoveryDiagnostic>();
    Task<IReadOnlyList<ClaudeNativeCandidate>> DiscoverAsync(CancellationToken token);
    Task<ClaudeNativeCandidate?> RefreshAsync(int processId, CancellationToken token);
    Task<SubmissionResult> SubmitAsync(NativeTarget target, NativeMessage message, CancellationToken token);
}

/// <summary>Registry hints plus unchanged native peer admission. Credentials are opt-in and Submit-only.</summary>
public sealed class ClaudeNativeAdapter : ILocalAgentAdapter
{
    private readonly IClaudeNativeProvider provider;
    private readonly bool enablePeerCredentials;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<SessionAddress, ClaudeNativeCandidate> candidates = [];
    // The last discovery pass's process bindings, replaced whole so whoami reads it without the async gate.
    private volatile (SessionAddress Address, long ProcessId, ulong StartFileTime)[] processIndex = [];
    private bool disposed;
    public string Id => "claude-native";
    public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => provider.DiscoveryDiagnostics;

    public ClaudeNativeAdapter(bool enablePeerCredentials = false)
        : this(new ClaudeNativeProvider(enablePeerCredentials), enablePeerCredentials) { }
    public ClaudeNativeAdapter(IClaudeNativeProvider provider, bool enablePeerCredentials = false, TimeProvider? clock = null)
    { this.provider = provider; this.enablePeerCredentials = enablePeerCredentials; this.clock = clock ?? TimeProvider.System; }

    public async Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
    {
        if (brokerId == Guid.Empty) throw new BrokerException("invalid_session", "Broker identity is required.");
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var rows = await provider.DiscoverAsync(token);
            if (rows.Count > 32) throw new BrokerException("session_limit", "Native Claude directory exceeded its bound.");
            var found = new Dictionary<SessionAddress, ClaudeNativeCandidate>();
            var result = new List<BrokerSession>();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (!Valid(row)) continue;
                var address = new SessionAddress(brokerId, "claude:" + row.Target.SessionId, row.NativeSessionIncarnation);
                if (!found.TryAdd(address, row)) continue;
                result.Add(new(address, "Claude " + row.Target.SessionId[..8], Id, enablePeerCredentials && row.CanDeliver
                    ? SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply : SessionCapabilities.None,
                    Activity(row.Status), clock.GetUtcNow().AddSeconds(30)));
            }
            candidates.Clear(); foreach (var pair in found) candidates.Add(pair.Key, pair.Value);
            processIndex = found.Select(pair => (pair.Key, (long)pair.Value.Target.ProcessId, pair.Value.Target.StartFileTime)).ToArray();
            return result;
        }
        finally { gate.Release(); }
    }

    public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => SubmitAsync(message, null, token);

    // One Claude process hosts one session, so the process ID and exact OS creation time name it; a reused ID never matches.
    public SessionAddress? FindSessionByProcess(int processId, ulong startFileTime)
    {
        var matches = processIndex.Where(entry => entry.ProcessId == processId && entry.StartFileTime == startFileTime).ToArray();
        return matches.Length == 1 ? matches[0].Address : null;
    }

    public async Task<AdapterResult> SubmitAsync(BrokerMessage message, PeerSenderInfo? sender, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!enablePeerCredentials) return new(AdapterAdmission.Refused, "claude_credentials_disabled");
            if (!candidates.TryGetValue(message.Recipient, out var previous)) return Deferred("native_session_unavailable");
            // A signature permits observation only. Refuse before refresh, transport or credentials.
            if (!previous.CanDeliver) return new(AdapterAdmission.Refused, "claude_observation_only");
            ClaudeNativeCandidate? fresh;
            try { fresh = await provider.RefreshAsync(previous.Target.ProcessId, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            { return Deferred("native_metadata_unavailable"); }
            if (fresh is null || !Valid(fresh) || fresh.NativeSessionIncarnation != message.Recipient.Incarnation)
                return Deferred("native_owner_changed");
            if (!fresh.CanDeliver) return new(AdapterAdmission.Refused, "claude_observation_only");
            if (fresh.Status != "idle") return Deferred("native_recipient_not_idle");
            // Immediate metadata refresh occurred before any credential handling. The native gate is
            // authoritative for actual mode/settings and the idle snapshot can race with GUI activity.
            var native = new NativeMessage(message.Id, fresh.Target.SessionId, PeerMessageFormatter.Format(message, sender), RecipientMode.Unknown,
                DelegateNativeAdmission: true, Activity: NativeActivity.Idle);
            try
            {
                var result = await provider.SubmitAsync(fresh.Target, native, token);
                return result.Status switch
                {
                    SubmissionStatus.SubmittedUnconfirmed => new(AdapterAdmission.Submitted, "native_write_unconfirmed"),
                    SubmissionStatus.Held => Deferred("native_not_submitted"),
                    _ => new(AdapterAdmission.Refused, "native_admission_refused"),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            { return new(AdapterAdmission.Uncertain, "native_submission_uncertain"); }
        }
        finally { gate.Release(); }
    }

    private static bool Valid(ClaudeNativeCandidate candidate) => candidate.Target.ProcessId > 1 && candidate.Target.StartFileTime > 0 &&
        Guid.TryParseExact(candidate.Target.SessionId, "D", out Guid id) && id != Guid.Empty && id.ToString("D") == candidate.Target.SessionId &&
        (candidate.CanDeliver || candidate.Provenance == ClaudeDiscoveryProvenance.PublisherSignedObservation &&
            candidate.Target.ImageSha256.Length == 64 && candidate.Target.ImageSha256.All(Uri.IsHexDigit));
    private static SessionActivity Activity(string? status) => status switch
    {
        "idle" => SessionActivity.Idle,
        "busy" or "shell" => SessionActivity.Busy,
        "waiting" => SessionActivity.ApprovalRequired,
        _ => SessionActivity.Unknown,
    };
    private static AdapterResult Deferred(string reason) => new(AdapterAdmission.DeferredNotSubmitted, reason);
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; candidates.Clear(); processIndex = []; await provider.DisposeAsync(); }
        finally { gate.Release(); }
    }
}
