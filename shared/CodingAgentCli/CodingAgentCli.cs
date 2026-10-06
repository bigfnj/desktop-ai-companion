using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;          // AiExecutablePolicy: AI Brain's executable trust rules, source-linked here too
using DesktopAICompanion.ModuleKit;   // AtomicFile

// The coding-agent CLI runner (lane feature/cli-backend, 2026-10-06): AI Brain and Remembrance can run their
// language-model work through Claude Code (`claude`) or Codex (`codex`) instead of a model server, and both compile
// THIS file by source link (AiBrain.csproj, Remembrance.csproj), the way PetStudio links the host's parser. One copy,
// because the flags, the trust rules and the error wording are one policy: a second copy would drift the first time
// either CLI moved a flag. It lives outside both module folders, in shared/, because a module cannot reference
// another module (separate load contexts) and src/ is the host's. The publish freshness check follows the link, so a
// change here makes BOTH payloads stale, which is the truth.
//
// What it does, and why each piece is shaped the way it is, is recorded at the site below. The refused alternatives,
// briefly: going through the npm shims (cmd.exe quoting; the shim is a script, not the binary); a private CODEX_HOME
// with a copied auth.json (Codex refresh tokens are single-use, so the copy that renews first signs the user out of
// every other Codex they run); reading ~/.codex/models_cache.json for the model (every Codex app on the machine
// rewrites it with ITS version's list); `claude --bare` (it wants an API key instead of the subscription login).
namespace DesktopAICompanion.CodingAgent
{
    /// <summary>Which coding-agent CLI a call goes through. None is "not a CLI": the module's own backend.</summary>
    internal enum CodingAgentKind { None = 0, Claude = 1, Codex = 2 }

    /// <summary>The words and file names each CLI goes by. The stored ids are what both modules persist.</summary>
    internal static class CodingAgents
    {
        internal const string ClaudeId = "claude";
        internal const string CodexId = "codex";

        internal static CodingAgentKind FromId(string id)
        {
            string value = (id ?? "").Trim();
            if (string.Equals(value, ClaudeId, StringComparison.OrdinalIgnoreCase)) return CodingAgentKind.Claude;
            if (string.Equals(value, CodexId, StringComparison.OrdinalIgnoreCase)) return CodingAgentKind.Codex;
            return CodingAgentKind.None;
        }

        internal static string IdOf(CodingAgentKind agent)
        {
            if (agent == CodingAgentKind.Claude) return ClaudeId;
            if (agent == CodingAgentKind.Codex) return CodexId;
            return "";
        }

        /// <summary>The product's own name, as its maker writes it.</summary>
        internal static string ProductName(CodingAgentKind agent)
        {
            return agent == CodingAgentKind.Codex ? "Codex" : "Claude Code";
        }

        /// <summary>The radio option both modules show for this CLI.</summary>
        internal static string ChoiceLabel(CodingAgentKind agent)
        {
            return agent == CodingAgentKind.Codex ? "Codex CLI" : "Claude Code CLI";
        }

        /// <summary>Whose servers the CLI sends a prompt to: the sentence a summary file and the pane say.</summary>
        internal static string Vendor(CodingAgentKind agent)
        {
            return agent == CodingAgentKind.Codex ? "OpenAI" : "Anthropic";
        }

        internal static string ExecutableName(CodingAgentKind agent)
        {
            return agent == CodingAgentKind.Codex ? "codex.exe" : "claude.exe";
        }

        /// <summary>What a user types to sign the CLI in, said wherever a call found it signed out.</summary>
        internal static string SignInHint(CodingAgentKind agent)
        {
            return agent == CodingAgentKind.Codex
                ? "run codex login in a terminal"
                : "run claude in a terminal and sign in with /login";
        }
    }

    /// <summary>How a CLI call ended, in the classes the pane names in plain words.</summary>
    internal enum CliOutcome
    {
        Ok,
        NotInstalled,
        NotSignedIn,
        SignInExpired,
        /// <summary>The server refused the model because this CLI is too old for it ("requires a newer version of
        /// Codex"): the answer is Update CLI.</summary>
        CliTooOld,
        ModelRefused,
        TimedOut,
        Cancelled,
        Busy,
        NoAnswer,
        NoPrivateFolder,
        Failed,
    }

    /// <summary>One call: the system prompt (the persona, or the summarizer's one line), the prompt that goes on
    /// stdin, an optional PNG, and the bound.</summary>
    internal sealed class CliRequest
    {
        internal CodingAgentKind Agent;
        internal string SystemPrompt;
        internal string Prompt;
        internal byte[] ImagePng;
        internal TimeSpan Timeout;
        /// <summary>The log's word for the call ("remark", "summary", "validate"): never the prompt.</summary>
        internal string Purpose;
    }

    /// <summary>What a call produced. <see cref="Said"/> is a bounded one-line excerpt of the CLI's own words on a
    /// failure, for the PANE only: it can carry an account or an organisation name, so it never reaches the log.</summary>
    internal sealed class CliAnswer
    {
        internal CliOutcome Outcome = CliOutcome.Failed;
        internal string Text = "";
        internal string Model = "";
        internal string Version = "";
        internal int ExitCode;
        internal long ElapsedMilliseconds;
        internal long InputTokens = -1;
        internal string Said = "";
        internal bool Ok { get { return Outcome == CliOutcome.Ok; } }
    }

    /// <summary>A failed CLI call, for a caller that reports failure by throwing (AI Brain's backend seam). Derives from
    /// Exception itself and nothing more specific, so AI Brain's retry predicate (AiEndpointPolicy.IsRetryable) never
    /// retries it: a second CLI call after a sign-in failure or a timeout costs another call for the same answer.</summary>
    internal sealed class CodingAgentCliException : Exception
    {
        internal CodingAgentCliException(CodingAgentKind agent, CliAnswer answer)
            : base(CodingAgentCliText.Describe(agent, answer, null))
        {
            Agent = agent;
            Answer = answer ?? new CliAnswer();
        }

        internal CodingAgentKind Agent { get; private set; }
        internal CliAnswer Answer { get; private set; }
    }

    /// <summary>What a child process left behind.</summary>
    internal sealed class CliProcessResult
    {
        internal int ExitCode;
        internal string StandardOutput = "";
        internal string StandardError = "";
    }

    /// <summary>The seam every test drives: start the process described, feed it the input, and hand back what it
    /// wrote, honouring the token by ending the process and throwing OperationCanceledException.</summary>
    internal delegate Task<CliProcessResult> CliProcessRunner(
        ProcessStartInfo startInfo, string standardInput, CancellationToken cancellationToken);

    /// <summary>The three environment values the locator reads, behind a seam so a test hands it a scratch tree.</summary>
    internal sealed class CliEnvironment
    {
        internal string PathValue;
        internal string AppData;
        internal string UserProfile;

        internal static CliEnvironment Current()
        {
            return new CliEnvironment
            {
                PathValue = Environment.GetEnvironmentVariable("PATH") ?? "",
                AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
        }
    }

    /// <summary>A located CLI: the real binary, and when npm installed it, the folder its package sits in, so an
    /// update's leftover staging folders can be found beside it.</summary>
    internal sealed class CliInstall
    {
        internal CodingAgentKind Agent;
        internal string Executable;
        internal string NpmScopeDirectory;
        internal string NpmPackageRoot;
        internal string[] NpmPackageNames = new string[0];
    }

    /// <summary>
    /// Finds the CLI's REAL binary without a shell. The npm installs put three shims in the global prefix
    /// (%APPDATA%\npm\claude.cmd, claude.ps1 and a sh script; codex the same), and the binary they start is a fixed
    /// path below that prefix: Claude Code's bin\claude.exe, and for Codex the platform package's vendor\...\codex.exe
    /// that its codex.js launcher spawns. Running that binary with ProcessStartInfo.ArgumentList means no cmd.exe
    /// quoting at all, which a persona or a JSON settings argument would otherwise have to survive.
    ///
    /// The order a terminal would use: PATH entry by entry, the CLI's own .exe in a directory first and the shim's
    /// target second, then npm's default prefix (a long-running host keeps the PATH it was started with, so an install
    /// made since is on disk but not on that PATH), then Claude Code's native installer. EVERY candidate, the shims
    /// included, goes through AiExecutablePolicy.ResolveConfigured, AI Brain's trust rules: a drive-qualified local path
    /// with no reparse point on the way, so a relative, UNC, mapped-network or linked location is neither probed nor run.
    /// </summary>
    internal static class CodingAgentLocator
    {
        private static readonly string[] ClaudePackageNames = { "claude-code" };
        private static readonly string[] CodexPackageNames = { "codex", "codex-win32-x64", "codex-win32-arm64" };

        internal static CliInstall Locate(CodingAgentKind agent, CliEnvironment environment)
        {
            if (agent == CodingAgentKind.None || environment == null) return null;
            string exeName = CodingAgents.ExecutableName(agent);
            foreach (string raw in (environment.PathValue ?? "").Split(Path.PathSeparator))
            {
                string directory;
                try { directory = Environment.ExpandEnvironmentVariables((raw ?? "").Trim().Trim('"')); }
                catch { continue; }
                if (directory.Length == 0) continue;
                CliInstall found = InDirectory(agent, directory, exeName);
                if (found != null) return found;
            }
            if (!string.IsNullOrWhiteSpace(environment.AppData))
            {
                CliInstall npm = FromNpmPrefix(agent, Path.Combine(environment.AppData, "npm"));
                if (npm != null) return npm;
            }
            if (agent == CodingAgentKind.Claude && !string.IsNullOrWhiteSpace(environment.UserProfile))
            {
                string native = AiExecutablePolicy.ResolveConfigured(
                    Path.Combine(environment.UserProfile, ".local", "bin", "claude.exe"), "claude.exe");
                if (native != null) return new CliInstall { Agent = agent, Executable = native };
            }
            return null;
        }

        private static CliInstall InDirectory(CodingAgentKind agent, string directory, string exeName)
        {
            string direct;
            try { direct = AiExecutablePolicy.ResolveConfigured(Path.Combine(directory, exeName), exeName); }
            catch { return null; }
            if (direct != null) return new CliInstall { Agent = agent, Executable = direct };
            string stem = Path.GetFileNameWithoutExtension(exeName);
            foreach (string shim in new[] { stem + ".cmd", stem + ".ps1", stem })
            {
                string resolvedShim;
                try { resolvedShim = AiExecutablePolicy.ResolveConfigured(Path.Combine(directory, shim), shim); }
                catch { resolvedShim = null; }
                if (resolvedShim != null) return FromNpmPrefix(agent, directory);
            }
            return null;
        }

        /// <summary>The binary an npm global prefix's shims start, or null when that package is not there.</summary>
        internal static CliInstall FromNpmPrefix(CodingAgentKind agent, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return null;
            try
            {
                if (agent == CodingAgentKind.Claude)
                {
                    string scope = Path.Combine(prefix, "node_modules", "@anthropic-ai");
                    string root = Path.Combine(scope, "claude-code");
                    string exe = AiExecutablePolicy.ResolveConfigured(Path.Combine(root, "bin", "claude.exe"), "claude.exe");
                    if (exe == null) return null;
                    return new CliInstall
                    {
                        Agent = agent, Executable = exe, NpmScopeDirectory = scope, NpmPackageRoot = root,
                        NpmPackageNames = ClaudePackageNames,
                    };
                }
                if (agent == CodingAgentKind.Codex)
                {
                    string scope = Path.Combine(prefix, "node_modules", "@openai");
                    string root = Path.Combine(scope, "codex");
                    foreach (string candidate in CodexCandidates(scope, root))
                    {
                        string exe = AiExecutablePolicy.ResolveConfigured(candidate, "codex.exe");
                        if (exe == null) continue;
                        return new CliInstall
                        {
                            Agent = agent, Executable = exe, NpmScopeDirectory = scope, NpmPackageRoot = root,
                            NpmPackageNames = CodexPackageNames,
                        };
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Where codex.js finds the native binary: the platform package nested under the main one, the same
        /// package hoisted beside it, then the main package's own vendor folder (older packaging). The machine's own
        /// architecture first.</summary>
        private static IEnumerable<string> CodexCandidates(string scope, string root)
        {
            var targets = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("codex-win32-x64", "x86_64-pc-windows-msvc"),
                new KeyValuePair<string, string>("codex-win32-arm64", "aarch64-pc-windows-msvc"),
            };
            if (RuntimeInformation.OSArchitecture == Architecture.Arm64) targets.Reverse();
            foreach (KeyValuePair<string, string> target in targets)
            {
                string tail = Path.Combine("vendor", target.Value, "bin", "codex.exe");
                yield return Path.Combine(root, "node_modules", "@openai", target.Key, tail);
                yield return Path.Combine(scope, target.Key, tail);
                yield return Path.Combine(root, tail);
            }
        }
    }

    /// <summary>
    /// One module's CLI runner: the flags, the private folder, the single-flight gate, the Codex model pick and the
    /// update. Each module owns one instance for its life, so "one call at a time" holds per module (AI Brain's remarks,
    /// its auditions and Validate share the gate; Remembrance's summaries and Validate share theirs).
    /// </summary>
    internal sealed class CodingAgentCli
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // Measured 2026-10-06 (the lane's brief records the rows): each lever cut Claude Code's input from 37,359 tokens
        // per screenshot question to 6,262 with the answer still right, and Codex's from 21,521 to 12,307.
        internal const string ClaudeSettingsJson = "{\"claudeMdExcludes\":[\"**/.claude/CLAUDE.md\",\"**/.claude/rules/**\"]}";

        internal static readonly string[] CodexDisabledFeatures =
        {
            "apps", "plugins", "multi_agent", "skill_search", "tool_suggest", "browser_use", "browser_use_external",
            "computer_use", "image_generation", "goals", "hooks", "in_app_browser", "worktrees", "mentions_v2",
            "shell_tool", "unified_exec",
        };

        internal static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(120);
        internal static readonly TimeSpan ValidateTimeout = TimeSpan.FromSeconds(90);
        internal static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(10);
        // A persona is about two thousand characters; the bound keeps a command line far below Windows' 32k.
        internal const int MaximumSystemPromptCharacters = 8000;
        internal const int MaximumOutputCharacters = 8 * 1024 * 1024;
        internal const int MaximumErrorCharacters = 64 * 1024;
        private const int SaidCharacters = 240;
        private static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan StaleCallFolder = TimeSpan.FromHours(1);
        private static readonly TimeSpan SignInFreshness = TimeSpan.FromSeconds(60);

        private readonly string _scratchRoot;
        private readonly Action<string> _log;

        /// <summary>Starts the child. The real one unless a self-test swapped in a fake (no test ever spawns a CLI).</summary>
        internal CliProcessRunner RunProcess = RunRealProcessAsync;

        /// <summary>Where the locator looks. The real environment unless a self-test points it at a scratch tree.</summary>
        internal Func<CliEnvironment> EnvironmentSource = CliEnvironment.Current;

        /// <summary>How long Update CLI may run before it is stopped; a field so the self-test can watch it fire.</summary>
        internal TimeSpan UpdateBound = UpdateTimeout;

        /// <param name="scratchRoot">This module's own folder for the CLI (under its storage directory): the working
        /// directory every call runs in, the per-call files, and the Codex model pick. Null when the host gave the
        /// module no storage, and then every call answers NoPrivateFolder rather than writing anywhere else.</param>
        /// <param name="log">The module's diagnostic sink. Outcome words, exit codes, durations, token counts and model
        /// ids only: never a prompt, an answer, an account or what the CLI said.</param>
        internal CodingAgentCli(string scratchRoot, Action<string> log)
        {
            _scratchRoot = string.IsNullOrWhiteSpace(scratchRoot) ? null : scratchRoot;
            _log = log;
        }

        internal string ScratchRootForDiagnostics { get { return _scratchRoot; } }

        private void Log(string line)
        {
            Action<string> sink = _log;
            if (sink == null || string.IsNullOrEmpty(line)) return;
            try { sink(line); } catch { }
        }

        internal CliInstall Locate(CodingAgentKind agent)
        {
            CliEnvironment environment;
            try { environment = EnvironmentSource(); } catch { environment = null; }
            return CodingAgentLocator.Locate(agent, environment);
        }

        // ---- the gate ----------------------------------------------------------------------------------------------
        //
        // One model call at a time per module, and an update only while NOTHING of this CLI's is running: the update
        // replaces the binary a running call executes from (and on Windows cannot delete it, hence the staging folder
        // below). A probe (the version, the sign-in status, Codex's catalog) may run beside a model call, never beside an
        // update. A refused call answers Busy at once rather than queueing behind the one in flight.

        private enum CallKind { Model, Probe, Update }

        private readonly object _gate = new object();
        private int _modelCalls;
        private int _probes;
        private bool _updating;

        private bool TryEnter(CallKind kind)
        {
            lock (_gate)
            {
                if (_updating) return false;
                if (kind == CallKind.Model)
                {
                    if (_modelCalls > 0) return false;
                    _modelCalls++;
                    return true;
                }
                if (kind == CallKind.Probe)
                {
                    _probes++;
                    return true;
                }
                if (_modelCalls > 0 || _probes > 0) return false;
                _updating = true;
                return true;
            }
        }

        private void Leave(CallKind kind)
        {
            lock (_gate)
            {
                if (kind == CallKind.Model) _modelCalls = Math.Max(0, _modelCalls - 1);
                else if (kind == CallKind.Probe) _probes = Math.Max(0, _probes - 1);
                else _updating = false;
            }
        }

        /// <summary>True while any call of this runner is running.</summary>
        internal bool IsBusy
        {
            get { lock (_gate) return _modelCalls > 0 || _probes > 0 || _updating; }
        }

        // ---- a call ------------------------------------------------------------------------------------------------

        /// <summary>
        /// One call through the chosen CLI. Never throws: every way it can end is an outcome. The prompt goes on STDIN,
        /// always: a meeting transcript is far larger than the 32k Windows command line, and stdin keeps a prompt out of
        /// every process listing as well. Claude Code takes the image INLINE, in the stream-json user message (one
        /// request, no Read round trip); Codex takes it as `-i` on a private file deleted when the call ends, beside the
        /// instructions file that carries the system prompt.
        /// </summary>
        internal async Task<CliAnswer> AskAsync(CliRequest request, CancellationToken cancellationToken)
        {
            var answer = new CliAnswer();
            if (request == null || request.Agent == CodingAgentKind.None)
            {
                answer.Outcome = CliOutcome.NotInstalled;
                return answer;
            }
            if (!TryEnter(CallKind.Model))
            {
                answer.Outcome = CliOutcome.Busy;
                Log("cli: " + CodingAgents.IdOf(request.Agent) + " " + (request.Purpose ?? "call") + " " + OutcomeWord(answer.Outcome));
                return answer;
            }
            var clock = Stopwatch.StartNew();
            string callDirectory = null;
            try
            {
                CliInstall install = Locate(request.Agent);
                if (install == null)
                {
                    answer.Outcome = CliOutcome.NotInstalled;
                    return answer;
                }
                string working = WorkingDirectory();
                callDirectory = NewCallDirectory();
                if (working == null || callDirectory == null)
                {
                    answer.Outcome = CliOutcome.NoPrivateFolder;
                    return answer;
                }

                string imagePath = null;
                string instructionsPath = null;
                if (request.Agent == CodingAgentKind.Codex)
                {
                    CodexPick pick = await CodexPickAsync(install, cancellationToken).ConfigureAwait(false);
                    answer.Version = pick.Version ?? "";
                    answer.Model = (request.ImagePng != null && request.ImagePng.Length > 0 ? pick.Vision : pick.Text) ?? "";
                    if (request.ImagePng != null && request.ImagePng.Length > 0)
                    {
                        imagePath = Path.Combine(callDirectory, "screen.png");
                        File.WriteAllBytes(imagePath, request.ImagePng);
                    }
                    if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                    {
                        instructionsPath = Path.Combine(callDirectory, "instructions.md");
                        File.WriteAllText(instructionsPath, BoundedSystemPrompt(request.SystemPrompt), Utf8NoBom);
                    }
                }

                ProcessStartInfo startInfo = NewStartInfo(install, working);
                foreach (string argument in BuildArguments(request, answer.Model, imagePath, instructionsPath))
                    startInfo.ArgumentList.Add(argument);
                string input = request.Agent == CodingAgentKind.Claude ? BuildClaudeInput(request) : (request.Prompt ?? "");

                TimeSpan timeout = request.Timeout > TimeSpan.Zero ? request.Timeout : DefaultCallTimeout;
                CliProcessResult result = await RunBoundedAsync(startInfo, input, timeout, cancellationToken, answer)
                    .ConfigureAwait(false);
                if (result == null) return answer;   // TimedOut or Cancelled, set by RunBoundedAsync
                Interpret(request.Agent, result, answer);
                // A model the server refused is not picked again from a stale cache: the next call asks the catalog.
                if (answer.Outcome == CliOutcome.CliTooOld || answer.Outcome == CliOutcome.ModelRefused) ForgetCodexPick();
                return answer;
            }
            catch (OperationCanceledException)
            {
                answer.Outcome = cancellationToken.IsCancellationRequested ? CliOutcome.Cancelled : CliOutcome.TimedOut;
                return answer;
            }
            catch (Exception ex)
            {
                answer.Outcome = CliOutcome.Failed;
                answer.Said = OneLine(ex.Message);
                return answer;
            }
            finally
            {
                clock.Stop();
                answer.ElapsedMilliseconds = clock.ElapsedMilliseconds;
                DeleteQuietly(callDirectory);
                Leave(CallKind.Model);
                Log("cli: " + CodingAgents.IdOf(request.Agent) + " " + (request.Purpose ?? "call") + " " +
                    OutcomeWord(answer.Outcome) + " exit=" + answer.ExitCode.ToString(CultureInfo.InvariantCulture) +
                    " ms=" + answer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    (answer.InputTokens >= 0 ? " inputTokens=" + answer.InputTokens.ToString(CultureInfo.InvariantCulture) : "") +
                    (answer.Model.Length > 0 ? " model=" + answer.Model : ""));
            }
        }

        /// <summary>Validate: one tiny call through the CLI, the same flags and path a real one takes. Its line is kept
        /// for the card's Status row (LastValidation).</summary>
        internal async Task<CliAnswer> ValidateAsync(CodingAgentKind agent, CancellationToken cancellationToken)
        {
            CliAnswer answer = await AskAsync(new CliRequest
            {
                Agent = agent,
                SystemPrompt = "You check that a connection works. Reply with the single word OK and nothing else.",
                Prompt = "Reply with OK.",
                Timeout = ValidateTimeout,
                Purpose = "validate",
            }, cancellationToken).ConfigureAwait(false);
            if (agent != CodingAgentKind.None) RecordValidation(agent, answer);
            return answer;
        }

        /// <summary>The bound and the caller's cancellation over one child, told apart: the caller's token is Cancelled,
        /// the bound firing is TimedOut. Null when either fired; the answer then carries which.</summary>
        private async Task<CliProcessResult> RunBoundedAsync(ProcessStartInfo startInfo, string input, TimeSpan timeout,
            CancellationToken cancellationToken, CliAnswer answer)
        {
            if (timeout.TotalMilliseconds > int.MaxValue) timeout = TimeSpan.FromMilliseconds(int.MaxValue);
            using (var deadline = new CancellationTokenSource(timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
            {
                try
                {
                    return await RunProcess(startInfo, input, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    answer.Outcome = cancellationToken.IsCancellationRequested ? CliOutcome.Cancelled : CliOutcome.TimedOut;
                    return null;
                }
            }
        }

        /// <summary>
        /// The command line, without the prompt: the lean flags the brief measured. Pure, so the self-test pins it.
        /// Claude Code: print mode, stream-json in and out (inline image input needs both, and stream-json output in print
        /// mode needs --verbose), no tools, no session written (AgentFlow watches ~/.claude/projects and would announce the
        /// companion's own call as a waiting session), no MCP servers or skills, the user's CLAUDE.md and rules excluded,
        /// and the short system prompt in place of the coding agent's. No --model: its default model. No --bare: that mode
        /// wants an API key in place of the subscription login.
        /// Codex: exec outside a repository, nothing persisted (--ephemeral, for the same AgentFlow reason, ~/.codex/sessions),
        /// a read-only sandbox, the user's config and rules not loaded, low reasoning, the permission and environment
        /// preambles off, every optional feature off, and the short instructions file in place of the base instructions.
        /// `-i` takes a list, so an option has to follow it, and --json does; the prompt is "-", read from stdin.
        /// </summary>
        internal static List<string> BuildArguments(CliRequest request, string codexModel, string imagePath, string instructionsPath)
        {
            var args = new List<string>();
            if (request == null) return args;
            if (request.Agent == CodingAgentKind.Claude)
            {
                args.Add("-p");
                args.Add("--input-format");
                args.Add("stream-json");
                args.Add("--output-format");
                args.Add("stream-json");
                args.Add("--verbose");
                args.Add("--no-session-persistence");
                args.Add("--tools");
                args.Add("");
                args.Add("--strict-mcp-config");
                args.Add("--disable-slash-commands");
                args.Add("--settings");
                args.Add(ClaudeSettingsJson);
                if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                {
                    args.Add("--system-prompt");
                    args.Add(BoundedSystemPrompt(request.SystemPrompt));
                }
                return args;
            }
            if (request.Agent == CodingAgentKind.Codex)
            {
                args.Add("exec");
                args.Add("--skip-git-repo-check");
                args.Add("--ephemeral");
                args.Add("-s");
                args.Add("read-only");
                args.Add("--ignore-user-config");
                args.Add("--ignore-rules");
                args.Add("-c");
                args.Add("model_reasoning_effort=low");
                args.Add("-c");
                args.Add("include_permissions_instructions=false");
                args.Add("-c");
                args.Add("include_environment_context=false");
                foreach (string feature in CodexDisabledFeatures)
                {
                    args.Add("--disable");
                    args.Add(feature);
                }
                if (!string.IsNullOrEmpty(instructionsPath))
                {
                    // A TOML basic string, so the path is read as a path whatever it contains (an apostrophe in a user
                    // name included); a bare Windows path also worked in the measurement only because it fails to parse
                    // as TOML and falls back to a literal.
                    args.Add("-c");
                    args.Add("model_instructions_file=" + TomlBasicString(instructionsPath));
                }
                if (!string.IsNullOrEmpty(codexModel))
                {
                    args.Add("-m");
                    args.Add(codexModel);
                }
                if (!string.IsNullOrEmpty(imagePath))
                {
                    args.Add("-i");
                    args.Add(imagePath);
                }
                args.Add("--json");
                args.Add("-");
            }
            return args;
        }

        /// <summary>Claude Code's one stream-json user message: the image as a base64 block first, when there is one, then
        /// the prompt. One line, ended, and then stdin closes, which is what ends the session.</summary>
        internal static string BuildClaudeInput(CliRequest request)
        {
            var content = new JsonArray();
            if (request != null && request.ImagePng != null && request.ImagePng.Length > 0)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = Convert.ToBase64String(request.ImagePng),
                    },
                });
            }
            content.Add(new JsonObject { ["type"] = "text", ["text"] = request != null ? (request.Prompt ?? "") : "" });
            var message = new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            };
            return message.ToJsonString() + "\n";
        }

        private static string BoundedSystemPrompt(string value)
        {
            string text = (value ?? "").Replace("\0", "");
            return text.Length > MaximumSystemPromptCharacters
                ? UnicodeTextProgress.TruncateAtCodePointBoundary(text, MaximumSystemPromptCharacters)
                : text;
        }

        internal static string TomlBasicString(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>The child's start info: the binary itself, no shell, no window, all three streams redirected as UTF-8
        /// with no byte-order mark (stdin above all: a BOM in front of the stream-json line is not JSON), in this module's
        /// own working directory, so no project CLAUDE.md, .claude settings or AGENTS.md from wherever the app was
        /// started is picked up, and Claude Code's per-folder record in ~/.claude.json has one entry per module rather
        /// than one per call.</summary>
        private static ProcessStartInfo NewStartInfo(CliInstall install, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = install.Executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
            };
            if (install.Agent == CodingAgentKind.Claude)
            {
                // The measured lever beside claudeMdExcludes: the auto-memory files stay out of the prompt.
                startInfo.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
            }
            else if (install.Agent == CodingAgentKind.Codex && install.NpmPackageRoot != null)
            {
                // What codex.js sets before it spawns this binary, so a directly started codex.exe knows npm manages it
                // (its update and its upgrade hints name npm) exactly as the shim's child would.
                startInfo.Environment.Remove("CODEX_MANAGED_BY_BUN");
                startInfo.Environment.Remove("CODEX_MANAGED_BY_PNPM");
                startInfo.Environment.Remove("CODEX_MANAGED_BY_VITE_PLUS");
                startInfo.Environment["CODEX_MANAGED_BY_NPM"] = "1";
                startInfo.Environment["CODEX_MANAGED_PACKAGE_ROOT"] = install.NpmPackageRoot;
            }
            return startInfo;
        }

        // ---- reading what came back --------------------------------------------------------------------------------

        /// <summary>Fill the answer from the child's output: the text, the token count, and on a failure the class and a
        /// bounded excerpt of what the CLI said.</summary>
        internal static void Interpret(CodingAgentKind agent, CliProcessResult result, CliAnswer answer)
        {
            answer.ExitCode = result.ExitCode;
            string said;
            if (agent == CodingAgentKind.Claude)
            {
                string text, subtype;
                bool isError;
                long tokens;
                bool found = ParseClaudeStream(result.StandardOutput, out text, out isError, out subtype, out tokens);
                answer.InputTokens = tokens;
                if (found && !isError && !string.IsNullOrWhiteSpace(text))
                {
                    answer.Outcome = CliOutcome.Ok;
                    answer.Text = text.Trim();
                    return;
                }
                said = found ? (text ?? "") + " " + (isError ? "" : subtype ?? "") : "";
            }
            else
            {
                string text, errors;
                bool failed;
                long tokens;
                ParseCodexStream(result.StandardOutput, out text, out errors, out failed, out tokens);
                answer.InputTokens = tokens;
                if (!failed && !string.IsNullOrWhiteSpace(text))
                {
                    answer.Outcome = CliOutcome.Ok;
                    answer.Text = text.Trim();
                    return;
                }
                said = errors ?? "";
            }
            string all = (said + " " + (result.StandardError ?? "")).Trim();
            answer.Said = OneLine(all);
            CliOutcome classified = Classify(all);
            answer.Outcome = classified == CliOutcome.Failed && result.ExitCode == 0 && all.Length == 0
                ? CliOutcome.NoAnswer
                : classified;
        }

        /// <summary>
        /// The failure class from what the CLI said, in the order that keeps two messages apart: a NEWER CLI first (Codex's
        /// server answers a model this build is too old for with "... requires a newer version of Codex", which means
        /// Update CLI), then an EXPIRED sign-in before a missing one, because the expired messages also say "sign in
        /// again" (Codex: "your refresh token has expired / was already used / was revoked. Please log out and sign in
        /// again"; Claude Code: "OAuth token has expired", "Your session has expired. Please run /login"), then a missing
        /// sign-in ("Not logged in · Please run /login", "Invalid API key", an answered 401), then a refused model.
        /// </summary>
        internal static CliOutcome Classify(string said)
        {
            string s = (said ?? "").ToLowerInvariant();
            if (s.Length == 0) return CliOutcome.Failed;
            if (Has(s, "requires a newer version") || Has(s, "newer version of codex") || Has(s, "newer version of claude"))
                return CliOutcome.CliTooOld;
            if (Has(s, "refresh token") || Has(s, "could not be refreshed") || Has(s, "sign in again") ||
                Has(s, "sign-in again") || Has(s, "session has expired") || Has(s, "token has expired") ||
                Has(s, "token has been revoked") || Has(s, "was revoked") || Has(s, "authentication required"))
                return CliOutcome.SignInExpired;
            if (Has(s, "not logged in") || Has(s, "not signed in") || Has(s, "please run /login") ||
                Has(s, "invalid api key") || Has(s, "codex login") || Has(s, "please log in") ||
                Has(s, "login required") || Has(s, "unauthorized") || HasNumber(s, "401"))
                return CliOutcome.NotSignedIn;
            if (Has(s, "model") && (Has(s, "not supported") || Has(s, "not available") || Has(s, "does not exist") ||
                Has(s, "not found") || Has(s, "unsupported") || Has(s, "no access") || Has(s, "not allowed") ||
                Has(s, "invalid model")))
                return CliOutcome.ModelRefused;
            return CliOutcome.Failed;
        }

        private static bool Has(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        /// <summary>A number standing alone, so "401" is not found inside "4012" or a request id.</summary>
        private static bool HasNumber(string haystack, string number)
        {
            int from = 0;
            while (true)
            {
                int at = haystack.IndexOf(number, from, StringComparison.Ordinal);
                if (at < 0) return false;
                int end = at + number.Length;
                bool before = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
                bool after = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
                if (before && after) return true;
                from = at + 1;
            }
        }

        /// <summary>Claude Code's stream: the answer and the usage are on the LAST line whose type is "result"; on a
        /// failure that line carries is_error and the error text in "result". The input count is the uncached input plus
        /// both cache counts, which is what the brief's token rows add up.</summary>
        internal static bool ParseClaudeStream(string stdout, out string text, out bool isError, out string subtype, out long inputTokens)
        {
            text = "";
            isError = false;
            subtype = "";
            inputTokens = -1;
            bool found = false;
            foreach (JsonObject line in JsonLines(stdout))
            {
                if (!string.Equals(StringOf(line, "type"), "result", StringComparison.Ordinal)) continue;
                found = true;
                text = StringOf(line, "result");
                subtype = StringOf(line, "subtype");
                isError = BoolOf(line, "is_error");
                JsonObject usage = line["usage"] as JsonObject;
                inputTokens = usage == null
                    ? -1
                    : Math.Max(0, LongOf(usage, "input_tokens")) + Math.Max(0, LongOf(usage, "cache_creation_input_tokens")) +
                      Math.Max(0, LongOf(usage, "cache_read_input_tokens"));
            }
            return found;
        }

        /// <summary>Codex's --json stream: the answer is the last item.completed whose item is an agent_message, the usage
        /// is on turn.completed, and a failure is an "error" event or turn.failed, each carrying a message.</summary>
        internal static void ParseCodexStream(string stdout, out string text, out string errors, out bool failed, out long inputTokens)
        {
            text = "";
            var said = new StringBuilder();
            failed = false;
            inputTokens = -1;
            foreach (JsonObject line in JsonLines(stdout))
            {
                string type = StringOf(line, "type");
                if (type == "item.completed")
                {
                    JsonObject item = line["item"] as JsonObject;
                    if (item != null && StringOf(item, "type") == "agent_message") text = StringOf(item, "text");
                }
                else if (type == "turn.completed")
                {
                    JsonObject usage = line["usage"] as JsonObject;
                    if (usage != null) inputTokens = LongOf(usage, "input_tokens");
                }
                else if (type == "turn.failed")
                {
                    failed = true;
                    JsonObject error = line["error"] as JsonObject;
                    if (error != null) said.Append(StringOf(error, "message")).Append(' ');
                }
                else if (type == "error")
                {
                    said.Append(StringOf(line, "message")).Append(' ');
                }
            }
            errors = said.ToString().Trim();
        }

        private static IEnumerable<JsonObject> JsonLines(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                JsonObject parsed = null;
                try { parsed = JsonNode.Parse(line) as JsonObject; }
                catch { parsed = null; }
                if (parsed != null) yield return parsed;
            }
        }

        private static string StringOf(JsonObject o, string name)
        {
            try
            {
                JsonNode node = o[name];
                JsonValue value = node as JsonValue;
                string s;
                if (value != null && value.TryGetValue(out s)) return s ?? "";
            }
            catch { }
            return "";
        }

        private static bool BoolOf(JsonObject o, string name)
        {
            try
            {
                JsonValue value = o[name] as JsonValue;
                bool b;
                if (value != null && value.TryGetValue(out b)) return b;
            }
            catch { }
            return false;
        }

        private static long LongOf(JsonObject o, string name)
        {
            try
            {
                JsonValue value = o[name] as JsonValue;
                if (value == null) return -1;
                long l;
                if (value.TryGetValue(out l)) return l;
                double d;
                if (value.TryGetValue(out d)) return (long)d;
            }
            catch { }
            return -1;
        }

        /// <summary>A pane-sized, single-line excerpt.</summary>
        internal static string OneLine(string value)
        {
            string one = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            while (one.IndexOf("  ", StringComparison.Ordinal) >= 0) one = one.Replace("  ", " ");
            return one.Length > SaidCharacters
                ? UnicodeTextProgress.TruncateAtCodePointBoundary(one, SaidCharacters) + "…"
                : one;
        }

        internal static string OutcomeWord(CliOutcome outcome)
        {
            switch (outcome)
            {
                case CliOutcome.Ok: return "ok";
                case CliOutcome.NotInstalled: return "not-installed";
                case CliOutcome.NotSignedIn: return "not-signed-in";
                case CliOutcome.SignInExpired: return "sign-in-expired";
                case CliOutcome.CliTooOld: return "cli-too-old";
                case CliOutcome.ModelRefused: return "model-refused";
                case CliOutcome.TimedOut: return "timed-out";
                case CliOutcome.Cancelled: return "cancelled";
                case CliOutcome.Busy: return "busy";
                case CliOutcome.NoAnswer: return "no-answer";
                case CliOutcome.NoPrivateFolder: return "no-private-folder";
                default: return "failed";
            }
        }

        // ---- the private folder ------------------------------------------------------------------------------------

        private string WorkingDirectory()
        {
            if (_scratchRoot == null) return null;
            try
            {
                string work = Path.Combine(_scratchRoot, "work");
                Directory.CreateDirectory(work);
                return work;
            }
            catch { return null; }
        }

        /// <summary>A folder of this call's own for the files Codex reads (the screenshot, the instructions), deleted in
        /// the call's finally. A folder an earlier run could not delete (a process still holding a file, a crash) is
        /// swept here once it is an hour old.</summary>
        private string NewCallDirectory()
        {
            if (_scratchRoot == null) return null;
            try
            {
                string calls = Path.Combine(_scratchRoot, "calls");
                Directory.CreateDirectory(calls);
                foreach (string stale in Directory.GetDirectories(calls))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetCreationTimeUtc(stale) > StaleCallFolder) Directory.Delete(stale, true);
                    }
                    catch { }
                }
                string mine = Path.Combine(calls, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(mine);
                return mine;
            }
            catch { return null; }
        }

        private static void DeleteQuietly(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
        }

        // ---- Codex's model -----------------------------------------------------------------------------------------
        //
        // No model chooser. Claude Code runs on its default. Codex is asked which models THIS installed CLI may use:
        // `codex debug models` renders the catalog its own server hands it (no model call), and the pick is the entry
        // with the LOWEST priority among those whose visibility is "list", image-capable for a vision turn. Never
        // ~/.codex/models_cache.json: every Codex app on the machine rewrites that shared file with ITS version's list,
        // and a newer desktop app's list once offered models the older CLI was refused. The pick is cached per installed
        // CLI, keyed by the binary's path, size and write time (an update changes them) with its version beside it, in
        // memory and in the module's own folder, so the catalog is fetched once per CLI version, not once per remark.

        internal sealed class CodexPick
        {
            internal string Fingerprint = "";
            internal string Version = "";
            internal string Text;
            internal string Vision;
        }

        private readonly object _pickSync = new object();
        private CodexPick _pick;

        /// <summary>How many times this runner asked Codex for its catalog: the self-test's count of cache misses.</summary>
        internal int CatalogFetchesForDiagnostics;

        private string PickCachePath
        {
            get { return _scratchRoot == null ? null : Path.Combine(_scratchRoot, "codex-model-pick.json"); }
        }

        private async Task<CodexPick> CodexPickAsync(CliInstall install, CancellationToken cancellationToken)
        {
            string fingerprint = Fingerprint(install.Executable);
            CodexPick cached;
            lock (_pickSync) cached = _pick;
            if (cached == null) cached = ReadPickCache();
            if (cached != null && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                lock (_pickSync) _pick = cached;
                return cached;
            }

            var pick = new CodexPick { Fingerprint = fingerprint };
            pick.Version = await VersionCoreAsync(install, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref CatalogFetchesForDiagnostics);
            CliProcessResult catalog = await RunToolAsync(install, new[] { "debug", "models" }, CatalogTimeout, cancellationToken)
                .ConfigureAwait(false);
            string json = catalog != null && catalog.ExitCode == 0 ? catalog.StandardOutput : "";
            pick.Text = PickModel(json, false);
            pick.Vision = PickModel(json, true);
            if (pick.Text == null && pick.Vision == null)
            {
                // Nothing usable listed (offline, an unreadable catalog): run on Codex's own default this time, and ask
                // again next time rather than caching an empty pick.
                Log("cli: codex catalog listed no usable model; running on Codex's default");
                return pick;
            }
            lock (_pickSync) _pick = pick;
            WritePickCache(pick);
            Log("cli: codex model pick text=" + (pick.Text ?? "(none)") + " vision=" + (pick.Vision ?? "(none)") +
                " version=" + (pick.Version.Length > 0 ? pick.Version : "(unknown)"));
            return pick;
        }

        internal void ForgetCodexPick()
        {
            lock (_pickSync) _pick = null;
            string path = PickCachePath;
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string Fingerprint(string executable)
        {
            try
            {
                var info = new FileInfo(executable);
                return info.FullName + "|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                       info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch { return executable ?? ""; }
        }

        private CodexPick ReadPickCache()
        {
            string path = PickCachePath;
            try
            {
                if (path == null || !File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
                JsonObject o = JsonNode.Parse(File.ReadAllText(path, Utf8NoBom)) as JsonObject;
                if (o == null) return null;
                var pick = new CodexPick
                {
                    Fingerprint = StringOf(o, "fingerprint"),
                    Version = StringOf(o, "version"),
                    Text = NullIfBlank(StringOf(o, "text")),
                    Vision = NullIfBlank(StringOf(o, "vision")),
                };
                if (!IsUsableSlug(pick.Text)) pick.Text = null;
                if (!IsUsableSlug(pick.Vision)) pick.Vision = null;
                return pick.Text == null && pick.Vision == null ? null : pick;
            }
            catch { return null; }
        }

        private void WritePickCache(CodexPick pick)
        {
            string path = PickCachePath;
            if (path == null) return;
            var o = new JsonObject
            {
                ["fingerprint"] = pick.Fingerprint,
                ["version"] = pick.Version,
                ["text"] = pick.Text ?? "",
                ["vision"] = pick.Vision ?? "",
            };
            AtomicFile.TryWriteAllText(path, o.ToJsonString(), null);
        }

        private static string NullIfBlank(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>The pick from a `codex debug models` catalog ({"models":[{slug, priority, visibility,
        /// input_modalities}, ...]}): the lowest priority among visibility "list", and for an image turn only those whose
        /// input_modalities name "image". A tie keeps the catalog's own order. Null when nothing qualifies.</summary>
        internal static string PickModel(string catalogJson, bool needImage)
        {
            if (string.IsNullOrWhiteSpace(catalogJson)) return null;
            try
            {
                int start = catalogJson.IndexOf('{');
                if (start < 0) return null;
                JsonObject root = JsonNode.Parse(catalogJson.Substring(start),
                    null, new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject;
                JsonArray models = root == null ? null : root["models"] as JsonArray;
                if (models == null) return null;
                string best = null;
                long bestPriority = long.MaxValue;
                foreach (JsonNode node in models)
                {
                    JsonObject model = node as JsonObject;
                    if (model == null) continue;
                    string slug = StringOf(model, "slug").Trim();
                    if (!IsUsableSlug(slug)) continue;
                    if (!string.Equals(StringOf(model, "visibility"), "list", StringComparison.Ordinal)) continue;
                    long priority = LongOf(model, "priority");
                    if (priority < 0) continue;
                    if (needImage && !Lists(model, "input_modalities", "image")) continue;
                    if (priority < bestPriority)
                    {
                        best = slug;
                        bestPriority = priority;
                    }
                }
                return best;
            }
            catch { return null; }
        }

        private static bool Lists(JsonObject o, string arrayName, string wanted)
        {
            JsonArray array = o[arrayName] as JsonArray;
            if (array == null) return false;
            foreach (JsonNode item in array)
            {
                JsonValue value = item as JsonValue;
                string s;
                if (value != null && value.TryGetValue(out s) && string.Equals(s, wanted, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>A slug goes on a command line as `-m slug`, so one that could read as an option, or carries anything
        /// but the characters model ids use, is not taken.</summary>
        private static bool IsUsableSlug(string slug)
        {
            if (string.IsNullOrEmpty(slug) || slug.Length > 128 || slug[0] == '-') return false;
            foreach (char c in slug)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '.' && c != '-' && c != '_' && c != ':' && c != '/') return false;
            return true;
        }

        // ---- the version, the sign-in, the update ------------------------------------------------------------------

        /// <summary>A tool call: the binary with fixed arguments and no prompt, bounded. Null when the bound fired.</summary>
        private async Task<CliProcessResult> RunToolAsync(CliInstall install, string[] arguments, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            string working = WorkingDirectory();
            if (working == null) return null;
            ProcessStartInfo startInfo = NewStartInfo(install, working);
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            var scratch = new CliAnswer();
            return await RunBoundedAsync(startInfo, "", timeout, cancellationToken, scratch).ConfigureAwait(false);
        }

        private async Task<string> VersionCoreAsync(CliInstall install, CancellationToken cancellationToken)
        {
            try
            {
                CliProcessResult result = await RunToolAsync(install, new[] { "--version" }, VersionTimeout, cancellationToken)
                    .ConfigureAwait(false);
                return result == null ? "" : ParseVersion(result.StandardOutput);
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        /// <summary>The first dotted number in `--version`'s output ("2.1.292 (Claude Code)", "codex-cli 0.160.1").</summary>
        internal static string ParseVersion(string output)
        {
            string text = output ?? "";
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i]) || (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '.'))) continue;
                int end = i;
                bool dot = false;
                while (end < text.Length && (char.IsDigit(text[end]) || (text[end] == '.' && end + 1 < text.Length && char.IsDigit(text[end + 1]))))
                {
                    if (text[end] == '.') dot = true;
                    end++;
                }
                if (dot) return text.Substring(i, end - i);
            }
            return "";
        }

        /// <summary>
        /// What the pane's CLI card shows about one CLI: whether it is installed, its version, the model a call runs on
        /// (Claude Code's default, or Codex's pick) and which account it is signed into. Read off the CLI itself, never
        /// off a file another app writes, and never logged: the account is on screen only.
        /// </summary>
        internal sealed class CliDetails
        {
            internal CodingAgentKind Agent;
            internal bool Installed;
            internal string Version = "";
            internal string TextModel;
            internal string VisionModel;
            /// <summary>The "Signed in as" row: an account, a sign-in method, or a ✗/⚠ sentence.</summary>
            internal string SignedIn = "";
            internal DateTime ReadAtUtc;
        }

        /// <summary>
        /// Read the details now. Claude Code: `claude auth status` prints JSON (loggedIn, email, subscriptionType). Codex:
        /// `codex login status` prints the METHOD only ("Logged in using ChatGPT"); no Codex command names the account,
        /// and decoding ~/.codex/auth.json for it was refused: that is Codex's credential store, not a file this module
        /// reads. Codex's model is the pick a call would use (CodexPickAsync), so the card names what will run.
        /// </summary>
        internal async Task<CliDetails> ReadDetailsAsync(CodingAgentKind agent, CancellationToken cancellationToken)
        {
            var details = new CliDetails { Agent = agent, ReadAtUtc = DateTime.UtcNow };
            CliInstall install = Locate(agent);
            if (install == null) return details;
            details.Installed = true;
            if (!TryEnter(CallKind.Probe))
            {
                details.SignedIn = "⚠ " + CodingAgents.ProductName(agent) + " is updating; reopen this pane in a moment.";
                return details;
            }
            try
            {
                details.Version = await VersionCoreAsync(install, cancellationToken).ConfigureAwait(false);
                if (agent == CodingAgentKind.Codex)
                {
                    CodexPick pick = await CodexPickAsync(install, cancellationToken).ConfigureAwait(false);
                    details.TextModel = pick.Text;
                    details.VisionModel = pick.Vision;
                }
                CliProcessResult status = await RunToolAsync(install,
                    agent == CodingAgentKind.Claude ? new[] { "auth", "status" } : new[] { "login", "status" },
                    StatusTimeout, cancellationToken).ConfigureAwait(false);
                details.SignedIn = status == null
                    ? "⚠ " + CodingAgents.ProductName(agent) + " did not say whether it is signed in."
                    : agent == CodingAgentKind.Claude
                        ? DescribeClaudeAuthStatus(status.StandardOutput)
                        : DescribeCodexLoginStatus(status.StandardOutput + "\n" + status.StandardError);
            }
            catch (Exception)
            {
                details.SignedIn = "⚠ " + CodingAgents.ProductName(agent) + " did not say whether it is signed in.";
            }
            finally { Leave(CallKind.Probe); }
            return details;
        }

        internal static string DescribeClaudeAuthStatus(string output)
        {
            JsonObject o = null;
            try
            {
                string text = output ?? "";
                int start = text.IndexOf('{');
                if (start >= 0) o = JsonNode.Parse(text.Substring(start)) as JsonObject;
            }
            catch { o = null; }
            if (o == null) return "⚠ Claude Code did not say whether it is signed in.";
            if (!BoolOf(o, "loggedIn"))
                return "✗ Not signed in. To sign in, " + CodingAgents.SignInHint(CodingAgentKind.Claude) + ".";
            string email = StringOf(o, "email").Trim();
            string plan = StringOf(o, "subscriptionType").Trim();
            string who = email.Length > 0 ? email : "an account Claude Code does not name";
            return who + (plan.Length > 0 ? " (" + plan + ")" : "");
        }

        internal static string DescribeCodexLoginStatus(string output)
        {
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Not logged in", StringComparison.OrdinalIgnoreCase))
                    return "✗ Not signed in. To sign in, " + CodingAgents.SignInHint(CodingAgentKind.Codex) + ".";
                const string prefix = "Logged in using ";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // "Logged in using an API key - sk-...": the masked key after the dash is cut off, never shown.
                    string method = line.Substring(prefix.Length);
                    int dash = method.IndexOf(" - ", StringComparison.Ordinal);
                    if (dash >= 0) method = method.Substring(0, dash);
                    method = method.Trim();
                    if (method.StartsWith("an ", StringComparison.OrdinalIgnoreCase) ||
                        method.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
                        return method + " (Codex does not say which account)";
                    return "a " + method + " account (Codex does not say which)";
                }
            }
            return "⚠ Codex did not say whether it is signed in.";
        }

        // The card's rows are served from a cache and refreshed behind, AI Brain's BeginVramProbe shape: a pane open must
        // never wait on a child process. Single-flight per CLI; a reading older than a minute is refreshed.
        private readonly object _detailsSync = new object();
        private readonly Dictionary<CodingAgentKind, CliDetails> _details = new Dictionary<CodingAgentKind, CliDetails>();
        private int _claudeProbing;
        private int _codexProbing;

        /// <summary>The last details read for this CLI, or null before the first reading; a stale or missing reading
        /// starts a refresh. Never waits.</summary>
        internal CliDetails CachedDetails(CodingAgentKind agent)
        {
            if (agent == CodingAgentKind.None) return null;
            CliDetails cached;
            lock (_detailsSync) _details.TryGetValue(agent, out cached);
            if (cached == null || DateTime.UtcNow - cached.ReadAtUtc > SignInFreshness) BeginDetailsRefresh(agent);
            return cached;
        }

        internal void BeginDetailsRefresh(CodingAgentKind agent)
        {
            if (agent == CodingAgentKind.None) return;
            if (agent == CodingAgentKind.Claude ? Interlocked.CompareExchange(ref _claudeProbing, 1, 0) != 0
                                                : Interlocked.CompareExchange(ref _codexProbing, 1, 0) != 0) return;
            Task.Run(async delegate
            {
                try { await RefreshDetailsAsync(agent, CancellationToken.None).ConfigureAwait(false); }
                catch { }
                finally
                {
                    if (agent == CodingAgentKind.Claude) Interlocked.Exchange(ref _claudeProbing, 0);
                    else Interlocked.Exchange(ref _codexProbing, 0);
                }
            });
        }

        /// <summary>Read now and keep it for the card. Awaited directly by the self-test.</summary>
        internal async Task<CliDetails> RefreshDetailsAsync(CodingAgentKind agent, CancellationToken cancellationToken)
        {
            CliDetails read = await ReadDetailsAsync(agent, cancellationToken).ConfigureAwait(false);
            lock (_detailsSync) _details[agent] = read;
            return read;
        }

        // The card's "Status" row: the last Validate, with when it ran and how long it took. Per CLI, for this session.
        private readonly Dictionary<CodingAgentKind, string> _lastValidation = new Dictionary<CodingAgentKind, string>();

        /// <summary>The last Validate's line for this CLI, or null when none ran this session.</summary>
        internal string LastValidation(CodingAgentKind agent)
        {
            lock (_detailsSync)
            {
                string line;
                return _lastValidation.TryGetValue(agent, out line) ? line : null;
            }
        }

        private void RecordValidation(CodingAgentKind agent, CliAnswer answer)
        {
            string marker = answer.Ok ? "✓" : (answer.Outcome == CliOutcome.Busy || answer.Outcome == CliOutcome.Cancelled ? "⚠" : "✗");
            string line = marker + " " + (answer.Ok ? "answered" : CodingAgentCliText.Brief(answer.Outcome)) + " at " +
                          DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ", in " +
                          (answer.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
            lock (_detailsSync) _lastValidation[agent] = line;
        }

        /// <summary>
        /// Update CLI: the CLI's own update (`claude update`, `codex update`), refused while any call of this runner is in
        /// flight, answering old -> new version. Measured 2026-10-06: npm cannot delete the codex.exe the update itself runs
        /// from, so it leaves its retired copy of the package beside the live one as `.codex-&lt;hash&gt;` (one from July held
        /// 343 MB). Once the update process has EXITED, and only then, the folders matching npm's retirement name exactly
        /// are removed (see IsNpmStagingName); a bound that fires kills the update and removes nothing, because npm may
        /// still be working in that folder.
        /// </summary>
        internal async Task<string> UpdateAsync(CodingAgentKind agent, CancellationToken cancellationToken)
        {
            string product = CodingAgents.ProductName(agent);
            if (!TryEnter(CallKind.Update))
                return "⚠ Not now: a " + product + " call from this module is running. Press Update CLI again when it has finished.";
            try
            {
                CliInstall install = Locate(agent);
                if (install == null)
                    return CodingAgentCliText.Describe(agent, new CliAnswer { Outcome = CliOutcome.NotInstalled }, null);
                string before = await VersionCoreAsync(install, cancellationToken).ConfigureAwait(false);
                var bounded = new CliAnswer();
                string working = WorkingDirectory();
                if (working == null)
                    return CodingAgentCliText.Describe(agent, new CliAnswer { Outcome = CliOutcome.NoPrivateFolder }, null);
                ProcessStartInfo startInfo = NewStartInfo(install, working);
                startInfo.ArgumentList.Add("update");
                TimeSpan bound = UpdateBound > TimeSpan.Zero ? UpdateBound : UpdateTimeout;
                CliProcessResult result = await RunBoundedAsync(startInfo, "", bound, cancellationToken, bounded)
                    .ConfigureAwait(false);
                if (result == null)
                {
                    // Nothing is removed after a stop: the update was killed, and npm may still be inside its folder.
                    Log("cli: " + CodingAgents.IdOf(agent) + " update " + OutcomeWord(bounded.Outcome));
                    return bounded.Outcome == CliOutcome.Cancelled
                        ? "⚠ The update was cancelled."
                        : "✗ " + product + "'s update did not finish within " + FormatDuration(bound) + " and was stopped.";
                }
                StagingCleanup cleaned = install.NpmScopeDirectory != null
                    ? CleanNpmStaging(install.NpmScopeDirectory, install.NpmPackageNames)
                    : new StagingCleanup();
                ForgetCodexPick();
                CliInstall after = Locate(agent) ?? install;
                string now = await VersionCoreAsync(after, cancellationToken).ConfigureAwait(false);
                Log("cli: " + CodingAgents.IdOf(agent) + " update exit=" + result.ExitCode.ToString(CultureInfo.InvariantCulture) +
                    " before=" + (before.Length > 0 ? before : "?") + " after=" + (now.Length > 0 ? now : "?") +
                    " stagingRemoved=" + cleaned.Removed.ToString(CultureInfo.InvariantCulture));
                string tail = cleaned.Removed > 0
                    ? " Removed " + cleaned.Removed.ToString(CultureInfo.InvariantCulture) + " leftover npm staging folder" +
                      (cleaned.Removed == 1 ? "" : "s") + " (" + FormatMegabytes(cleaned.Bytes) + ")."
                    : "";
                if (result.ExitCode != 0 && string.Equals(before, now, StringComparison.Ordinal))
                {
                    string said = OneLine((result.StandardError ?? "") + " " + (result.StandardOutput ?? ""));
                    return "✗ " + product + "'s update failed (exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + ")" +
                           (said.Length > 0 ? ": " + said : ".") + tail;
                }
                if (before.Length > 0 && string.Equals(before, now, StringComparison.Ordinal))
                    return "✓ " + product + " is already up to date (" + now + ")." + tail;
                return "✓ " + product + " updated: " + (before.Length > 0 ? before : "unknown") + " -> " +
                       (now.Length > 0 ? now : "unknown") + "." + tail;
            }
            catch (Exception ex) { return "✗ " + product + "'s update failed: " + OneLine(ex.Message); }
            finally { Leave(CallKind.Update); }
        }

        internal sealed class StagingCleanup
        {
            internal int Removed;
            internal long Bytes;
            internal int Kept;
        }

        /// <summary>
        /// npm's retirement name for a package folder it replaces: "." + the folder name + "-" + eight letters and
        /// digits (@npmcli/arborist retire-path.js: a SHA-1 of the old path in base64 with everything but [A-Za-z0-9]
        /// removed, cut to 8). Exactly that shape and nothing looser: the live package has no leading dot, so it can never
        /// match, and neither can any folder a user made.
        /// </summary>
        internal static bool IsNpmStagingName(string name, IEnumerable<string> packageNames)
        {
            if (string.IsNullOrEmpty(name) || packageNames == null) return false;
            foreach (string package in packageNames)
            {
                if (string.IsNullOrEmpty(package)) continue;
                string prefix = "." + package + "-";
                if (name.Length != prefix.Length + 8 || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                bool hashShaped = true;
                for (int i = prefix.Length; i < name.Length; i++)
                {
                    char c = name[i];
                    if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) hashShaped = false;
                }
                if (hashShaped) return true;
            }
            return false;
        }

        /// <summary>Remove the staging folders beside the live package. Folders only, never a link (a reparse point is
        /// skipped, not followed), each measured first so the pane can say what was freed.</summary>
        internal static StagingCleanup CleanNpmStaging(string scopeDirectory, IEnumerable<string> packageNames)
        {
            var cleanup = new StagingCleanup();
            string[] folders;
            try { folders = Directory.GetDirectories(scopeDirectory); }
            catch { return cleanup; }
            foreach (string folder in folders)
            {
                if (!IsNpmStagingName(Path.GetFileName(folder), packageNames)) continue;
                try
                {
                    var info = new DirectoryInfo(folder);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { cleanup.Kept++; continue; }
                    long bytes = 0;
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        IgnoreInaccessible = true,
                    };
                    foreach (FileInfo file in info.EnumerateFiles("*", options)) bytes += file.Length;
                    info.Delete(true);
                    cleanup.Removed++;
                    cleanup.Bytes += bytes;
                }
                catch { cleanup.Kept++; }
            }
            return cleanup;
        }

        private static string FormatDuration(TimeSpan span)
        {
            return span.TotalMinutes >= 1
                ? ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes"
                : Math.Max(1, (int)Math.Round(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s";
        }

        private static string FormatMegabytes(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString(bytes >= 10L * 1024 * 1024 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB";
        }

        // ---- the real child process ----------------------------------------------------------------------------------

        /// <summary>
        /// Start the CLI, feed stdin, drain both streams ASYNCHRONOUSLY and wait for the exit under the token. The readers
        /// start before the first byte is written, so a child that fills its stdout pipe before it reads its stdin cannot
        /// deadlock the feed; the feed is not awaited ahead of the exit, so a child that never reads its stdin cannot hang
        /// the call past its bound. On cancellation the whole tree is killed. Each stream is kept to a bound and drained
        /// past it, and a grandchild still holding a pipe after the exit is not waited for past DrainBound.
        /// </summary>
        internal static async Task<CliProcessResult> RunRealProcessAsync(
            ProcessStartInfo startInfo, string standardInput, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var process = new Process { StartInfo = startInfo })
            {
                if (!process.Start()) throw new InvalidOperationException("The CLI did not start.");
                var output = new BoundedPump(process.StandardOutput, MaximumOutputCharacters);
                var error = new BoundedPump(process.StandardError, MaximumErrorCharacters);
                Task feed = FeedAsync(process, standardInput);
                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    try { process.WaitForExit(5000); } catch { }
                    throw;
                }
                await Task.WhenAny(Task.WhenAll(output.Completion, error.Completion, feed), Task.Delay(DrainBound))
                    .ConfigureAwait(false);
                return new CliProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = output.Text,
                    StandardError = error.Text,
                };
            }
        }

        private static async Task FeedAsync(Process process, string input)
        {
            try
            {
                StreamWriter writer = process.StandardInput;
                if (!string.IsNullOrEmpty(input))
                {
                    await writer.WriteAsync(input).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch { }   // the child closed its end (it exited or was killed); its exit code says why
            finally
            {
                try { process.StandardInput.Close(); } catch { }
            }
        }

        /// <summary>One redirected stream, read to its end in blocks: kept up to the bound, drained beyond it.</summary>
        private sealed class BoundedPump
        {
            private readonly StringBuilder _text = new StringBuilder();
            private readonly object _sync = new object();
            private readonly int _maximum;
            internal readonly Task Completion;

            internal BoundedPump(StreamReader reader, int maximum)
            {
                _maximum = maximum;
                Completion = PumpAsync(reader);
            }

            internal string Text { get { lock (_sync) return _text.ToString(); } }

            private async Task PumpAsync(StreamReader reader)
            {
                var buffer = new char[8192];
                while (true)
                {
                    int read;
                    try { read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false); }
                    catch { return; }
                    if (read <= 0) return;
                    lock (_sync)
                    {
                        int room = _maximum - _text.Length;
                        if (room > 0) _text.Append(buffer, 0, Math.Min(room, read));
                    }
                }
            }
        }
    }

    /// <summary>The pane's plain words for an outcome, one sentence per class the brief names, with the ✓ ✗ ⚠ markers
    /// both panes already use.</summary>
    internal static class CodingAgentCliText
    {
        /// <summary>A few words for an outcome, for the card's Status row ("✗ not signed in at 14:32:05, in 0.8 s").</summary>
        internal static string Brief(CliOutcome outcome)
        {
            switch (outcome)
            {
                case CliOutcome.Ok: return "answered";
                case CliOutcome.NotInstalled: return "not installed";
                case CliOutcome.NotSignedIn: return "not signed in";
                case CliOutcome.SignInExpired: return "sign-in expired";
                case CliOutcome.CliTooOld: return "too old for its model, press Update CLI";
                case CliOutcome.ModelRefused: return "model refused";
                case CliOutcome.TimedOut: return "no answer in time";
                case CliOutcome.Cancelled: return "cancelled";
                case CliOutcome.Busy: return "busy with another call";
                case CliOutcome.NoAnswer: return "finished without an answer";
                case CliOutcome.NoPrivateFolder: return "no data folder";
                default: return "failed";
            }
        }

        /// <param name="timeout">The bound the call ran under, for the timed-out sentence; null for the default.</param>
        internal static string Describe(CodingAgentKind agent, CliAnswer answer, TimeSpan? timeout)
        {
            string product = CodingAgents.ProductName(agent);
            CliAnswer a = answer ?? new CliAnswer();
            string said = string.IsNullOrEmpty(a.Said) ? "" : ": " + a.Said;
            switch (a.Outcome)
            {
                case CliOutcome.Ok:
                    return "✓ " + product + (a.Version.Length > 0 ? " " + a.Version : "") + " answered in " +
                           (a.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s" +
                           (a.Model.Length > 0 ? " on " + a.Model : "") + ".";
                case CliOutcome.NotInstalled:
                    return agent == CodingAgentKind.Codex
                        ? "✗ Codex is not installed: there is no codex.exe on PATH or in %APPDATA%\\npm (a linked or network folder is not trusted). Install it, then press Validate again."
                        : "✗ Claude Code is not installed: there is no claude.exe on PATH, in %APPDATA%\\npm or in %USERPROFILE%\\.local\\bin (a linked or network folder is not trusted). Install it, then press Validate again.";
                case CliOutcome.NotSignedIn:
                    return "✗ " + product + " is not signed in. To sign in, " + CodingAgents.SignInHint(agent) +
                           ", then press Validate again.";
                case CliOutcome.SignInExpired:
                    return agent == CodingAgentKind.Codex
                        ? "✗ Codex's sign-in has expired: its refresh token has expired, was already used or was revoked. Run codex login in a terminal, then press Validate again."
                        : "✗ Claude Code's sign-in has expired or was revoked. Run claude in a terminal and sign in again with /login, then press Validate again.";
                case CliOutcome.CliTooOld:
                    return "✗ " + product + " refused " + (a.Model.Length > 0 ? "the model " + a.Model : "the model") +
                           " because this " + product + " is too old for it. Press Update CLI, then Validate again.";
                case CliOutcome.ModelRefused:
                    return "✗ " + product + " refused " + (a.Model.Length > 0 ? "the model " + a.Model : "its default model") + said;
                case CliOutcome.TimedOut:
                    TimeSpan bound = timeout ?? CodingAgentCli.DefaultCallTimeout;
                    return "✗ " + product + " did not answer within " +
                           ((int)bound.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s.";
                case CliOutcome.Cancelled:
                    return "⚠ The " + product + " call was cancelled.";
                case CliOutcome.Busy:
                    return "⚠ " + product + " is already answering another request from this module, or updating. Press again when it has finished.";
                case CliOutcome.NoAnswer:
                    return "✗ " + product + " finished without an answer (exit " + a.ExitCode.ToString(CultureInfo.InvariantCulture) + ").";
                case CliOutcome.NoPrivateFolder:
                    return "✗ This module has no data folder of its own, so it cannot run " + product + " (the host gave it no storage).";
                default:
                    return "✗ " + product + " failed (exit " + a.ExitCode.ToString(CultureInfo.InvariantCulture) + ")" +
                           (said.Length > 0 ? said : ".");
            }
        }
    }
}
