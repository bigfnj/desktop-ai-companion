using System;
using AgentBroker.Observations;
using DesktopAICompanion.ModuleKit.Testing;
using DesktopAICompanion.ModuleKit;
using System.Text.Json;
using System.Linq;

namespace DesktopAICompanion.AgentFlow
{
    public sealed partial class AgentFlowModule
    {
        // UI-thread only. The shared payload contains exactly a count and timestamp, never a card or conversation.
        internal void PublishBrokerApprovalCount(int? count, DateTimeOffset observedAt)
        {
            if (_host == null || _shuttingDown) return;
            string value = count is >= 0 and <= CodexApprovalObservation.MaximumCount
                ? new CodexApprovalObservation(count.Value, observedAt).ToJson() : "";
            try { _host.PublishContext(Info.Id, CodexApprovalObservation.ContextKey, value); } catch { }
        }

        private void ClearBrokerApprovalObservation()
        {
            if (_host == null) return;
            try { _host.PublishContext(Info.Id, CodexApprovalObservation.ContextKey, ""); } catch { }
        }

        private static bool SelfCheckBrokerObservation(SelfTestProbe probe)
        {
            var host = new RecordingHost();
            var module = new AgentFlowModule { _host = host };
            var pass = new SweepPass(0, false, false, false, null, null);
            var view = new PromptView { Agent = CdpApprover.AgentCodex, TargetId = "synthetic-target", CardId = "first-card", UnsafeHeader = "SYNTHETIC_PRIVATE_PROMPT" };
            pass.Handle(view, out _);
            pass.Handle(view, out _);
            pass.Handle(new PromptView { Agent = CdpApprover.AgentClaude, TargetId = "other-target" }, out _);
            probe.Check("BROKER_PROMPT_COUNT: repeated reads of one Codex card count once and Claude prompts do not count",
                pass.VisibleCodexApprovalCount == 1 && pass.CodexPromptCountKnown);
            pass.Handle(new PromptView { Agent = CdpApprover.AgentCodex, TargetId = view.TargetId, CardId = "second-card" }, out _);
            probe.Check("BROKER_PROMPT_MULTIPLE_CARDS: two distinct waiting cards in one panel count as two prompts", pass.VisibleCodexApprovalCount == 2);
            var provenance = new SweepPass(0, false, false, false, null, null);
            provenance.Handle(new PromptView { Agent = CdpApprover.AgentCodex, TargetId = "no-card-id" }, out _);
            probe.Check("BROKER_PROMPT_CARD_ID_REQUIRED: a card without stable element identity makes the count unavailable", !provenance.CodexPromptCountKnown);
            CheckBrokerCensus(probe);
            var now = DateTimeOffset.UtcNow;
            module.PublishBrokerApprovalCount(pass.VisibleCodexApprovalCount, now);
            string raw = host.ReadContext(CodexApprovalObservation.ContextKey);
            using (var json = JsonDocument.Parse(raw))
                probe.Check("BROKER_PROMPT_PRIVACY: the published payload contains exactly a count and timestamp, no target, command or prompt",
                    json.RootElement.EnumerateObject().Count() == 2 && !raw.Contains("SYNTHETIC_PRIVATE_PROMPT") && !raw.Contains("synthetic-target"));
            probe.Check("BROKER_PROMPT_PUBLISH: a complete observation preserves its original count and timestamp",
                CodexApprovalObservation.Parse(raw) == new CodexApprovalObservation(2, now));
            module.PublishBrokerApprovalCount(0, now);
            probe.Check("BROKER_PROMPT_ZERO: an observed zero is distinct from unavailable",
                CodexApprovalObservation.Parse(host.ReadContext(CodexApprovalObservation.ContextKey))?.Count == 0);
            pass.Handle(new PromptView { Agent = CdpApprover.AgentCodex }, out _);
            probe.Check("BROKER_PROMPT_UNKNOWN: a prompt without target provenance makes this aggregate observation unavailable", !pass.CodexPromptCountKnown);
            module.PublishBrokerApprovalCount(null, now);
            probe.Check("BROKER_PROMPT_UNAVAILABLE: a failed or absent sweep clears the observation rather than publishing zero",
                host.ReadContext(CodexApprovalObservation.ContextKey) == "");
            module.PublishBrokerApprovalCount(1, now);
            module.ClearBrokerApprovalObservation();
            probe.Check("BROKER_PROMPT_SHUTDOWN_CLEAR: shutdown's publication withdraws the retained value", host.ReadContext(CodexApprovalObservation.ContextKey) == "");
            module.BeginShutdown();
            module.PublishBrokerApprovalCount(1, now);
            probe.Check("BROKER_PROMPT_LATE_WORK: a completed worker cannot publish after shutdown began", host.ReadContext(CodexApprovalObservation.ContextKey) == "");
            var teardownHost = new RecordingHost();
            var teardown = new AgentFlowModule { _host = teardownHost, _pressArmed = true };
            teardown.PublishBrokerApprovalCount(1, now);
            bool guardedAtWithdrawal = false;
            Action<string> onContext = key =>
            {
                if (key == CodexApprovalObservation.ContextKey && teardownHost.ReadContext(key) == "")
                    guardedAtWithdrawal = teardown._shuttingDown && !teardown.StillArmed();
            };
            teardownHost.ContextChanged += onContext;
            try { teardown.Shutdown(); }
            finally { teardownHost.ContextChanged -= onContext; }
            probe.Check("BROKER_PROMPT_SHUTDOWN_ORDER: pressing is disabled before shutdown publishes to other modules", guardedAtWithdrawal);
            return true;
        }

        private static void CheckBrokerCensus(SelfTestProbe probe)
        {
            string url = "vscode-webview://synthetic/index.html?" + CdpApprover.CodexTargetMarker;
            foreach (string scenario in new[] { "missing-browser", "bad-list", "bad-target", "attach-refused", "no-answer", "unreachable", "evaluation-error", "complete-zero", "empty-directory" })
            {
                var target = new FakeCdpServer.Target { Id = "synthetic-codex", Url = url, EvaluateResult = "none" };
                if (scenario == "attach-refused") target.RefuseAttach = true;
                if (scenario == "no-answer") target.EvaluateResult = null;
                if (scenario == "unreachable") target.EvaluateResult = "unreachable";
                if (scenario == "evaluation-error") target.Throws = true;
                using (var server = new FakeCdpServer(scenario == "empty-directory" ? new FakeCdpServer.Target[0] : new[] { target }))
                {
                    if (scenario == "missing-browser") server.VersionReplyOverride = "{}";
                    if (scenario == "bad-list") server.TargetListReplyOverride = "{}";
                    if (scenario == "bad-target") server.TargetListReplyOverride = "[{\"url\":\"unresolvable-panel\"}]";
                    var pass = new SweepPass(server.Port, false, false, false, null, null);
                    SweepOnce(server.Port, pass, null, 1500, out _, out _);
                    bool complete = scenario == "complete-zero" || scenario == "empty-directory";
                    probe.Check("BROKER_PROMPT_CENSUS: " + scenario + " distinguishes unavailable from an observed zero",
                        pass.CodexPromptCountKnown == complete && pass.VisibleCodexApprovalCount == 0);
                }
            }
        }
    }
}
