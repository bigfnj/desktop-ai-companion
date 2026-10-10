using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.CodingAgent;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// The brain's backend seam over a coding-agent CLI (lane feature/cli-backend, aibrain 1.3.0). AiBrain builds the
    /// same messages it builds for any backend: the persona as the system message (CompanionName, Disposition and the
    /// user's name included, BuildSystemPrompt), the screen as the user message, and the screenshot as its image on a
    /// vision turn. This hands the system message to the CLI as its short system prompt (Claude Code) or its
    /// instructions file (Codex), the user text to its stdin, and the first image inline or as `-i`, through the
    /// module's one runner, so a remark, an audition sample and Validate share one single-flight gate.
    ///
    /// The model the CLI runs on and its effort come through the CONSTRUCTOR, from the CLI card's choice (lane
    /// feature/cli-model-effort, aibrain 1.5.0: CliClaudeModel / CliCodexModel and their efforts), and every call names
    /// both. ChatAsync's model argument is still ignored, on purpose: it is the brain's model policy's id, which on a CLI
    /// slot is the CLI's name (AiSettings.CliModelName, there for the brain's logs) and on any other path the local or
    /// cloud slot's model, and neither is a value either CLI takes. Reusing it would have meant teaching the slot-model
    /// policy (ChooseModel, the inventory, substitution) about aliases it never lists, so the CLI's choice travels beside
    /// it instead. Nothing is loaded into any local server, so the lifecycle members are no-ops: no warm-up, nothing to
    /// unload, and "available" means the binary is where the locator looks (no process is started to find out). A
    /// failure is thrown as CodingAgentCliException, which AI Brain's retry predicate does not retry.
    /// </summary>
    internal sealed class CodingAgentBackend : ICompanionBrainBackend
    {
        private readonly CodingAgentCli _cli;
        private readonly CodingAgentKind _agent;
        private readonly TimeSpan _timeout;
        private readonly string _model;
        private readonly string _effort;

        /// <param name="model">The CLI card's model for this CLI, as the runner takes it: a Claude Code alias or a Codex slug,
        /// or "" for none (Claude Code's default, Codex's automatic pick). Checked by the runner, never here; only trimmed
        /// here, as CheckChoice trims it, so that ChosenModelTakesNoImages looks up the slug the runner will pass and not a
        /// padded copy of it (review finding F4: a hand-edited "text-only-low" with spaces round it read as not listed, so
        /// the screen was captured and the runner then refused the call as ModelCannotSee instead of the text turn).</param>
        /// <param name="effort">The CLI card's effort for this CLI (low, medium or high); "" is the runner's default.</param>
        internal CodingAgentBackend(CodingAgentCli cli, CodingAgentKind agent, TimeSpan timeout, string model, string effort)
        {
            _cli = cli ?? new CodingAgentCli(null, null);
            _agent = agent;
            _timeout = timeout > TimeSpan.Zero ? timeout : CodingAgentCli.DefaultCallTimeout;
            _model = (model ?? "").Trim();
            _effort = effort ?? "";
        }

        /// <summary>For the self-test: which CLI this backend calls, through which runner, on which model and effort.</summary>
        internal CodingAgentKind AgentForDiagnostics { get { return _agent; } }
        internal CodingAgentCli CliForDiagnostics { get { return _cli; } }
        internal string ModelForDiagnostics { get { return _model; } }
        internal string EffortForDiagnostics { get { return _effort; } }

        /// <summary>
        /// True when the model this backend sends to is one its CLI's own catalog says takes NO images: only a Codex model
        /// the user chose, read from the runner's cache (CodexModelTakesImages, which never starts Codex). AiBrain asks it
        /// before a capture and reads the screen as text instead (AiBrain.SendsScreenshot). False when it can see, when
        /// the catalog does not list it or is not cached yet (not knowing is not a no, F102), on the automatic pick (which
        /// is image-capable for a screenshot by construction) and on Claude Code, whose three aliases all take images.
        /// </summary>
        internal bool ChosenModelTakesNoImages()
        {
            if (_agent != CodingAgentKind.Codex || _model.Length == 0) return false;
            return _cli.CodexModelTakesImages(_model) == false;
        }

        public async Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            var system = new StringBuilder();
            var prompt = new StringBuilder();
            byte[] image = null;
            if (messages != null)
            {
                foreach (ChatMessage message in messages)
                {
                    if (message == null) continue;
                    if (string.Equals(message.Role, "system", StringComparison.Ordinal))
                    {
                        if (system.Length > 0) system.Append("\n\n");
                        system.Append(message.Content ?? "");
                        continue;
                    }
                    if (prompt.Length > 0) prompt.Append("\n\n");
                    prompt.Append(message.Content ?? "");
                    if (image == null && message.ImagesBase64 != null && message.ImagesBase64.Length > 0 &&
                        !string.IsNullOrEmpty(message.ImagesBase64[0]))
                        image = Convert.FromBase64String(message.ImagesBase64[0]);
                }
            }
            // jsonFormat is not passed on: neither CLI constrains its output, and the persona already asks for the
            // {"text","emotion"} object, which AiBrain.Parse finds fenced or bare.
            CliAnswer answer = await _cli.AskAsync(new CliRequest
            {
                Agent = _agent,
                SystemPrompt = system.ToString(),
                Prompt = prompt.ToString(),
                ImagePng = image,
                Timeout = _timeout,
                Purpose = image != null ? "remark-vision" : "remark",
                Model = _model,
                Effort = _effort,
            }, ct).ConfigureAwait(false);
            if (answer.Outcome == CliOutcome.Cancelled) throw new OperationCanceledException(ct);
            if (!answer.Ok) throw new CodingAgentCliException(_agent, answer);
            return answer.Text;
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            return Task.FromResult(_cli.Locate(_agent) != null);
        }

        public Task<bool> EnsureServerAsync(CancellationToken ct)
        {
            return IsAvailableAsync(ct);
        }

        public Task WarmUpAsync(string model, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task UnloadAsync(string model, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            // The runner is the module's, shared with every other brain and with the pane's Validate; nothing here owns it.
        }
    }
}
