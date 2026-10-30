using System.Text.Json;
using AgentBroker.ClaudeAdapter;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

internal static class ClaudePinDiagnosticChecks
{
    // The reviewed pins live in the embedded pin document (lane broker-pins); the same three, selected by version.
    private static readonly EngineProfile Pin21292 = EngineProfile.Verified.Single(pin => pin.Version == "2.1.292");
    private static readonly EngineProfile Pin21304 = EngineProfile.Verified.Single(pin => pin.Version == "2.1.304");
    private static readonly EngineProfile Pin21306 = EngineProfile.Verified.Single(pin => pin.Version == "2.1.306");

    internal static async Task RunAsync(Action<bool, string> check, string? scenario = null)
    {
        string[] selected = scenario is null
            ? ["early-reject", "unsupported-version", "hash-mismatch", "mixed", "recovery", "bounded-snapshot", "elevated"] : [scenario];
        foreach (string name in selected)
            switch (name)
            {
                case "early-reject": await EarlyRefusalsStaySilentAsync(check); break;
                case "unsupported-version": await UnsupportedVersionAsync(check); break;
                case "hash-mismatch": await HashMismatchAsync(check); break;
                case "mixed": await MixedPassAndRecoveryAsync(check); break;
                case "recovery": await RecoveryAsync(check); break;
                case "bounded-snapshot": await BoundedSnapshotAsync(check); break;
                case "elevated": await ElevatedSessionAsync(check); break;
                default: throw new ArgumentException("Unknown Claude pin diagnostic scenario.", nameof(scenario));
            }
    }

    private static async Task EarlyRefusalsStaySilentAsync(Action<bool, string> check)
    {
        var missing = Create(101) with
        {
            Image = @"C:\Program Files\WindowsApps\Claude_9.0.0.0_x64\app\claude.exe",
            MetadataFailure = new FileNotFoundException("Synthetic absent numeric session metadata."),
        };
        var malformed = Create(102) with { MetadataFailure = new InvalidDataException("Synthetic malformed session metadata.") };
        var wrongName = Create(103) with { Name = "another-process" };
        var absentImage = Create(104) with { Image = null };
        var relativeImage = Create(105) with { Image = "claude.exe" };
        var wrongImage = Create(106) with { Image = Path.Combine(Path.GetTempPath(), "agentbroker-claude-diagnostic-106", "other.exe") };
        var replacedProcess = Create(107) with { Started = 1 };
        var reparseImage = Create(108) with { GuardFailure = new IOException("Synthetic rejected reparse component.") };
        var source = new PublicSource(missing, malformed, wrongName, absentImage, relativeImage, wrongImage, replacedProcess, reparseImage);
        await using var provider = new ClaudeNativeProvider(source);
        var sessions = await provider.DiscoverAsync(CancellationToken.None);
        check(sessions.Count == 0 && provider.DiscoveryDiagnostics.Count == 0,
            "Claude public processes rejected before pin checks remain silent, including Desktop without session metadata");
        check(!source.Calls.Contains("101:open") && !source.Calls.Contains("102:open"),
            "Claude missing or malformed numeric session metadata prevents even process inspection");
        check(!source.Calls.Any(call => call.EndsWith(":version", StringComparison.Ordinal) || call.EndsWith(":hash", StringComparison.Ordinal)),
            "Claude wrong name, image, process lifetime and reparse refusals never reach public version or hash checks");
        check(source.Calls.Count(call => call.EndsWith(":dispose", StringComparison.Ordinal)) == 6,
            "Claude early refusals dispose each public process inspection they opened");
    }

    private static async Task UnsupportedVersionAsync(Action<bool, string> check)
    {
        var source = new PublicSource(Create(201));
        await using var provider = new ClaudeNativeProvider(source);
        var sessions = await provider.DiscoverAsync(CancellationToken.None);
        var diagnostic = provider.DiscoveryDiagnostics.SingleOrDefault();
        check(sessions.Count == 0 && diagnostic?.Code == "unsupported_public_version",
            "Claude stale public version is refused with its specific compatibility diagnostic");
        check(diagnostic is not null && diagnostic.Description.Contains("Claude Code 2.1.305 installed", StringComparison.Ordinal) &&
            EngineProfile.Verified.All(profile => diagnostic.Description.Contains(profile.Version, StringComparison.Ordinal)),
            "Claude stale version diagnostic names the installed and every currently verified version");
        check(source.Calls.SequenceEqual(["201:metadata", "201:open", "201:name", "201:image", "201:start", "201:guard", "201:version", "201:dispose"]),
            "Claude stale-version reporting follows metadata, identity, lifetime and reparse validation without hashing");
        check(await provider.RefreshAsync(201, CancellationToken.None) is null &&
            provider.DiscoveryDiagnostics.Count == 1,
            "Claude direct refresh refuses stale delivery targets without accumulating discovery diagnostics");
    }

    private static async Task HashMismatchAsync(Action<bool, string> check)
    {
        var entry = Create(301) with { Version = Pin21306.Version, Hash = new string('0', 64) };
        var source = new PublicSource(entry);
        await using var provider = new ClaudeNativeProvider(source);
        var sessions = await provider.DiscoverAsync(CancellationToken.None);
        var diagnostic = provider.DiscoveryDiagnostics.SingleOrDefault();
        check(sessions.Count == 0 && diagnostic?.Code == "image_hash_mismatch",
            "Claude supported public version with an unverified full image hash is refused with its distinct reason");
        check(diagnostic is not null && diagnostic.Description.Contains("Claude Code " + entry.Version + " installed", StringComparison.Ordinal) &&
            diagnostic.Description.Contains("hash does not match", StringComparison.Ordinal) &&
            EngineProfile.Verified.All(profile => diagnostic.Description.Contains(profile.Version, StringComparison.Ordinal)),
            "Claude image mismatch status names installed and verified versions without exposing the executable hash");
        check(source.Calls.Count(call => call == "301:hash") == 1 &&
            source.Calls.IndexOf("301:version") < source.Calls.IndexOf("301:hash"),
            "Claude full image hash still follows its supported public version filter exactly once");
        check(!diagnostic!.Description.Contains(entry.Image!, StringComparison.Ordinal) &&
            !diagnostic.Description.Contains(entry.Metadata.SessionId, StringComparison.Ordinal) &&
            !diagnostic.Description.Contains(entry.Hash, StringComparison.Ordinal),
            "Claude compatibility diagnostic excludes image paths, session identity and raw image hashes");
    }

    private static async Task MixedPassAndRecoveryAsync(Action<bool, string> check)
    {
        var stale = Create(401);
        var supported = Create(402) with { Version = Pin21292.Version, Hash = Pin21292.Sha256 };
        var source = new PublicSource(stale, supported);
        await using var provider = new ClaudeNativeProvider(source);
        var first = await provider.DiscoverAsync(CancellationToken.None);
        var firstSnapshot = provider.DiscoveryDiagnostics;
        check(first.Count == 1 && first[0].Target.ProcessId == supported.Pid &&
            EngineProfile.Find(first[0].Target.EngineVersion, first[0].Target.ImageSha256) == Pin21292 &&
            firstSnapshot.Count == 1 && firstSnapshot[0].Code == "unsupported_public_version",
            "Claude mixed discovery retains its exactly pinned supported session and reports a stale peer in the same pass");
        source.Processes.Remove(stale.Pid);
        var recovered = await provider.DiscoverAsync(CancellationToken.None);
        check(recovered.Count == 1 && recovered[0].Target == first[0].Target && provider.DiscoveryDiagnostics.Count == 0,
            "Claude completed healthy discovery replaces and clears the stale-version diagnostic");
        check(firstSnapshot.Count == 1 && firstSnapshot[0].Code == "unsupported_public_version",
            "Claude previously published discovery snapshots remain immutable after recovery");
        check(source.Calls.Count(call => call == "402:hash") == 1 && source.Calls.Count(call => call == "402:version") == 1,
            "Claude reporting preserves the verified process-incarnation cache without extra version reads or hashing");
        source.Processes[supported.Pid] = supported with { Started = supported.Started + 1 };
        var replaced = await provider.DiscoverAsync(CancellationToken.None);
        check(replaced.Count == 0 && provider.DiscoveryDiagnostics.Count == 0,
            "Claude lifetime mismatch stays silent even when an old incarnation was cached");
        source.Processes.Clear();
        await provider.DiscoverAsync(CancellationToken.None);
        source.Processes[supported.Pid] = supported with { Version = Pin21306.Version, Hash = new string('0', 64) };
        var reused = await provider.DiscoverAsync(CancellationToken.None);
        check(reused.Count == 0 && provider.DiscoveryDiagnostics.Single().Code == "image_hash_mismatch" &&
            source.Calls.Count(call => call == "402:hash") == 2,
            "Claude departed-process cache eviction still rechecks and diagnoses a later unverified image");
    }

    private static async Task BoundedSnapshotAsync(Action<bool, string> check)
    {
        var entries = Enumerable.Range(501, 40).Select(pid => Create(pid) with { Version = "2.1." + pid }).Reverse().ToArray();
        var source = new PublicSource(entries);
        await using var provider = new ClaudeNativeProvider(source);
        await provider.DiscoverAsync(CancellationToken.None);
        check(provider.DiscoveryDiagnostics.Count == 32 && source.Calls.Count(call => call.EndsWith(":metadata", StringComparison.Ordinal)) == 32 &&
            !source.Calls.Contains("533:metadata"),
            "Claude discovery diagnostics retain the existing sorted thirty-two-process bound");
        source.Processes.Clear();
        await provider.DiscoverAsync(CancellationToken.None);
        check(provider.DiscoveryDiagnostics.Count == 0,
            "Claude empty completed discovery clears all previous pin diagnostics");
        source.Processes[601] = Create(601) with { Version = "2.1.310\n" + new string('x', 512) };
        await provider.DiscoverAsync(CancellationToken.None);
        string text = provider.DiscoveryDiagnostics.Single().ToStatusText();
        check(!text.Any(char.IsControl) && text.Length <= 386 && !text.Contains(new string('x', 65), StringComparison.Ordinal),
            "Claude installed-version reporting is bounded and strips public metadata control characters");
    }

    // N-broker-elevated-caller-01: a Claude Code session of this user's home that runs elevated used to be skipped in
    // silence (its process cannot be opened from a Medium broker). It is still skipped and never woken, but counted, and
    // the count names no process, session or path.
    private static async Task ElevatedSessionAsync(Action<bool, string> check)
    {
        var denied = new System.ComponentModel.Win32Exception(5);
        var elevated = Create(801) with { ImageFailure = denied };
        var secondElevated = Create(802) with { ImageFailure = new System.ComponentModel.Win32Exception(5) };
        var unreadableRecord = Create(803) with { MetadataFailure = new System.ComponentModel.Win32Exception(5) };
        var otherFailure = Create(804) with { ImageFailure = new System.ComponentModel.Win32Exception(299) };
        var supported = Create(805) with { Version = Pin21292.Version, Hash = Pin21292.Sha256 };
        var source = new PublicSource(elevated, unreadableRecord, otherFailure, supported);
        await using var provider = new ClaudeNativeProvider(source);
        var sessions = await provider.DiscoverAsync(CancellationToken.None);
        var diagnostic = provider.DiscoveryDiagnostics.SingleOrDefault(item => item.Code == "claude_session_elevated");
        check(sessions.Count == 1 && sessions[0].Target.ProcessId == supported.Pid && diagnostic is not null,
            "CLAUDE_ELEVATED_COUNTED: an elevated session is counted in the pass's diagnostic while the openable one is still listed");
        check(provider.DiscoveryDiagnostics.Count == 1 && diagnostic is not null &&
            diagnostic.Description.StartsWith("1 Claude Code session runs elevated or as another user, so the broker cannot verify or wake it;", StringComparison.Ordinal),
            "CLAUDE_ELEVATED_RECORD_ONLY: an unreadable session record and a non-access failure are not counted as elevated");
        string text = diagnostic?.ToStatusText() ?? "";
        check(!text.Contains("801", StringComparison.Ordinal) && !text.Contains(elevated.Metadata.SessionId, StringComparison.Ordinal) &&
            !text.Contains(elevated.Image!, StringComparison.Ordinal) && text.Contains("register_agent", StringComparison.Ordinal),
            "CLAUDE_ELEVATED_COUNT_ONLY: the diagnostic names no process, session or path, and says what to do instead");
        source.Processes[secondElevated.Pid] = secondElevated;
        await provider.DiscoverAsync(CancellationToken.None);
        check(provider.DiscoveryDiagnostics.SingleOrDefault(item => item.Code == "claude_session_elevated")?.Description
                .StartsWith("2 Claude Code sessions run elevated", StringComparison.Ordinal) == true,
            "CLAUDE_ELEVATED_PLURAL: two elevated sessions are counted as two");
        check(await provider.RefreshAsync(elevated.Pid, CancellationToken.None) is null,
            "CLAUDE_ELEVATED_REFRESH: a direct refresh of an elevated session still finds no target");
        source.Processes.Remove(elevated.Pid); source.Processes.Remove(secondElevated.Pid);
        await provider.DiscoverAsync(CancellationToken.None);
        check(provider.DiscoveryDiagnostics.Count == 0, "CLAUDE_ELEVATED_CLEARS: the diagnostic clears once no elevated session remains");
    }

    private static async Task RecoveryAsync(Action<bool, string> check)
    {
        var stale = Create(701);
        var source = new PublicSource(stale);
        await using var provider = new ClaudeNativeProvider(source);
        await provider.DiscoverAsync(CancellationToken.None);
        var staleSnapshot = provider.DiscoveryDiagnostics;
        source.Processes[stale.Pid] = stale with { Version = Pin21304.Version, Hash = Pin21304.Sha256 };
        var recovered = await provider.DiscoverAsync(CancellationToken.None);
        check(staleSnapshot.Count == 1 && recovered.Count == 1 && provider.DiscoveryDiagnostics.Count == 0,
            "Claude stale-version diagnostic clears when the same process becomes an exactly verified supported candidate");
        source.Processes[stale.Pid + 1] = Create(stale.Pid + 1);
        await provider.DiscoverAsync(CancellationToken.None);
        var secondSnapshot = provider.DiscoveryDiagnostics;
        source.Processes.Clear();
        await provider.DiscoverAsync(CancellationToken.None);
        check(secondSnapshot.Count == 1 && provider.DiscoveryDiagnostics.Count == 0,
            "Claude stale-version diagnostic clears after its last process exits");
    }

    private static PublicProcess Create(int pid)
    {
        ulong started = checked((ulong)DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc());
        string session = Guid.NewGuid().ToString("D");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messagingSocketPath = @"\\.\pipe\cc-msg-" + Guid.NewGuid().ToString("N"),
            sessionId = session,
            procStartFt = started.ToString(System.Globalization.CultureInfo.InvariantCulture),
            kind = "interactive",
            status = "idle",
            updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        var metadata = NativeSessionMetadata.Parse(pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json", json);
        return new(pid, metadata, "claude", Path.Combine(Path.GetTempPath(), "agentbroker-claude-diagnostic-" + pid, "claude.exe"),
            // Re-pointed by lane broker-pins: 2.1.310 is now pinned, so a stale build is 2.1.305, between pins and not
            // newer than all of them (the newer-than-every-pin text has its own checks in PinStatusChecks.cs).
            started, "2.1.305", Pin21306.Sha256);
    }

    private sealed record PublicProcess(int Pid, NativeSessionMetadata Metadata, string Name, string? Image,
        ulong Started, string? Version, string Hash)
    {
        internal Exception? MetadataFailure { get; init; }
        internal Exception? GuardFailure { get; init; }
        internal Exception? ImageFailure { get; init; }
    }

    private sealed class PublicSource(params PublicProcess[] processes) : IClaudeDiscoverySource
    {
        internal Dictionary<int, PublicProcess> Processes { get; } = processes.ToDictionary(process => process.Pid);
        internal List<string> Calls { get; } = [];
        public IReadOnlyList<int> GetProcessIds() => Processes.Keys.ToArray();
        public NativeSessionMetadata ReadMetadata(int processId)
        {
            Calls.Add(processId + ":metadata");
            var process = Processes[processId];
            if (process.MetadataFailure is not null) throw process.MetadataFailure;
            return process.Metadata;
        }
        public IClaudeDiscoveryProcess OpenProcess(int processId)
        { Calls.Add(processId + ":open"); return new ProcessView(Processes[processId], Calls); }
        public void RejectReparseComponents(string image)
        {
            var process = Find(image); Calls.Add(process.Pid + ":guard");
            if (process.GuardFailure is not null) throw process.GuardFailure;
        }
        public string? ReadProductVersion(string image)
        { var process = Find(image); Calls.Add(process.Pid + ":version"); return process.Version; }
        public string ReadImageSha256(string image)
        { var process = Find(image); Calls.Add(process.Pid + ":hash"); return process.Hash; }
        private PublicProcess Find(string image) => Processes.Values.Single(process => process.Image == image);

        private sealed class ProcessView(PublicProcess process, List<string> calls) : IClaudeDiscoveryProcess
        {
            public string ProcessName { get { calls.Add(process.Pid + ":name"); return process.Name; } }
            public string? ImagePath { get { calls.Add(process.Pid + ":image"); return process.ImageFailure is { } failure ? throw failure : process.Image; } }
            public ulong StartFileTime { get { calls.Add(process.Pid + ":start"); return process.Started; } }
            public void Dispose() => calls.Add(process.Pid + ":dispose");
        }
    }
}
