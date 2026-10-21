using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBroker.ClaudeAdapter;

/// <summary>Writes an authenticated native peer message. No native delivery ACK has been established.</summary>
public sealed class NativePeerAdapter(INativePeerConnectionFactory connections, IPeerCredentialSource credentials)
{
    // Core raw text stays16KiB. The formatted JSON peer envelope can expand ASCII controls sixfold.
    // Verified 2.1.304 native ingress bounds each JSONL line to1MiB chars; this byte bound is stricter.
    public const int MaximumEnvelopeTextBytes = 128 * 1024;
    public async Task<SubmissionResult> SendAsync(NativeTarget target, NativeMessage message,
        string userHome, string? configDirectory = null, CancellationToken cancellation = default)
    {
        if (EngineProfile.Find(target.EngineVersion, target.ImageSha256) is null)
            return Refused("unsupported_engine_profile");
        if (target.ProcessId <= 0 || target.StartFileTime == 0 || !Path.IsPathFullyQualified(target.ImagePath) ||
            !target.ImagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !CanonicalSession(target.SessionId) || message.MessageId == Guid.Empty || message.SessionId != target.SessionId ||
            string.IsNullOrWhiteSpace(message.Text) || Encoding.UTF8.GetByteCount(message.Text) > MaximumEnvelopeTextBytes ||
            message.Text.Contains('\0') || !Enum.IsDefined(message.RecipientMode) || !Enum.IsDefined(message.InboundSetting) || !Enum.IsDefined(message.Activity))
            return Refused("invalid_native_message");
        // Activity comes from fresh local registry metadata, never a remote message's claims. It is a hint,
        // not an atomic reservation: the native owner retains final queue and permission admission.
        if (message.Activity != NativeActivity.Idle) return new(SubmissionStatus.Held, "native_recipient_not_idle");
        // Optional explicit policy values must be locally verified. Registry records do not expose these.
        if (message.InboundSetting == NativeInboundSetting.Deny) return Refused("native_inbound_denied");
        if (message.InboundSetting == NativeInboundSetting.Hold ||
            !message.DelegateNativeAdmission && message.InboundSetting != NativeInboundSetting.Accept && message.RecipientMode != RecipientMode.Prompting)
            return new(SubmissionStatus.Held, "native_inbound_policy");

        (string PipeName, string CanonicalPath) pipe;
        CredentialTarget credentialTarget;
        try
        {
            pipe = CanonicalPeerTarget.Parse(target.PipePath);
            credentialTarget = CanonicalPeerTarget.Derive(target.PipePath, target.ProcessId, userHome, configDirectory);
        }
        catch (ArgumentException) { return Refused("invalid_native_target"); }

        bool userWriteStarted = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            await using var connection = await connections.ConnectAsync(pipe.PipeName, cancellation).ConfigureAwait(false);
            if (!connection.EnforcesCurrentUser || !Matches(target, connection.Identity)) return Refused("native_identity_mismatch");
            // Actual protected credential reads are confined to this explicit injectable boundary.
            // An agent may invoke a production source only after exact platform approval for that target.
            using var credential = PeerCredential.ParseAndConsume(credentials.Read(credentialTarget));
            if ((credential.ProcessStartFileTime ?? credential.ProcessStart) != target.StartFileTime.ToString(CultureInfo.InvariantCulture))
                return Refused("credential_start_mismatch");
            if (!Matches(target, connection.Reinspect())) return Refused("native_identity_changed");
            byte[] auth = credential.AuthenticationFrame();
            try
            {
                await connection.Outbound.WriteAsync(auth, cancellation).ConfigureAwait(false);
                await connection.Outbound.FlushAsync(cancellation).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(auth); }
            byte[] user = UserFrame(message);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                userWriteStarted = true;
                await connection.Outbound.WriteAsync(user, cancellation).ConfigureAwait(false);
                await connection.Outbound.FlushAsync(cancellation).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(user); }
            return new(SubmissionStatus.SubmittedUnconfirmed, "native_write_only");
        }
        catch (OperationCanceledException) when (userWriteStarted)
        { return new(SubmissionStatus.SubmittedUnconfirmed, "native_write_uncertain"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return userWriteStarted ? new(SubmissionStatus.SubmittedUnconfirmed, "native_write_uncertain")
                : Refused(ex is InvalidDataException ? "invalid_credential" : "native_transport_unavailable");
        }
    }

    private static bool Matches(NativeTarget target, NativeProcessIdentity identity)
        => identity.IsCurrentUser && identity.ProcessId == target.ProcessId && identity.StartFileTime == target.StartFileTime &&
            string.Equals(Path.GetFullPath(identity.ImagePath), Path.GetFullPath(target.ImagePath), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(identity.ImageSha256, target.ImageSha256, StringComparison.OrdinalIgnoreCase) &&
            EngineProfile.Find(target.EngineVersion, identity.ImageSha256) is not null;

    private static bool CanonicalSession(string id)
        => Guid.TryParseExact(id, "D", out Guid parsed) && id == parsed.ToString("D");

    private static byte[] UserFrame(NativeMessage message)
    {
        // No from/from_mode, origin wrapper, permission-mode change, child token or forged selfSent.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "user", message = new { role = "user", content = message.Text },
            uuid = message.MessageId.ToString("D"), session_id = message.SessionId,
        });
        byte[] frame = new byte[json.Length + 1];
        json.CopyTo(frame, 0); frame[^1] = (byte)'\n'; CryptographicOperations.ZeroMemory(json);
        return frame;
    }

    private static SubmissionResult Refused(string reason) => new(SubmissionStatus.Refused, reason);
}
