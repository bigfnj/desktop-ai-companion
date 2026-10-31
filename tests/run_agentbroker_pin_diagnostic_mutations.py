"""Negative witnesses for AgentBroker pin diagnostics and deferred-claim ordering.

List the witnesses without changing files or starting processes:
  python -X utf8 tests/run_agentbroker_pin_diagnostic_mutations.py

In an exclusively reserved build window, run the witnesses and retain evidence:
  python -X utf8 tests/run_agentbroker_pin_diagnostic_mutations.py --apply

Only the explicit C# sources below can be temporarily changed.
No pin, verification predicate, hash implementation or vendor file is changed.
The original bytes are restored in finally, including on test/build failure.
All builds are serial. Stdout/stderr go directly to ordinary evidence files,
avoiding inherited-pipe reader waits during subprocess timeout handling.
Reporting-only witnesses require existing regressions to pass while mutated.
The separate concurrency witness intentionally restores the product race and
runs its deterministic new check; existing regressions run after restoration.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid


ROOT = Path(__file__).resolve().parents[1]
WRAPPER = ROOT / "tools/agentbroker/Invoke-DeveloperTool.ps1"
SOURCES = {
    "claude": "tools/AgentBroker.Worker/Adapters/ClaudeNativeProvider.cs",
    "codex": "tools/AgentBroker.Worker/Adapters/CodexNativeAdapter.cs",
    "desktop": "tools/AgentBroker.CodexDesktopAdapter/WindowsDesktopConnection.cs",
    "router": "tools/AgentBroker.CodexDesktopAdapter/VsCodeRouterCompatibilityProfile.cs",
    "runtime": "tools/AgentBroker.Worker/BrokerRuntime.cs",
    "provider": "tools/AgentBroker.Worker/Adapters/CodexNativeProvider.cs",
    "claims": "tools/AgentBroker.Worker/DeferredDeliveryClaims.cs",
    "store": "src/AgentBroker.Core/BrokerStore.cs",
    "tools": "tools/AgentBroker.Worker/BrokerTools.cs",
    "workerapp": "tools/AgentBroker.Worker/WorkerApplication.cs",
    "tracker": "tools/AgentBroker.Worker/McpActivityTracker.cs",
    "localcontrol": "tools/AgentBroker.Worker/LocalControlServer.cs",
    "lanprofile": "tools/AgentBroker.Worker/LanProfile.cs",
    "desktopadapter": "tools/AgentBroker.CodexDesktopAdapter/DesktopAdapter.cs",
    "publishertrust": "tools/AgentBroker.CodexDesktopAdapter/WindowsPublisherImageTrust.cs",
    "vscode": "tools/AgentBroker.CodexDesktopAdapter/VsCodeCompatibilityProfile.cs",
    "probe": "tools/AgentBroker.CodexDesktopAdapter/DesktopConnectionProfileProbe.cs",
    "channel": "tools/AgentBroker.Worker/McpChannelBridge.cs",
    "federation": "tools/AgentBroker.Worker/Federation/PeerFederation.cs",
    "models": "src/AgentBroker.Core/BrokerModels.cs",
    "pins": "tools/AgentBroker.Pins/PinDocument.cs",
    "diagnostic": "tools/AgentBroker.Worker/AdapterDiscoveryDiagnostic.cs",
}
PROJECTS = {
    "worker": "tests/AgentBroker.Worker.Tests/AgentBroker.Worker.Tests.csproj",
    "desktop": "tests/AgentBroker.Desktop.Tests/AgentBroker.Desktop.Tests.csproj",
}
ASSEMBLIES = {
    family: ROOT / Path(project).parent / "bin/Release/net10.0-windows" / (Path(project).stem + ".dll")
    for family, project in PROJECTS.items()
}
# Worker 196 measured on 22b05e9 (feature/agentbroker-bus base): the folded 2026-10-26 preview put
# peer diagnostics (24) and Claude observation (42) before the --existing-tests-only cut-off.
# Re-measured 2026-10-29 by lane feature/broker-next-160 (N-modules-fixes-01): later lanes had added checks before the
# cut-off without moving these, so every --apply run failed its healthy baseline; both include that lane's two
# malformed-subject checks (Claude observation, Codex observation). Worker 210 after the same lane's seven runtime
# lifecycle checks (N-agentbroker-native-flicker-01). Worker 213 and focused 81 after the review fixes: three more lifecycle
# checks (adapter failure recorded, owner_discovery_rejected per row), and owner_discovery_rejected during owner
# resolution now checked as a skipped row (two checks) instead of a failed pass (three).
EXISTING_COUNTS = {"worker": 234, "desktop": 157}  # worker 213 -> 234: re-measured by lane broker-163; stale before it (no existing-suite check changed there)
FOCUSED_COUNTS = {"worker": 91, "desktop": 15}  # worker 81 -> 91: lane broker-163 (CLAUDE_ELEVATED_* and RUNTIME_NOTE_*)
CLAUDE_SCENARIOS = ("early-reject", "unsupported-version", "hash-mismatch", "mixed", "recovery", "bounded-snapshot")


@dataclass(frozen=True)
class Witness:
    name: str
    source: str
    family: str
    test_args: tuple[str, ...]
    failure_file: str
    replacements: tuple[tuple[str, str], ...]
    replacement_counts: tuple[int, ...] = ()
    kind: str = "reporting"


WITNESSES = (
    Witness("claude-reporting", "claude", "worker", (), "ClaudePinDiagnosticChecks.cs", (
        ('                    diagnostics?.Add(CompatibilityDiagnostic("unsupported_public_version", version));', ""),
        ('                    diagnostics?.Add(CompatibilityDiagnostic("image_hash_mismatch", version));', ""),
    )),
    Witness("codex-error-code-reporting", "codex", "worker", ("--codex-pin-diagnostics-only",), "CodexPinDiagnosticChecks.cs", ((
        """        catch (DesktopAdapterException error)
        {
            bindings.Clear();
            throw new AdapterDiscoveryException(DescribeDiscoveryFailure(error), error);
        }""",
        '        catch (DesktopAdapterException) { bindings.Clear(); throw new IOException("Native Codex directory is unavailable."); }',
    ),)),
    Witness("codex-profile-reporting", "provider", "worker", ("--codex-provider-diagnostics-only",), "CodexProviderDiagnosticChecks.cs", (
        ('                diagnostics.Add(CodexNativeAdapter.DescribeDiscoveryFailure(error));', "                _ = error;"),
        ('                diagnostics.Add(new("vscode_profile_unavailable",\n'
         '                    $"Codex VS Code compatibility verification could not complete; verified extension {VsCodeCompatibilityProfile.VerifiedVersions}."));', ""),
    )),
    Witness("desktop-package-version-reporting", "desktop", "desktop", ("--pin-diagnostics-only",), "CodexPinMetadataChecks.cs", (
        ('throw new DesktopAdapterException("unsupported_desktop_version", InstalledApplicationVersion(image));',
         'throw new DesktopAdapterException("unsupported_desktop_version");'),
        ('throw new DesktopAdapterException("desktop_image_mismatch", profile.ApplicationVersion);',
         'throw new DesktopAdapterException("desktop_image_mismatch");'),
    )),
    Witness("vscode-router-version-reporting", "router", "desktop", ("--pin-diagnostics-only",), "CodexPinMetadataChecks.cs", ((
        'throw new DesktopAdapterException("unsupported_vscode_router", version);',
        'throw new DesktopAdapterException("unsupported_vscode_router");',
    ),)),
    # Both re-pointed by lane feature/broker-next-160 (N-agentbroker-native-flicker-01): the pass moved into one loop per
    # adapter (BrokerRuntime.DiscoverLoopAsync), four spaces shallower; same bytes otherwise, same intent.
    Witness("runtime-completed-pass-reporting", "runtime", "worker", ("--runtime-pin-diagnostics-only",), "AdapterDiagnosticChecks.cs", ((
        # Re-pointed by lane broker-163 (notes are kept apart from errors): the same completed-pass error reporting.
        """                var diagnostics = reported.Where(item => !item.Note).Select(item => item.ToStatusText()).Distinct().ToArray();
                if (diagnostics.Length == 0) adapterErrors.TryRemove(adapter.Id, out _);
                else adapterErrors[adapter.Id] = string.Join("; ", diagnostics);""",
        "                adapterErrors.TryRemove(adapter.Id, out _);",
    ),)),
    Witness("runtime-specific-exception-reporting", "runtime", "worker", ("--codex-pin-diagnostics-only",), "CodexPinDiagnosticChecks.cs", ((
        """                adapterErrors[adapter.Id] = e is AdapterDiscoveryException diagnostic ? diagnostic.Diagnostic.ToStatusText()
                    : e is BrokerException b ? b.Code : "adapter_unavailable";""",
        '                adapterErrors[adapter.Id] = e is BrokerException b ? b.Code : "adapter_unavailable";',
    ),)),
    Witness("deferred-claim-atomicity", "claims", "worker", ("--deferred-claim-only",), "DeferredClaimChecks.cs",
            (("        lock (gate)", "        _ = gate;"),), replacement_counts=(2,), kind="concurrency"),
    # ---- lane feature/agentbroker-bus ----
    Witness("bus-mailbox-addressable-live", "store", "worker", ("--mailbox-only",), "MailboxChecks.cs", ((
        "sessions.TryGetValue(address, out var s) && (Live(s) || Retained(s));",
        "sessions.TryGetValue(address, out var s) && Live(s);",
    ),)),
    Witness("bus-away-leaks-peer-directory", "store", "worker", ("--mailbox-only",), "MailboxChecks.cs", ((
        "if (forPeer) return sessions.Values.Where(s => s.LeaseUntil > now && !s.IsPrivate).ToArray();",
        "if (forPeer) return sessions.Values.Where(s => !s.IsPrivate)"
        ".Select(s => s.LeaseUntil > now ? s : s with { Activity = SessionActivity.Away }).ToArray();",
    ),)),
    Witness("bus-signal-never-completed", "store", "worker", ("--wait-tools-only",), "WaitToolChecks.cs", ((
        "Interlocked.Exchange(ref changeSignal, NewSignal()).TrySetResult();",
        "Interlocked.Exchange(ref changeSignal, NewSignal());",
    ),)),
    Witness("bus-send-compares-createdat", "store", "worker", ("--send-only",), "SendToolChecks.cs", ((
        "return Accept(sent, authenticatedBroker);",
        "return Accept(sent with { CreatedAt = Now }, authenticatedBroker);",
    ),)),
    # Re-pointed by lane feature/broker-next-160 (N-broker-e-02): wait_for_messages now asks the store's AcceptedIncoming
    # query, which holds the status filter; the mutation drops it there (same intent).
    Witness("bus-wait-returns-acknowledged", "store", "worker", ("--wait-tools-only",), "WaitToolChecks.cs", ((
        "            return state.Messages.Where(r => r.Message.Recipient == recipient && r.Status == MessageStatus.Accepted)",
        "            return state.Messages.Where(r => r.Message.Recipient == recipient)",
    ),)),
    Witness("bus-wait-per-session-unbounded", "runtime", "worker", ("--wait-tools-only",), "WaitToolChecks.cs", ((
        'if (!waits.TryAdd(session, 0)) throw new BrokerException("wait_in_progress", "This session already has an active wait.");',
        "waits.TryAdd(session, 0);",
    ),)),
    Witness("bus-instructions-removed", "workerapp", "worker", ("--instructions-only",), "InstructionsChecks.cs", ((
        "AddMcpServer(server => server.ServerInstructions = BrokerInstructions.Text)",
        "AddMcpServer()",
    ),)),
    Witness("bus-polling-retained-renewal", "store", "worker", ("--polling-lease-only",), "PollingLeaseChecks.cs", ((
        'session.Adapter != "mcp" || !Addressable(address))',
        'session.Adapter != "mcp" || !Live(address))',
    ),)),
    Witness("bus-native-retained", "store", "worker", ("--polling-lease-only",), "PollingLeaseChecks.cs", (
        ('            if (session.Adapter == "mcp") mailboxActivity[session.Address] = Now;\n'
         '            else mailboxActivity.Remove(session.Address);',
         "            mailboxActivity[session.Address] = Now;"),
        ('private bool Retained(BrokerSession s) => s.Adapter == "mcp" && s.Activity',
         "private bool Retained(BrokerSession s) => s.Activity"),
    )),
    # ---- lane feature/agentbroker-backlog-a ----
    # Inbox attribution (N-agentbroker-client-onboarding-01) and the LAN network profile (N-agentbroker-lan-profile-01).
    # The new checks run after the --existing-tests-only cut-off, so the existing pin stays 196.
    Witness("backlog-a-attribution-any-family", "tracker", "worker", ("--inbox-attribution-only",), "OnboardingChecks.cs", ((
        "        if (family is not (McpClientFamily.Claude or McpClientFamily.Codex) || incarnation == Guid.Empty ||",
        "        if (incarnation == Guid.Empty ||",
    ),)),
    Witness("backlog-a-attribution-outlives-directory", "localcontrol", "worker", ("--inbox-attribution-only",), "OnboardingChecks.cs", ((
        "        var attributed = mcp.Inboxes?.Where(inbox => listed.Contains((inbox.SessionId, inbox.Incarnation))).ToArray();",
        "        var attributed = mcp.Inboxes; _ = listed;",
    ),)),
    Witness("backlog-a-lan-any-adapter", "lanprofile", "worker", ("--lan-profile-only",), "OnboardingChecks.cs", ((
        "        var categories = connections.Where(item => item.AdapterId == carrier[0]).Select(item => item.Category).ToArray();",
        "        var categories = connections.Select(item => item.Category).ToArray();",
    ),)),
    Witness("backlog-a-lan-least-restrictive", "lanprofile", "worker", ("--lan-profile-only",), "OnboardingChecks.cs", ((
        "        return categories.Min() switch",
        "        return categories.Max() switch",
    ),)),
    Witness("backlog-a-lan-rule-any-under-name", "lanprofile", "worker", ("--lan-profile-only",), "OnboardingChecks.cs", ((
        '        return found.Count == 1 && FirewallSetup.Matches(found[0], desired) ? ("configured", profiles) : ("conflicting", profiles);',
        '        return ("configured", profiles);',
    ),)),
    Witness("backlog-a-lan-cache-by-reference", "lanprofile", "worker", ("--lan-profile-only",), "OnboardingChecks.cs", ((
        "            if (!Nullable.Equals(key, requested)) { key = requested; reading = null; }",
        "            if (key != requested) { key = requested; reading = null; }",
    ),)),
    Witness("backlog-a-lan-stale-after-address-change", "lanprofile", "worker", ("--lan-profile-only",), "OnboardingChecks.cs", ((
        "            if (!Nullable.Equals(key, requested)) { key = requested; reading = null; }",
        "            if (!Nullable.Equals(key, requested)) { key = requested; }",
    ),)),
    Witness("backlog-a-lan-own-address-evidence", "lanprofile", "worker", ("--lan-profile-only",), "OnboardingChecks.cs", ((
        "        if (local is not null && NetworkPolicy.Normalize(local).Equals(NetworkPolicy.Normalize(remote))) return;",
        "        _ = local;",
    ),)),
    Witness("backlog-a-lan-failure-reads-private", "lanprofile", "worker", ("--lan-profile-only",), "OnboardingChecks.cs", ((
        '{ category = "unavailable"; }',
        '{ category = "private"; }',
    ),)),
    # ---- lane feature/agentbroker-backlog-b ----
    # Codex observation-only discovery: publisher trust, protected location, router fallback, adapter guards.
    # Re-pointed by lane feature/broker-next-160 (N-broker-b-02): the subject parse moved into the linked
    # tools/AgentBroker.Pins/WindowsAuthenticode.cs; the organization comparison stays in this adapter's own policy.
    Witness("codex-publisher-organization", "publishertrust", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "                commonName == expectedPublisher && organization == expectedPublisher;",
        "                commonName == expectedPublisher;",
    ),)),
    Witness("codex-protected-location-owner", "publishertrust", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "        if (descriptor.Owner?.Value is not (LocalSystem or TrustedInstaller) || descriptor.DiscretionaryAcl is null) return false;",
        "        if (descriptor.DiscretionaryAcl is null) return false;",
    ),)),
    Witness("codex-protected-location-conditional-write", "publishertrust", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "bool allows = known is QualifiedAce qualified ? qualified.AceQualifier == AceQualifier.AccessAllowed : known is CompoundAce;",
        "bool allows = known is QualifiedAce qualified ? qualified.AceQualifier == AceQualifier.AccessAllowed && !qualified.IsCallback : known is CompoundAce;",
    ),)),
    Witness("codex-router-strict-fallback", "desktop", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "when (allowPublisherSigned && DesktopCompatibilityProfile.IsExactPinMismatch(pinned.Code))",
        "when (DesktopCompatibilityProfile.IsExactPinMismatch(pinned.Code))",
    ),)),
    Witness("codex-router-identity-fallback", "desktop", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "when (allowPublisherSigned && DesktopCompatibilityProfile.IsExactPinMismatch(pinned.Code))",
        "when (allowPublisherSigned)",
    ),)),
    Witness("codex-publisher-router-location", "desktop", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "        RequirePublisher(verify(image, WindowsPublisherImageTrust.OpenAIPublisher, true), version);",
        "        RequirePublisher(verify(image, WindowsPublisherImageTrust.OpenAIPublisher, false), version);",
    ),)),
    Witness("codex-publisher-router-archive", "desktop", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        '        if (!isProtectedFile(archive)) throw new DesktopAdapterException("public_image_location_unprotected", version);\n',
        "",
    ),)),
    Witness("codex-running-engine-publisher", "vscode", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "            WindowsDesktopConnection.RequirePublisher(verify(engine.Image), version);\n",
        "",
    ),)),
    Witness("codex-adapter-observation-guard", "desktopadapter", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "            if (connection.Trust != DesktopRouterTrust.ExactPinned &&\n"
        "                !(connection.Trust == DesktopRouterTrust.PublisherSigned && options.EnableUpdateTolerantDelivery))\n"
        '                return new(DesktopSubmissionOutcome.Rejected, ErrorCode: "router_observation_only");\n',
        "",
    ),)),
    Witness("codex-adapter-binding-trust", "desktopadapter", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "            || !binding.SupportsUntrustedAppInput || binding.RouterTrust != connection.Trust)",
        "            || !binding.SupportsUntrustedAppInput)",
    ),)),
    Witness("codex-adapter-capability-observation", "desktopadapter", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        "        if (!capability && connection.Trust != DesktopRouterTrust.PublisherSigned)",
        "        if (!capability)",
    ),)),
    Witness("codex-probe-trust-guard", "probe", "desktop", ("--codex-observation-only",), "CodexObservationChecks.cs", ((
        " ||\n            connection.Trust is not (DesktopRouterTrust.ExactPinned or DesktopRouterTrust.PublisherSigned))",
        ")",
    ),)),
    # Worker provider/adapter: observation refusal, lent trust, unsupported capability, strict profiles, diagnostics.
    Witness("codex-worker-observation-guard", "codex", "worker", ("--codex-trust-policy-only",), "CodexTrustPolicyChecks.cs", ((
        "            if (previous.RouterTrust != DesktopRouterTrust.ExactPinned && !updateTolerant)\n"
        '                return new(AdapterAdmission.Refused, "codex_observation_only");\n',
        "",
    ),)),
    Witness("codex-worker-lent-exact-trust", "codex", "worker", ("--codex-trust-policy-only",), "CodexTrustPolicyChecks.cs", ((
        "        CodexDeliveryAuthority.ExactPinned when binding.RouterTrust == DesktopRouterTrust.ExactPinned => null,",
        "        CodexDeliveryAuthority.ExactPinned => null,",
    ),)),
    Witness("codex-worker-unsupported-capability", "provider", "worker", ("--codex-trust-policy-only",), "CodexTrustPolicyChecks.cs", ((
        "        return NativeIdleGuard && profileTrust != DesktopRouterTrust.Unverified && binding.SupportsUntrustedAppInput\n",
        "        return NativeIdleGuard && profileTrust != DesktopRouterTrust.Unverified\n",
    ),)),
    Witness("codex-worker-strict-profiles", "provider", "worker", ("--codex-trust-policy-only",), "CodexTrustPolicyChecks.cs", ((
        "        if (!updateTolerant || !DesktopCompatibilityProfile.IsExactPinMismatch(exact.Code)) return DesktopRouterTrust.Unverified;",
        "        if (!DesktopCompatibilityProfile.IsExactPinMismatch(exact.Code)) return DesktopRouterTrust.Unverified;",
    ),)),
    # Re-pointed by lane feature/broker-next-160 (N-modules-fixes-01): the submission-time publisher check now names its
    # verifier (the fresh one) as a third argument, so the old two-argument bytes matched nothing.
    Witness("codex-worker-fresh-publisher-check", "provider", "worker", ("--codex-trust-policy-only",), "CodexTrustPolicyChecks.cs", ((
        "            profileTrust = VerifyProfilesByPublisher(error, diagnostics: null, verifyProfilesByPublisher);",
        "            profileTrust = DesktopRouterTrust.PublisherSigned;",
    ),)),
    Witness("codex-worker-router-pin-diagnostic", "provider", "worker", ("--codex-trust-policy-only",), "CodexTrustPolicyChecks.cs", ((
        "            diagnostics.Add(CodexNativeAdapter.DescribeDiscoveryFailure(routerPin));\n",
        "",
    ),)),
    # ---- lane feature/agentbroker-backlog-c ----
    Witness("broker-c-channel-permission-declared", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        '["experimental"] = new JsonObject { ["claude/channel"] = new JsonObject() },',
        '["experimental"] = new JsonObject { ["claude/channel"] = new JsonObject(), ["claude/channel/permission"] = new JsonObject() },',
    ),)),
    Witness("broker-c-channel-newer-revision", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        "requested is not null && SupportedProtocolVersions.Contains(requested, StringComparer.Ordinal) ? requested : SupportedProtocolVersions[0];",
        "requested ?? SupportedProtocolVersions[0];",
    ),)),
    Witness("broker-c-channel-mute-ignored", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        'if (own.IsMuted) return (ChannelDecision.Defer, "muted");', "",
    ),)),
    Witness("broker-c-channel-unpaired-pushed", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        'if (!local && !snapshot.Paired.Any(peer => peer.BrokerId == message.Sender.BrokerId)) return (ChannelDecision.Refuse, "unpaired_sender");', "",
    ),)),
    Witness("broker-c-channel-private-pushed", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        'if (!local && own.IsPrivate) return (ChannelDecision.Refuse, "private_session");', "",
    ),)),
    Witness("broker-c-channel-stale-snapshot", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", (
        ("private string? reported;", "private string? reported;\n        private BrokerSnapshot? cached;"),
        ("try { snapshot = await broker.SnapshotAsync(token); }", "try { snapshot = cached ??= await broker.SnapshotAsync(token); }"),
    )),
    Witness("broker-c-channel-repush", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        "fresh = receipts.Where(receipt => !pushed.ContainsKey(receipt.Message.Id)).ToList();", "fresh = receipts.ToList();",
    ),)),
    Witness("broker-c-channel-push-delivers", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        "await WriteAsync(Notification(receipt.Message, session));",
        "await WriteAsync(Notification(receipt.Message, session)); "
        "await broker.AcknowledgeAsync(receipt.Message.Id, session, MessageStatus.Delivered, token);",
    ),)),
    Witness("broker-c-channel-reply-any-session", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        'if (parent.Message.Recipient != session) throw new ChannelBrokerException("not_addressed_to_this_channel");', "",
    ),)),
    # Re-pointed by lane feature/agentbroker-backlog-d: since N-broker-c-02 the broker refuses a wait on a dropped inbox,
    # which resets the bridge's session, so the timer alone is no longer the only way back. Same intent (a bridge that
    # keeps a dropped inbox), both ways back removed.
    Witness("broker-c-channel-renew-only-by-wait", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", (
        ("if (session is null || Environment.TickCount64 - registeredAt >= tuning.RenewMilliseconds)",
         "if (session is null || Environment.TickCount64 - registeredAt >= long.MaxValue)"),
        ("                    Volatile.Write(ref self, null);\n"
         "                    await Task.Delay(Math.Min(tuning.PollMilliseconds, 250), token);",
         "                    await Task.Delay(Math.Min(tuning.PollMilliseconds, 250), token);"),
    )),
    Witness("broker-c-channel-error-code-wrapped", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        "if (wrapped >= 0) candidate = candidate[(wrapped + 3)..].Trim();", "_ = wrapped;",
    ),)),
    Witness("broker-c-channel-options-repeat", "channel", "worker", ("--channel-only",), "ChannelBridgeChecks.cs", ((
        'else if (args[index] == "--private" && !isPrivate) isPrivate = true;', 'else if (args[index] == "--private") isPrivate = true;',
    ),)),
    # ---- lane feature/agentbroker-backlog-d ----
    # The loopback listener's two authorities (N-bus-04, owner decision 2026-10-27) and a wait on a restart-lost mailbox
    # (N-broker-c-02). The new checks run after the --existing-tests-only cut-off, so the existing pin stays 196.
    Witness("broker-d-host-any-name", "workerapp", "worker", ("--loopback-host-only",), "LoopbackHostChecks.cs", ((
        'bool knownName = host.Host == "127.0.0.1" || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase);',
        "bool knownName = true;",
    ),)),
    Witness("broker-d-host-localhost-refused", "workerapp", "worker", ("--loopback-host-only",), "LoopbackHostChecks.cs", ((
        ' || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase);', ";",
    ),)),
    Witness("broker-d-host-any-port", "workerapp", "worker", ("--loopback-host-only",), "LoopbackHostChecks.cs", ((
        "return knownName && (host.Port ?? 80) == listenerPort;", "return knownName;",
    ),)),
    Witness("broker-d-wait-returns-unaddressable", "tools", "worker", ("--wait-tools-only",), "WaitToolChecks.cs", ((
        '            if (!runtime.Store.IsAddressable(session)) throw new McpException("session_unavailable");\n'
        "            var result = pending();\n",
        "            var result = pending();\n"
        '            if (result.Length == 0 && !runtime.Store.IsAddressable(session)) throw new McpException("session_unavailable");\n',
    ),)),
    # ---- lane feature/agentbroker-backlog-e ----
    # Tombstone on acknowledgement (N-broker-a-02, owner decision 2026-10-27), the rollback to 1.0.4 and the unchanged
    # federation wire. Each witness runs one TombstoneChecks group; the checks run after the --existing-tests-only
    # cut-off, so the existing pin stays 196.
    Witness("broker-e-ack-keeps-text", "store", "worker", ("--tombstone-case", "disk"), "TombstoneChecks.cs", ((
        "        var added = next.Messages.Where(Tombstoned).Select(ToTombstone).ToList();",
        "        var added = new List<Tombstone>();",
    ),)),
    Witness("broker-e-upgrade-not-converted", "store", "worker", ("--tombstone-case", "upgrade"), "TombstoneChecks.cs", ((
        "            bool upgraded = recovered.Any(Tombstoned);",
        "            bool upgraded = false;",
    ),)),
    Witness("broker-e-retry-hash-ignored", "store", "worker", ("--tombstone-case", "retry"), "TombstoneChecks.cs", ((
        "        sameText = candidateText is not null && HashText(candidateText) == tombstone.TextSha256;",
        "        sameText = candidateText is not null;",
    ),)),
    Witness("broker-e-retry-text-not-restored", "store", "worker", ("--tombstone-case", "retry"), "TombstoneChecks.cs", ((
        '        return Render(tombstone, sameText ? candidateText! : "");',
        '        return Render(tombstone, "");',
    ),)),
    # Hiding tombstones from the store's own lookups (FindReceipt, a reply's parent) also fails the EXISTING check
    # "actual HTTP reply tool permits finishing a delivered task after its original delivery deadline" (its parent is now
    # a tombstone), which a reporting witness may not; measured 2026-10-27. So these two target what only the new checks see.
    Witness("broker-e-receipt-skips-tombstone", "tools", "worker", ("--tombstone-case", "receipt"), "TombstoneChecks.cs", ((
        "        var receipt = runtime.Store.FindReceipt(messageId);",
        "        var receipt = runtime.Store.FindReceiptWithText(messageId);",
    ),)),
    Witness("broker-e-reply-conversation-lost", "store", "worker", ("--tombstone-case", "reply"), "TombstoneChecks.cs", ((
        "    private static MessageReceipt Render(Tombstone tombstone, string text) => new(new(tombstone.Id, tombstone.ConversationId,",
        "    private static MessageReceipt Render(Tombstone tombstone, string text) => new(new(tombstone.Id, tombstone.Id,",
    ),)),
    # Re-pointed by lane feature/broker-next-160 (N-modules-fixes-01): wait_for_receipt now reads its original the same
    # way, so the bare lookup line occurs twice; the anchor includes wait_for_reply's own signature (same intent).
    Witness("broker-e-wait-reply-skips-tombstone", "tools", "worker", ("--tombstone-case", "wait-reply"), "TombstoneChecks.cs", ((
        "    public Task<IReadOnlyList<MessageReceipt>> WaitForReply(Guid messageId, CancellationToken cancellationToken, int timeoutSeconds = 20)\n"
        "    {\n"
        "        ValidateTimeout(timeoutSeconds);\n"
        "        var original = runtime.Store.FindReceipt(messageId)?.Message",
        "    public Task<IReadOnlyList<MessageReceipt>> WaitForReply(Guid messageId, CancellationToken cancellationToken, int timeoutSeconds = 20)\n"
        "    {\n"
        "        ValidateTimeout(timeoutSeconds);\n"
        "        var original = runtime.Store.FindReceiptWithText(messageId)?.Message",
    ),)),
    Witness("broker-e-inbox-omits-acknowledged", "store", "worker", ("--tombstone-case", "inbox"), "TombstoneChecks.cs", ((
        '                history.AddRange(acknowledged.Select(id => Render(tombstones[id], "")));',
        "                _ = acknowledged;",
    ),)),
    Witness("broker-e-held-tombstoned", "store", "worker", ("--tombstone-case", "held"), "TombstoneChecks.cs", ((
        "    private bool Tombstoned(MessageReceipt receipt) => receipt.Status is MessageStatus.Delivered or MessageStatus.Refused &&",
        "    private bool Tombstoned(MessageReceipt receipt) => receipt.Status is MessageStatus.Delivered or MessageStatus.Refused or MessageStatus.Held &&",
    ),)),
    Witness("broker-e-acknowledged-count-toward-limit", "store", "worker", ("--tombstone-case", "capacity"), "TombstoneChecks.cs", ((
        '            if (retained.Count >= limits.MaximumMessages) Fail("message_limit");',
        '            if (retained.Count + tombstones.Count >= limits.MaximumMessages) Fail("message_limit");',
    ),)),
    Witness("broker-e-tombstone-cap-unbounded", "store", "worker", ("--tombstone-case", "capacity"), "TombstoneChecks.cs", ((
        '            if (retained.Count + tombstones.Count >= limits.MaximumTombstones) Fail("message_limit");',
        "            _ = limits.MaximumTombstones;",
    ),)),
    # A shorter grace, not none: with none, the EXISTING late-reply check fails too (its parent is a tombstone now).
    Witness("broker-e-grace-shortened", "store", "worker", ("--tombstone-case", "grace"), "TombstoneChecks.cs", ((
        "        var retainedAfter = Now - RecordGrace;",
        "        var retainedAfter = Now - TimeSpan.FromHours(12);",
    ),)),
    Witness("broker-e-journal-not-loaded", "store", "worker", ("--tombstone-case", "restart"), "TombstoneChecks.cs", ((
        """            ValidateState();
            LoadTombstones();
""",
        """            ValidateState();
""",
    ),)),
    Witness("broker-e-torn-tail-refused", "store", "worker", ("--tombstone-case", "restart"), "TombstoneChecks.cs", ((
        """        if (!header) return;
        journalLength = complete;""",
        """        if (used > 0) throw MalformedTombstones();
        if (!header) return;
        journalLength = complete;""",
    ),)),
    Witness("broker-e-journal-wins-over-snapshot", "store", "worker", ("--tombstone-case", "restart"), "TombstoneChecks.cs", ((
        "                if (!full.Contains(tombstone.Id)) AddTombstone(tombstone);",
        "                AddTombstone(tombstone);",
    ),)),
    # Tombstone bookkeeping in the snapshot: this build reads the new member, a 1.0.4 worker refuses it. (Leaving
    # empty-text records in the snapshot instead makes this build refuse its own state on the existing restart check.)
    Witness("broker-e-rollback-snapshot-member-added", "store", "worker", ("--tombstone-case", "rollback"), "TombstoneChecks.cs", ((
        "    private sealed record State(int Version, Guid BrokerId, List<PeerTrust> Peers, List<MessageReceipt> Messages);",
        "    private sealed record State(int Version, Guid BrokerId, List<PeerTrust> Peers, List<MessageReceipt> Messages, int Tombstones = 0);",
    ),)),
    # The commit path only: changing Tombstoned itself would also convert PaginationChecks' seeded paired-broker history
    # as the store opens, failing an EXISTING check. An acknowledgement on the wire is a commit.
    Witness("broker-e-peer-inbound-tombstoned", "store", "worker", ("--tombstone-case", "wire"), "TombstoneChecks.cs", (
        ("        var added = next.Messages.Where(Tombstoned).Select(ToTombstone).ToList();",
         "        var added = next.Messages.Where(receipt => receipt.Status is MessageStatus.Delivered or MessageStatus.Refused)"
         ".Select(ToTombstone).ToList();"),
        ("            next = next with { Messages = next.Messages.Where(receipt => !Tombstoned(receipt)).ToList() };",
         "            next = next with { Messages = next.Messages.Where(receipt => receipt.Status is not (MessageStatus.Delivered or "
         "MessageStatus.Refused)).ToList() };"),
    )),
    Witness("broker-e-receipts-route-sends-tombstone", "federation", "worker", ("--tombstone-case", "wire"), "TombstoneChecks.cs", ((
        "            Guid caller = RequirePeer(c); var receipt = store.FindReceiptWithText(id);",
        "            Guid caller = RequirePeer(c); var receipt = store.FindReceipt(id);",
    ),)),
    Witness("broker-e-wire-record-member-added", "models", "worker", ("--tombstone-case", "wire"), "TombstoneChecks.cs", ((
        """public sealed record MessageReceipt(BrokerMessage Message, MessageStatus Status,
    DateTimeOffset UpdatedAt, string? Reason = null, int Attempts = 0);""",
        """public sealed record MessageReceipt(BrokerMessage Message, MessageStatus Status,
    DateTimeOffset UpdatedAt, string? Reason = null, int Attempts = 0, bool Tombstone = false);""",
    ),)),
    # ---- lane feature/agentbroker-pin-automation ----
    # The exact pins moved into ONE embedded JSON document (lane broker-pins, owner decision 2026-10-27): its strict
    # schema, the bindings between fields, and the fail-closed consumers. Each runs one PinDocumentChecks or
    # PinStatusChecks group; the checks run after the --existing-tests-only cut-off, so the existing pins stay 148/196.
    Witness("pins-unknown-member-admitted", "pins", "desktop", ("--pin-document-case", "unknown-member"), "PinDocumentChecks.cs", ((
        "            if (!expected.Contains(property.Name, StringComparer.Ordinal))\n"
        '                throw new PinDocumentException(at + " has an unknown member " + Describe(property.Name));\n',
        "",
    ),)),
    Witness("pins-repeated-member-admitted", "pins", "desktop", ("--pin-document-case", "repeated-member"), "PinDocumentChecks.cs", ((
        "            if (!members.TryAdd(property.Name, property.Value))\n"
        '                throw new PinDocumentException(at + " repeats the member " + property.Name);',
        "            members[property.Name] = property.Value;",
    ),)),
    Witness("pins-missing-member-admitted", "pins", "desktop", ("--pin-document-case", "missing-member"), "PinDocumentChecks.cs", ((
        "        foreach (string name in expected)\n"
        '            if (!members.ContainsKey(name)) throw new PinDocumentException(at + " is missing the member " + name);\n',
        "",
    ),)),
    Witness("pins-schema-ignored", "pins", "desktop", ("--pin-document-case", "schema"), "PinDocumentChecks.cs", ((
        '            if (schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != "1")\n'
        '                throw new PinDocumentException("schema must be the number 1");\n',
        "",
    ),)),
    Witness("pins-lowercase-hash-admitted", "pins", "desktop", ("--pin-document-case", "formats"), "PinDocumentChecks.cs", ((
        'private static readonly Regex Hex64 = new(@"\\A[0-9A-F]{64}\\z", RegexOptions.CultureInvariant);',
        'private static readonly Regex Hex64 = new(@"\\A[0-9A-Fa-f]{64}\\z", RegexOptions.CultureInvariant);',
    ),)),
    Witness("pins-package-name-unbound", "pins", "desktop", ("--pin-document-case", "bindings"), "PinDocumentChecks.cs", ((
        '                if (pin.PackageFullName != "OpenAI.Codex_" + pin.ApplicationVersion + "_x64__" + CodexPublisherId)\n'
        '                    throw new PinDocumentException(at + ".packageFullName is not the OpenAI.Codex x64 package of its applicationVersion");\n',
        "",
    ),)),
    Witness("pins-directory-name-unbound", "pins", "desktop", ("--pin-document-case", "bindings"), "PinDocumentChecks.cs", ((
        '                if (directory != "openai.chatgpt-" + version + "-win32-x64")\n'
        '                    throw new PinDocumentException(at + ".directoryName is not the win32-x64 extension folder of its version");\n',
        "",
    ),)),
    Witness("pins-image-path-unchecked", "pins", "desktop", ("--pin-document-case", "image-paths"), "PinDocumentChecks.cs", ((
        "            if (!RelativeImage.IsMatch(property.Name))\n",
        "            if (property.Name.Length == 0)\n",
    ),)),
    Witness("pins-image-path-case-sensitive", "pins", "desktop", ("--pin-document-case", "image-paths"), "PinDocumentChecks.cs", ((
        "        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);",
        "        var names = new HashSet<string>(StringComparer.Ordinal);",
    ),)),
    Witness("pins-claude-hash-repeats", "pins", "desktop", ("--pin-document-case", "duplicates"), "PinDocumentChecks.cs", ((
        '            Unique(claude.Select(pin => pin.Sha256), "claudeCode", "sha256");\n',
        "",
    ),)),
    Witness("pins-desktop-refusal-is-mismatch", "desktop", "desktop", ("--pin-document-case", "fail-closed"), "PinDocumentChecks.cs", ((
        "    internal static IReadOnlyList<PackageProfile> PackagesOf(PinDocument pins) => pins.Error is not null\n"
        '        ? throw new DesktopAdapterException("pin_document_invalid")\n',
        "    internal static IReadOnlyList<PackageProfile> PackagesOf(PinDocument pins) => pins.Error is not null\n"
        "        ? Array.Empty<PackageProfile>()\n",
    ),)),
    Witness("pins-router-refusal-is-mismatch", "router", "desktop", ("--pin-document-case", "fail-closed"), "PinDocumentChecks.cs", ((
        "    internal static IReadOnlyList<RouterPin> PinsOf(PinDocument pins) => pins.Error is not null\n"
        '        ? throw new DesktopAdapterException("pin_document_invalid")\n',
        "    internal static IReadOnlyList<RouterPin> PinsOf(PinDocument pins) => pins.Error is not null\n"
        "        ? Array.Empty<RouterPin>()\n",
    ),)),
    Witness("pins-vscode-refusal-is-mismatch", "vscode", "desktop", ("--pin-document-case", "fail-closed"), "PinDocumentChecks.cs", ((
        "    internal static IReadOnlyList<ExtensionProfile> ProfilesOf(PinDocument pins) => pins.Error is not null\n"
        '        ? throw new DesktopAdapterException("pin_document_invalid")\n',
        "    internal static IReadOnlyList<ExtensionProfile> ProfilesOf(PinDocument pins) => pins.Error is not null\n"
        "        ? Array.Empty<ExtensionProfile>()\n",
    ),)),
    Witness("pins-refusal-lent-publisher-trust", "desktop", "desktop", ("--pin-document-case", "fail-closed"), "PinDocumentChecks.cs", ((
        '        "running_vscode_image_mismatch",\n    };',
        '        "running_vscode_image_mismatch", "pin_document_invalid",\n    };',
    ),)),
    Witness("pins-claude-status-silent", "claude", "worker", ("--pin-status-case", "claude-refused-document"), "PinStatusChecks.cs", ((
        "        if (pinDocumentError() is { } refused)\n",
        "        if (pinDocumentError() is { } refused && refused.Length < 0)\n",
    ),)),
    Witness("pins-codex-status-generic", "codex", "worker", ("--pin-status-case", "codex-refused-document"), "PinStatusChecks.cs", ((
        '            "pin_document_invalid" => "AgentBroker\'s pin document was refused ("',
        '            "pin_document_invalid_unmatched" => "AgentBroker\'s pin document was refused ("',
    ),)),
    # The Native status names a client newer than every pin, first and within the pane's 160-character line, and says a
    # reviewed re-pin arrives as a module update (lane broker-pins item 4). Reporting only.
    Witness("repin-claude-newer-silent", "claude", "worker", ("--pin-status-case", "claude-newer"), "PinStatusChecks.cs", ((
        '        if (code == "unsupported_public_version" && EngineProfile.IsNewerThanEveryVerified(version))\n',
        '        if (code == "unsupported_public_version" && EngineProfile.IsNewerThanEveryVerified(version) && version is null)\n',
    ),)),
    Witness("repin-codex-newer-silent", "codex", "worker", ("--pin-status-case", "codex-newer"), "PinStatusChecks.cs", ((
        "        if (newer is not null) return new(error.Code, newer);",
        "        _ = newer;",
    ),)),
    Witness("repin-newer-sentence-last", "diagnostic", "worker", ("--pin-status-case", "claude-newer"), "PinStatusChecks.cs", ((
        '        => client + " " + version + " installed is newer than every verified build; a reviewed re-pin arrives as a module update. Verified " +\n'
        '            verified + ".";',
        '        => client + " " + version + " installed; verified " + verified + ". It is newer than every verified build; a reviewed re-pin arrives as a module update.";',
    ),)),
    Witness("repin-newer-ignores-zero-revision", "pins", "desktop", ("--pin-document-case", "newer"), "PinDocumentChecks.cs", ((
        "        => new(value.Major, value.Minor, Math.Max(value.Build, 0), Math.Max(value.Revision, 0));",
        "        => value;",
    ),)),
    Witness("repin-running-version-dropped", "vscode", "desktop", ("--pin-document-case", "running-version"), "PinDocumentChecks.cs", ((
        '                throw new DesktopAdapterException("unsupported_running_vscode_profile",\n'
        "                    PublisherEngineVersion(engine.Image, Path.GetFullPath(ExtensionsRoot)));",
        '                throw new DesktopAdapterException("unsupported_running_vscode_profile");',
    ),)),
    Witness("repin-installed-version-dropped", "vscode", "desktop", ("--pin-document-case", "running-version"), "PinDocumentChecks.cs", ((
        "            { throw new DesktopAdapterException(error.Code, NewestRunningVersion(engines)); }",
        "            { throw; }",
    ),)),
)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def sha256(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def refuse_links(path: Path) -> None:
    """Check only this explicit path and its parents, never a directory tree."""
    for item in (path, *path.parents):
        if item.is_symlink() or (hasattr(item, "is_junction") and item.is_junction()):
            raise RuntimeError("Linked mutation/evidence path refused: " + str(item))


def read_source(relative: str) -> bytes:
    path = ROOT / relative
    refuse_links(path)
    return path.read_bytes()


def replace_exact(raw: bytes, before: str, after: str, expected_count: int = 1) -> bytes:
    newline = "\r\n" if b"\r\n" in raw else "\n"
    old = before.replace("\n", newline).encode("utf-8")
    new = after.replace("\n", newline).encode("utf-8")
    if raw.count(old) != expected_count:
        raise RuntimeError("Mutation anchor did not match its exact expected occurrence count.")
    return raw.replace(old, new, expected_count)


class Runner:
    def __init__(self, evidence: Path) -> None:
        self.evidence = evidence
        self.original = {relative: read_source(relative) for relative in SOURCES.values()}
        self.owned_current = dict(self.original)
        self.serial = 0
        self.steps: list[dict] = []
        backups = []
        for index, (relative, raw) in enumerate(self.original.items(), start=1):
            name = f"source-{index:02d}.bytes"
            with (self.evidence / name).open("xb") as stream:
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            backups.append({"relativeSource": relative, "backupName": name,
                            "bytes": len(raw), "sha256": sha256(raw)})
        # Persist all original uncommitted bytes before any build or mutation.
        # A host crash can skip finally; recovery must remain an explicit action.
        with (self.evidence / "source-backups.json").open("x", encoding="utf-8") as manifest:
            json.dump({"schema": 1, "sources": backups}, manifest, indent=2)
            manifest.write("\n")
            manifest.flush()
            os.fsync(manifest.fileno())

    def expect_restored(self) -> None:
        for relative, original in self.original.items():
            if read_source(relative) != original:
                raise RuntimeError("Source is not at its exact initial bytes: " + relative)

    def mutate(self, witness: Witness) -> None:
        self.expect_restored()
        relative = SOURCES[witness.source]
        changed = self.original[relative]
        for index, (before, after) in enumerate(witness.replacements):
            expected = witness.replacement_counts[index] if witness.replacement_counts else 1
            changed = replace_exact(changed, before, after, expected)
        self.owned_current[relative] = changed
        (ROOT / relative).write_bytes(changed)

    def restore(self) -> None:
        conflicts = []
        for relative, original in self.original.items():
            current = read_source(relative)
            if current not in (original, self.owned_current[relative]):
                conflicts.append(relative)
                continue  # Never overwrite a concurrent edit by somebody else.
            if current != original:
                (ROOT / relative).write_bytes(original)
            self.owned_current[relative] = original
        if conflicts:
            raise RuntimeError("Concurrent source edits prevent automatic restoration: " + ", ".join(conflicts))
        self.expect_restored()

    @staticmethod
    def stop_owned_tree(process: subprocess.Popen) -> None:
        if process.poll() is not None:
            return
        # The Popen handle remains owned/open, so this is only our exact child
        # lifetime. Never target a pre-existing application or regression PID.
        if os.name == "nt":
            stopped = subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                timeout=20, check=False,
            )
            if stopped.returncode != 0 and process.poll() is None:
                raise RuntimeError("Could not stop the timed-out owned build/test tree.")
        else:
            process.kill()
        process.wait(timeout=20)

    def invoke(self, label: str, args: list[str], timeout: int) -> dict:
        self.serial += 1
        stem = f"{self.serial:03d}-{label}"
        stdout_path, stderr_path = self.evidence / (stem + ".out"), self.evidence / (stem + ".err")
        command = ["pwsh", "-NoProfile", "-File", str(WRAPPER.relative_to(ROOT)), *args]
        entry = {"label": label, "command": command, "stdout": stdout_path.name,
                 "stderr": stderr_path.name, "startedUtc": utc_now()}
        start = time.monotonic()
        print("RUN " + label, flush=True)
        with stdout_path.open("xb") as out, stderr_path.open("xb") as err:
            process = subprocess.Popen(command, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=out, stderr=err)
            entry["ownedPid"] = process.pid
            try:
                entry["exitCode"] = process.wait(timeout=timeout)
            except BaseException:
                self.stop_owned_tree(process)
                entry["interrupted"] = True
                raise
            finally:
                entry["elapsedSeconds"] = round(time.monotonic() - start, 3)
                self.steps.append(entry)
        from mutation_process import decode_output
        for path in (stdout_path, stderr_path):
            if path.stat().st_size > 64 * 1024 * 1024:
                raise RuntimeError("Owned mutation evidence exceeded its output bound.")
        entry["output"] = decode_output(stdout_path.read_bytes()) + "\n" + decode_output(stderr_path.read_bytes())
        print(f"EXIT {label}: {entry['exitCode']}", flush=True)
        return entry

    def build(self, family: str, label: str) -> None:
        result = self.invoke(label, ["dotnet", "build", PROJECTS[family], "-c", "Release", "--no-restore",
                                    "--disable-build-servers", "-warnaserror", "-p:UseSharedCompilation=false"], 600)
        if result["exitCode"] != 0:
            raise RuntimeError("Healthy/mutant build failed; this is not a killed reporting mutation: " + label)

    def test(self, family: str, label: str, args: tuple[str, ...], *, failure_file: str | None = None,
             existing: bool = False, focused: bool = False, expected_assertions: int | None = None) -> dict:
        result = self.invoke(label, ["dotnet", str(ASSEMBLIES[family].relative_to(ROOT)), *args], 180)
        output = result["output"]
        if failure_file is not None:
            if result["exitCode"] != 1 or failure_file not in output:
                raise RuntimeError("Mutation did not fail cleanly with exit 1 in its specific new test file: " + label)
            result["expectedFailureFile"] = failure_file
            result["expectedOutcome"] = "new_test_failed"
        else:
            if result["exitCode"] != 0:
                raise RuntimeError("A required healthy or existing regression test failed: " + label)
            result["expectedOutcome"] = "pass"
            matches = re.findall(r"(?:RESULT=PASS assertions=|checks passed: )(\d+)", output)
            result["assertions"] = int(matches[-1]) if matches else None
            if existing and result["assertions"] != EXISTING_COUNTS[family]:
                raise RuntimeError("Existing-test assertion inventory changed unexpectedly: " + label)
            if focused and result["assertions"] != FOCUSED_COUNTS[family]:
                raise RuntimeError("Focused diagnostic assertion inventory changed unexpectedly: " + label)
            if expected_assertions is not None and result["assertions"] != expected_assertions:
                raise RuntimeError("Focused concurrency assertion inventory changed unexpectedly: " + label)
        return result

    def healthy(self, prefix: str, families: tuple[str, ...]) -> None:
        for family in families:
            self.build(family, prefix + "-build-" + family)
        for family in families:
            self.test(family, prefix + "-new-" + family, ("--pin-diagnostics-only",), focused=True)
            self.test(family, prefix + "-existing-" + family, ("--existing-tests-only",), existing=True)
        if "worker" in families:
            self.test("worker", prefix + "-deferred-claim", ("--deferred-claim-only",), expected_assertions=4)

    def witness(self, witness: Witness) -> dict:
        record = {"name": witness.name, "classification": witness.kind, "source": SOURCES[witness.source], "checks": []}
        self.mutate(witness)
        try:
            self.build(witness.family, witness.name + "-build")
            if witness.name == "claude-reporting":
                for scenario in CLAUDE_SCENARIOS:
                    outcome = self.test("worker", witness.name + "-" + scenario,
                                        ("--claude-pin-diagnostics-case", scenario),
                                        failure_file=None if scenario == "early-reject" else witness.failure_file)
                    record["checks"].append({"scenario": scenario, "exitCode": outcome["exitCode"],
                                             "expectedOutcome": outcome["expectedOutcome"]})
            else:
                outcome = self.test(witness.family, witness.name + "-new", witness.test_args,
                                    failure_file=witness.failure_file)
                record["checks"].append({"exitCode": outcome["exitCode"], "expectedOutcome": outcome["expectedOutcome"]})
            if witness.kind == "concurrency":
                record["existingRegression"] = {"runWhileMutated": False,
                    "reason": "This witness intentionally restores the known legacy deferred-claim race; the old 130 checks run after source restoration."}
            else:
                old = self.test(witness.family, witness.name + "-existing", ("--existing-tests-only",), existing=True)
                record["existingRegression"] = {"runWhileMutated": True, "exitCode": old["exitCode"], "assertions": old["assertions"]}
            record["verdict"] = "FIRED"
            return record
        finally:
            self.restore()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--apply", action="store_true", help="Run serial builds and temporary reporting-only mutations.")
    parser.add_argument("--case", action="append", choices=[item.name for item in WITNESSES],
                        help="Select an explicit witness; repeat to select more than one.")
    parser.add_argument("--output", type=Path, help="A new artifacts/pin-diagnostic-mutations-<label> evidence directory.")
    args = parser.parse_args()
    selected = [item for item in WITNESSES if not args.case or item.name in args.case]
    families = tuple(family for family in PROJECTS if any(item.family == family for item in selected))
    if not args.apply:
        print(json.dumps({"mode": "plan_only", "buildsStarted": False, "sourcesChanged": False,
                          "selectedFamilies": families, "expectedFailureExitCode": 1,
                          "witnesses": [{"name": item.name, "source": SOURCES[item.source],
                                         "classification": item.kind, "newFailureFile": item.failure_file,
                                         "existingAssertions": EXISTING_COUNTS[item.family] if item.kind == "reporting" else None}
                                        for item in selected]}, indent=2))
        return 0
    artifacts = ROOT / "artifacts"
    evidence = args.output or artifacts / ("pin-diagnostic-mutations-" + uuid.uuid4().hex[:12])
    if not evidence.is_absolute():
        evidence = ROOT / evidence
    refuse_links(evidence)
    evidence = evidence.resolve()
    if evidence.parent != artifacts.resolve() or not re.fullmatch(r"pin-diagnostic-mutations-[A-Za-z0-9-]{1,80}", evidence.name):
        raise ValueError("Evidence must be a new artifacts/pin-diagnostic-mutations-<label> directory.")
    artifacts.mkdir(exist_ok=True)
    evidence.mkdir(exist_ok=False)
    lock_path = artifacts / ".pin-diagnostics-mutation.lock"
    refuse_links(lock_path)
    lock = os.open(lock_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    os.write(lock, (str(os.getpid()) + "\n").encode("ascii"))
    report = {"schema": 1, "startedUtc": utc_now(), "evidence": evidence.relative_to(ROOT).as_posix(),
              "sourceBackupsManifest": "source-backups.json", "focusedAssertions": FOCUSED_COUNTS,
              "existingAssertions": EXISTING_COUNTS, "deferredClaimAssertions": 4, "selectedFamilies": families,
              "expectedFailureExitCode": 1, "witnesses": [], "verdict": "INCOMPLETE"}
    runner = None
    failed = False
    interrupted = False
    try:
        runner = Runner(evidence)
        report["sourceSha256Before"] = {relative: sha256(raw) for relative, raw in runner.original.items()}
        runner.healthy("baseline", families)
        for witness in selected:
            report["witnesses"].append(runner.witness(witness))
        report["verdict"] = "PASS"
    except BaseException as error:
        failed = True
        interrupted = isinstance(error, KeyboardInterrupt)
        report["error"] = type(error).__name__ + ": " + str(error)
        report["verdict"] = "INTERRUPTED" if interrupted else "FAIL"
    finally:
        if runner is not None:
            try:
                runner.restore()
                report["sourceBytesRestored"] = True
                report["sourceSha256After"] = {relative: sha256(read_source(relative)) for relative in runner.original}
                if not interrupted:
                    runner.healthy("restored", families)
                    report["restoredBuildAndRegression"] = "PASS"
            except BaseException as error:
                failed = True
                report["verdict"] = "FAIL"
                report["restorationOrFinalCheckError"] = type(error).__name__ + ": " + str(error)
            report["steps"] = [{key: value for key, value in step.items() if key != "output"} for step in runner.steps]
        report["finishedUtc"] = utc_now()
        try:
            (evidence / "results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        finally:
            os.close(lock)
            lock_path.unlink()
    print(json.dumps({"verdict": report["verdict"], "witnesses": len(report["witnesses"]),
                      "sourceBytesRestored": report.get("sourceBytesRestored", False),
                      "report": str(evidence / "results.json")}, indent=2), flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
