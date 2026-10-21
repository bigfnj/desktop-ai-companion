using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using AgentBroker.ClaudeAdapter;

namespace AgentBroker.ClaudeSmokeProbe;

public sealed record SmokeTarget(NativeTarget Native, CredentialTarget Credential, NativeActivity Activity)
{
    public string Classification => Native.EngineVersion is "2.1.304" or "2.1.306"
        ? "verified_vscode_bundled_runtime" : "verified_standalone_runtime";
}

public static class SmokeTargets
{
    public static SmokeTarget SelectExact(string sessionId, IReadOnlyList<SmokeTarget> candidates)
    {
        var matches = candidates.Where(candidate => candidate.Native.SessionId == sessionId).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Exact unique native target is unavailable.");
        return matches[0];
    }

    public static SmokeTarget Resolve(string sessionId, IReadOnlyList<int> explicitProcessIds)
    {
        if (!Guid.TryParseExact(sessionId, "D", out Guid parsed) || parsed == Guid.Empty || parsed.ToString("D") != sessionId ||
            explicitProcessIds.Count is < 1 or > 32 || explicitProcessIds.Any(pid => pid <= 1)) throw new ArgumentException("Invalid explicit target selection.");
        var matches = new List<SmokeTarget>();
        foreach (int pid in explicitProcessIds.Distinct())
        {
            try
            {
                var metadata = NativeMetadataReader.ReadDefaultHomeProcess(pid);
                if (metadata.SessionId != sessionId) continue;
                using var process = Process.GetProcessById(pid);
                if (!string.Equals(process.ProcessName, "claude", StringComparison.OrdinalIgnoreCase)) continue;
                string? image = process.MainModule?.FileName;
                if (image is null || !Path.IsPathFullyQualified(image) || !string.Equals(Path.GetFileName(image), "claude.exe", StringComparison.OrdinalIgnoreCase)) continue;
                ulong start = checked((ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc());
                if (metadata.StartFileTime != start) continue;
                CredentialPathPolicy.RejectReparseComponents(image, File.GetAttributes);
                using var executable = File.OpenRead(image);
                string hash = Convert.ToHexString(SHA256.HashData(executable));
                var profile = EngineProfile.Verified.SingleOrDefault(value => value.Sha256 == hash);
                if (profile is null) continue;
                var now = DateTimeOffset.UtcNow;
                bool plausible = metadata.HasPlausibleActivityTimestamp(now);
                var target = new NativeTarget(metadata.CanonicalPipe, pid, image, profile.Sha256, profile.Version, start, sessionId);
                var credential = CanonicalPeerTarget.Derive(metadata.CanonicalPipe, pid,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); // Pure path derivation only.
                matches.Add(new(target, credential, plausible && metadata.Status == "idle" ? NativeActivity.Idle : NativeActivity.Unknown));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or OverflowException)
            { /* Unavailable/malformed public metadata never chooses a fallback session. */ }
        }
        return SelectExact(sessionId, matches);
    }

    public static void VerifyExpected(SmokeTarget fresh, int expectedPid, ulong expectedStart, string expectedCredentialPath)
    {
        if (fresh.Native.ProcessId != expectedPid || fresh.Native.StartFileTime != expectedStart ||
            !Path.IsPathFullyQualified(expectedCredentialPath) ||
            !string.Equals(Path.GetFullPath(expectedCredentialPath), fresh.Credential.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Approved target identity changed.");
        if (fresh.Activity != NativeActivity.Idle) throw new InvalidDataException("Native recipient is not freshly idle.");
    }
}
