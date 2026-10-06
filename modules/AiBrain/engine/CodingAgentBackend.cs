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
    /// The model argument is ignored on purpose: Claude Code runs on its default and Codex on the runner's pick, and
    /// the id AiBrain passes is the CLI's name (AiSettings.CliModelName), there for its logs. Nothing is loaded into any
    /// local server, so the lifecycle members are no-ops: no warm-up, nothing to unload, and "available" means the
    /// binary is where the locator looks (no process is started to find out). A failure is thrown as
    /// CodingAgentCliException, which AI Brain's retry predicate does not retry.
    /// </summary>
    internal sealed class CodingAgentBackend : ICompanionBrainBackend
    {
        private readonly CodingAgentCli _cli;
        private readonly CodingAgentKind _agent;
        private readonly TimeSpan _timeout;

        internal CodingAgentBackend(CodingAgentCli cli, CodingAgentKind agent, TimeSpan timeout)
        {
            _cli = cli ?? new CodingAgentCli(null, null);
            _agent = agent;
            _timeout = timeout > TimeSpan.Zero ? timeout : CodingAgentCli.DefaultCallTimeout;
        }

        /// <summary>For the self-test: which CLI this backend calls, and through which runner.</summary>
        internal CodingAgentKind AgentForDiagnostics { get { return _agent; } }
        internal CodingAgentCli CliForDiagnostics { get { return _cli; } }

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
