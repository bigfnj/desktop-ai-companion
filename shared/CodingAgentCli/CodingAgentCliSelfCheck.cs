using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// The coding-agent CLI runner's own self-check (lane feature/cli-backend), compiled into both modules beside the runner
// and run by both modules' self-tests, so each payload proves the copy it ships. NO CLI IS EVER STARTED: every call goes
// to FakeCliProcess through the runner's process seam, every binary is an empty file in a scratch tree under %TEMP%, and
// nothing reaches a model, a network or a real screen. The real child-process plumbing (RunRealProcessAsync) is the one
// part this cannot reach; its shape is pinned by tests/runtime-hardening-selftest.ps1 and the coordinator's real-app check
// drives it with synthetic input.
namespace DesktopAICompanion.CodingAgent
{
    /// <summary>One start the fake saw, with the per-call files read WHILE the call ran (they are gone after).</summary>
    internal sealed class FakeCliCall
    {
        internal string Executable = "";
        internal List<string> Arguments = new List<string>();
        internal string Input = "";
        internal string WorkingDirectory = "";
        internal Dictionary<string, string> Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal string ImagePath;
        internal byte[] ImageAtCall;
        internal string InstructionsPath;
        internal string InstructionsAtCall;
        internal volatile bool TokenCancelled;
        /// <summary>How the start was described: a shell, a window, the three redirects and their encodings.</summary>
        internal bool UsesShell;
        internal bool NoWindow;
        internal bool AllRedirected;
        internal bool Utf8WithoutBom;
        internal string ArgumentString = "";

        internal bool IsCodex
        {
            get { return string.Equals(Path.GetFileName(Executable), "codex.exe", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>The arguments begin with these.</summary>
        internal bool Is(params string[] leading)
        {
            if (Arguments.Count < leading.Length) return false;
            for (int i = 0; i < leading.Length; i++)
                if (!string.Equals(Arguments[i], leading[i], StringComparison.Ordinal)) return false;
            return true;
        }

        internal bool IsModelCall { get { return Is("-p") || Is("exec"); } }

        /// <summary>The argument after <paramref name="flag"/>, or null.</summary>
        internal string After(string flag)
        {
            int at = Arguments.IndexOf(flag);
            return at >= 0 && at + 1 < Arguments.Count ? Arguments[at + 1] : null;
        }
    }

    /// <summary>The fake CLI behind the runner's process seam: records each start and answers through Respond.</summary>
    internal sealed class FakeCliProcess
    {
        private readonly object _sync = new object();
        private readonly List<FakeCliCall> _calls = new List<FakeCliCall>();

        internal Func<FakeCliCall, CancellationToken, Task<CliProcessResult>> Respond;

        internal List<FakeCliCall> Calls { get { lock (_sync) return new List<FakeCliCall>(_calls); } }

        internal void Clear() { lock (_sync) _calls.Clear(); }

        internal Task<CliProcessResult> RunAsync(ProcessStartInfo startInfo, string input, CancellationToken cancellationToken)
        {
            var call = new FakeCliCall
            {
                Executable = startInfo.FileName ?? "",
                Arguments = new List<string>(startInfo.ArgumentList),
                Input = input ?? "",
                WorkingDirectory = startInfo.WorkingDirectory ?? "",
                UsesShell = startInfo.UseShellExecute,
                NoWindow = startInfo.CreateNoWindow,
                AllRedirected = startInfo.RedirectStandardInput && startInfo.RedirectStandardOutput && startInfo.RedirectStandardError,
                Utf8WithoutBom = IsUtf8WithoutBom(startInfo.StandardInputEncoding) && IsUtf8WithoutBom(startInfo.StandardOutputEncoding) &&
                                 IsUtf8WithoutBom(startInfo.StandardErrorEncoding),
                ArgumentString = startInfo.Arguments ?? "",
            };
            foreach (string key in new[]
            {
                "CLAUDE_CODE_DISABLE_AUTO_MEMORY", "CODEX_MANAGED_BY_NPM", "CODEX_MANAGED_PACKAGE_ROOT", "CLAUDE_CODE_OAUTH_TOKEN",
                "CLAUDE_CODE_DISABLE_ADVISOR_TOOL", "CLAUDE_CODE_DISABLE_TERMINAL_TITLE", "CLAUDE_CODE_EFFORT_LEVEL",
            })
            {
                string value;
                if (startInfo.Environment.TryGetValue(key, out value) && value != null) call.Environment[key] = value;
            }
            call.ImagePath = call.After("-i");
            if (call.ImagePath != null && File.Exists(call.ImagePath)) call.ImageAtCall = File.ReadAllBytes(call.ImagePath);
            foreach (string argument in call.Arguments)
            {
                const string prefix = "model_instructions_file=";
                if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
                call.InstructionsPath = UnTomlBasicString(argument.Substring(prefix.Length));
                if (File.Exists(call.InstructionsPath)) call.InstructionsAtCall = File.ReadAllText(call.InstructionsPath);
            }
            lock (_sync) _calls.Add(call);
            cancellationToken.Register(delegate { call.TokenCancelled = true; });
            Func<FakeCliCall, CancellationToken, Task<CliProcessResult>> respond = Respond;
            return respond != null ? respond(call, cancellationToken) : Task.FromResult(Result(0, "", ""));
        }

        private static bool IsUtf8WithoutBom(Encoding encoding)
        {
            return encoding != null && encoding.CodePage == 65001 && encoding.GetPreamble().Length == 0;
        }

        private static string UnTomlBasicString(string value)
        {
            string v = value ?? "";
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        internal static CliProcessResult Result(int exitCode, string stdout, string stderr)
        {
            return new CliProcessResult { ExitCode = exitCode, StandardOutput = stdout ?? "", StandardError = stderr ?? "" };
        }

        /// <summary>The model the fake Claude Code answers on with no --model. No family's name is in it: what the
        /// user's own setup resolves can be anything.</summary>
        internal const string DefaultAnsweredModel = "claude-selftest-default-1";

        /// <summary>Claude Code's own side request, which its result line's modelUsage names FIRST (measured 2026-10-09).</summary>
        internal const string SideRequestModel = "claude-side-request-selftest-1";

        /// <summary>The model the fake answers an alias on: one of that alias's family, as claude-haiku-5-5 answers haiku.</summary>
        internal static string AnsweredModelFor(string alias)
        {
            return string.IsNullOrEmpty(alias) ? DefaultAnsweredModel : "claude-" + alias + "-selftest-1";
        }

        /// <summary>Claude Code's stream for one turn, answered on the fake's default model (an errored one's assistant
        /// message is "&lt;synthetic&gt;", as Claude Code writes it).</summary>
        internal static string ClaudeStream(string result, bool isError)
        {
            return ClaudeStream(result, isError, isError ? "<synthetic>" : DefaultAnsweredModel, "fake-default");
        }

        /// <summary>Claude Code's stream for one turn in the shape 2.1.293 writes it (measured 2026-10-09): an init line
        /// whose model ECHOES the request, the assistant message carrying the model that answered, and the result line,
        /// whose modelUsage names Claude Code's own side request before the model that answered.</summary>
        internal static string ClaudeStream(string result, bool isError, string answeredModel, string initModel)
        {
            var init = new JsonObject { ["type"] = "system", ["subtype"] = "init", ["model"] = initModel };
            var assistant = new JsonObject
            {
                ["type"] = "assistant",
                ["message"] = new JsonObject
                {
                    ["model"] = answeredModel, ["type"] = "message", ["role"] = "assistant",
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = result } },
                },
            };
            var usageByModel = new JsonObject();
            if (!isError)
            {
                usageByModel[SideRequestModel] = new JsonObject { ["inputTokens"] = 40 };
                usageByModel[answeredModel] = new JsonObject { ["inputTokens"] = 1112 };
            }
            var final = new JsonObject
            {
                ["type"] = "result",
                ["subtype"] = "success",
                ["is_error"] = isError,
                ["result"] = result,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 12, ["cache_creation_input_tokens"] = 100, ["cache_read_input_tokens"] = 1000,
                    ["output_tokens"] = 7,
                },
                ["modelUsage"] = usageByModel,
            };
            return init.ToJsonString() + "\n" + assistant.ToJsonString() + "\n" + final.ToJsonString() + "\n";
        }

        /// <summary>Codex's --json events for one turn that answered.</summary>
        internal static string CodexStream(string text)
        {
            return "{\"type\":\"thread.started\",\"thread_id\":\"t\"}\n" +
                   "{\"type\":\"turn.started\"}\n" +
                   new JsonObject
                   {
                       ["type"] = "item.completed",
                       ["item"] = new JsonObject { ["id"] = "item_0", ["type"] = "reasoning", ["text"] = "thinking" },
                   }.ToJsonString() + "\n" +
                   new JsonObject
                   {
                       ["type"] = "item.completed",
                       ["item"] = new JsonObject { ["id"] = "item_1", ["type"] = "agent_message", ["text"] = text },
                   }.ToJsonString() + "\n" +
                   "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":4321,\"cached_input_tokens\":100,\"output_tokens\":9}}\n";
        }

        /// <summary>Codex's --json events for a turn the server refused.</summary>
        internal static string CodexFailure(string message)
        {
            return "{\"type\":\"thread.started\",\"thread_id\":\"t\"}\n" +
                   new JsonObject { ["type"] = "error", ["message"] = message }.ToJsonString() + "\n" +
                   new JsonObject { ["type"] = "turn.failed", ["error"] = new JsonObject { ["message"] = message } }.ToJsonString() + "\n";
        }

        /// <summary>A synthetic `codex debug models` catalog. Array order is NOT priority order, on purpose; the
        /// lowest-priority entry is hidden, and a listed one would read as an option. The display names and the effort
        /// levels take the shapes the real catalog uses (objects with an "effort"), and one plain string; the last entry
        /// is one the automatic pick could take (IsUsableSlug) and a user could not choose (IsCodexModelName). The second
        /// hidden entry takes no images and lists one effort: a model a user may have chosen while it was listed, which the
        /// checks before a call must still know (review finding F2).</summary>
        internal const string Catalog =
            "{\"models\":[" +
            "{\"slug\":\"vision-later\",\"display_name\":\"Vision Later\",\"priority\":7,\"visibility\":\"list\",\"input_modalities\":[\"text\",\"image\"]," +
            "\"supported_reasoning_levels\":[{\"effort\":\"medium\",\"description\":\"d\"},\"high\"]}," +
            "{\"slug\":\"hidden-first\",\"display_name\":\"Hidden First\",\"priority\":0,\"visibility\":\"hide\",\"input_modalities\":[\"text\",\"image\"]}," +
            "{\"slug\":\"hidden-text-only\",\"display_name\":\"Hidden Text\",\"priority\":3,\"visibility\":\"hide\",\"input_modalities\":[\"text\"]," +
            "\"supported_reasoning_levels\":[\"medium\"]}," +
            "{\"slug\":\"-reads-as-an-option\",\"priority\":0,\"visibility\":\"list\",\"input_modalities\":[\"text\",\"image\"]}," +
            "{\"slug\":\"vision-second\",\"display_name\":\"Vision Second\",\"priority\":2,\"visibility\":\"list\",\"input_modalities\":[\"text\",\"image\"]," +
            "\"supported_reasoning_levels\":[{\"effort\":\"low\",\"description\":\"d\"},{\"effort\":\"high\",\"description\":\"d\"},{\"effort\":\"Not A Word\"}]}," +
            "{\"slug\":\"text-only-low\",\"display_name\":\"Text Only\",\"priority\":1,\"visibility\":\"list\",\"input_modalities\":[\"text\"]," +
            "\"supported_reasoning_levels\":[{\"effort\":\"low\",\"description\":\"d\"},{\"effort\":\"medium\",\"description\":\"d\"}]}," +
            "{\"slug\":\"Org/Upper_Case\",\"display_name\":\"Upper\",\"priority\":9,\"visibility\":\"list\",\"input_modalities\":[\"text\"]}" +
            "]}";

        /// <summary>The ordinary fake: versions, a catalog, signed in, and every model call answered with
        /// <paramref name="answer"/>.</summary>
        internal static Func<FakeCliCall, CancellationToken, Task<CliProcessResult>> Answering(string answer)
        {
            return delegate(FakeCliCall call, CancellationToken token)
            {
                if (call.Is("--version"))
                    return Task.FromResult(Result(0, call.IsCodex ? "codex-cli 0.160.1\n" : "2.1.292 (Claude Code)\n", ""));
                if (call.Is("debug", "models")) return Task.FromResult(Result(0, Catalog, ""));
                if (call.Is("auth", "status"))
                    return Task.FromResult(Result(0,
                        "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"email\":\"someone@example.invalid\",\"subscriptionType\":\"max\"}", ""));
                if (call.Is("login", "status")) return Task.FromResult(Result(0, "", "Logged in using ChatGPT\n"));
                if (call.Is("update")) return Task.FromResult(Result(0, "updated\n", ""));
                if (call.IsCodex) return Task.FromResult(Result(0, CodexStream(answer), ""));
                // Answered on the family of the alias asked for, with the init line echoing the request, as Claude Code does.
                string alias = call.After("--model");
                return Task.FromResult(Result(0, ClaudeStream(answer, false, AnsweredModelFor(alias), alias ?? "fake-default"), ""));
            };
        }
    }

    /// <summary>A scratch tree for one check: an npm prefix holding both CLIs the way npm lays them out (empty files),
    /// and a module data folder for the runner.</summary>
    internal sealed class FakeCliScratch : IDisposable
    {
        internal readonly string Root;
        internal readonly string Npm;
        internal readonly string Data;
        internal readonly string ClaudeScope;
        internal readonly string ClaudeExe;
        internal readonly string CodexScope;
        internal readonly string CodexExe;

        internal FakeCliScratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "dp-cli-selfcheck-" + Guid.NewGuid().ToString("N"));
            Npm = Path.Combine(Root, "npm");
            Data = Path.Combine(Root, "data");
            Directory.CreateDirectory(Data);
            ClaudeScope = Path.Combine(Npm, "node_modules", "@anthropic-ai");
            ClaudeExe = Path.Combine(ClaudeScope, "claude-code", "bin", "claude.exe");
            CodexScope = Path.Combine(Npm, "node_modules", "@openai");
            CodexExe = Path.Combine(CodexScope, "codex", "node_modules", "@openai",
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                    ? "codex-win32-arm64" : "codex-win32-x64",
                "vendor",
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                    ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc",
                "bin", "codex.exe");
            Touch(Path.Combine(Npm, "claude.cmd"), "@ECHO off\n");
            Touch(Path.Combine(Npm, "claude.ps1"), "");
            Touch(Path.Combine(Npm, "codex.cmd"), "@ECHO off\n");
            Touch(ClaudeExe, "");
            Touch(CodexExe, "");
        }

        internal static void Touch(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content ?? "");
        }

        internal CliEnvironment PathOnly
        {
            get { return new CliEnvironment { PathValue = Npm, AppData = "", UserProfile = "" }; }
        }

        internal CodingAgentCli NewRunner(FakeCliProcess fake, List<string> log)
        {
            var runner = new CodingAgentCli(Path.Combine(Data, "cli"), delegate(string line) { lock (log) log.Add(line); });
            runner.RunProcess = fake.RunAsync;
            CliEnvironment environment = PathOnly;
            runner.EnvironmentSource = delegate { return environment; };
            return runner;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }
    }

    internal static class CodingAgentCliSelfCheck
    {
        /// <summary>A tiny real PNG (one pixel), so nothing that resembles a screen is ever handed to the runner.</summary>
        internal static readonly byte[] SyntheticPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        internal const string Persona = "You are Probe, a tiny companion. Disposition: dry. Your human is called Tester.";

        /// <summary>Run every group, each under its own catch, so one that throws is a FAIL naming it and the rest
        /// still run.</summary>
        internal static void Run(Action<string, bool> check)
        {
            Guard(check, "locate", CheckLocate);
            Guard(check, "flags", CheckFlags);
            Guard(check, "input", CheckInput);
            Guard(check, "model pick", CheckModelPick);
            Guard(check, "classes", CheckClasses);
            Guard(check, "single flight and update", CheckSingleFlightAndUpdate);
            Guard(check, "details", CheckDetails);
            Guard(check, "installs found elsewhere", CheckLocateElsewhere);
            Guard(check, "updated elsewhere", CheckUpdatedElsewhere);
            Guard(check, "saved sign-in token", CheckToken);
            Guard(check, "model and effort", CheckModelAndEffort);
            Guard(check, "the model that answered", CheckAnsweredModel);
            Guard(check, "codex chosen model", CheckCodexChoice);
            Guard(check, "text shown from outside", CheckDisplayable);
            Guard(check, "a sign-in change during a call", CheckSignInChange);
        }

        private static void Guard(Action<string, bool> check, string group, Action<Action<string, bool>> run)
        {
            try { run(check); }
            catch (Exception ex) { check("cli runner: the " + group + " group threw " + ex.GetType().Name + ": " + ex.Message, false); }
        }

        private static T Wait<T>(Task<T> task)
        {
            if (!task.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("a fake CLI call did not end");
            return task.Result;
        }

        private static CliRequest Remark(CodingAgentKind agent, string prompt, byte[] image)
        {
            return new CliRequest
            {
                Agent = agent, SystemPrompt = Persona, Prompt = prompt, ImagePng = image,
                Timeout = TimeSpan.FromSeconds(20), Purpose = "remark",
            };
        }

        /// <summary>A remark with a model and an effort chosen, as a module's settings would name them.</summary>
        private static CliRequest Chosen(CodingAgentKind agent, string model, string effort, byte[] image)
        {
            CliRequest request = Remark(agent, "p", image);
            request.Model = model;
            request.Effort = effort;
            return request;
        }

        /// <summary>A Claude Code model call's --settings at <paramref name="effort"/>, written out by hand rather than
        /// built the way the runner builds it, so the exact-argument pins compare against text and not against the code
        /// under test.</summary>
        private static string ExpectedClaudeSettings(string effort)
        {
            return "{\"claudeMdExcludes\":[\"**/.claude/CLAUDE.md\",\"**/.claude/rules/**\"],\"env\":{\"CLAUDE_CODE_EFFORT_LEVEL\":\"" +
                   effort + "\",\"CLAUDE_CODE_DISABLE_ADVISOR_TOOL\":\"1\",\"CLAUDE_CODE_DISABLE_TERMINAL_TITLE\":\"1\"}}";
        }

        /// <summary>The --settings an argument list carries, parsed, or null without one.</summary>
        private static JsonObject ParsedSettings(List<string> arguments)
        {
            int at = arguments == null ? -1 : arguments.IndexOf("--settings");
            if (at < 0 || at + 1 >= arguments.Count) return null;
            try { return JsonNode.Parse(arguments[at + 1]) as JsonObject; }
            catch { return null; }
        }

        // ---- locate ----

        private static void CheckLocate(Action<string, bool> check)
        {
            using (var scratch = new FakeCliScratch())
            {
                CliInstall claude = CodingAgentLocator.Locate(CodingAgentKind.Claude, scratch.PathOnly);
                check("cli runner: the npm shim's Claude Code binary is found and run, never the .cmd shim",
                    claude != null && string.Equals(claude.Executable, scratch.ClaudeExe, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(claude.NpmScopeDirectory, scratch.ClaudeScope, StringComparison.OrdinalIgnoreCase));
                CliInstall codex = CodingAgentLocator.Locate(CodingAgentKind.Codex, scratch.PathOnly);
                check("cli runner: Codex resolves to the platform package's codex.exe that codex.js starts",
                    codex != null && string.Equals(codex.Executable, scratch.CodexExe, StringComparison.OrdinalIgnoreCase) &&
                    codex.NpmPackageRoot != null && codex.NpmPackageRoot.EndsWith(Path.Combine("@openai", "codex"), StringComparison.OrdinalIgnoreCase));

                // Through APPDATA with nothing on PATH: a host started before the install still finds it.
                var appDataOnly = new CliEnvironment { PathValue = "", AppData = scratch.Root, UserProfile = "" };
                check("cli runner: with nothing on PATH, npm's default prefix under %APPDATA% is still found",
                    CodingAgentLocator.Locate(CodingAgentKind.Claude, appDataOnly) != null);

                // The trust rules: the same real install named by a drive-RELATIVE path is not probed or run.
                string rootRelative = scratch.Npm.Length > 2 && scratch.Npm[1] == ':' ? scratch.Npm.Substring(2) : scratch.Npm;
                var untrusted = new CliEnvironment { PathValue = rootRelative, AppData = "", UserProfile = "" };
                check("cli runner: a PATH entry that is not a drive-qualified local path is never trusted, even naming a real install",
                    CodingAgentLocator.Locate(CodingAgentKind.Claude, untrusted) == null);

                File.Delete(scratch.ClaudeExe);
                check("cli runner: a shim whose binary is missing is not an install",
                    CodingAgentLocator.Locate(CodingAgentKind.Claude, scratch.PathOnly) == null);

                string native = Path.Combine(scratch.Root, "native");
                FakeCliScratch.Touch(Path.Combine(native, "claude.exe"), "");
                CliInstall direct = CodingAgentLocator.Locate(CodingAgentKind.Claude,
                    new CliEnvironment { PathValue = native, AppData = "", UserProfile = "" });
                check("WITNESS cli runner: a claude.exe on PATH itself is run directly, with no npm folder to clean",
                    direct != null && direct.NpmScopeDirectory == null);
                check("cli runner: no CLI is found where nothing is installed",
                    CodingAgentLocator.Locate(CodingAgentKind.Codex,
                        new CliEnvironment { PathValue = native, AppData = "", UserProfile = "" }) == null);
            }
        }

        // ---- flags ----

        private static void CheckFlags(Action<string, bool> check)
        {
            List<string> claude = CodingAgentCli.BuildArguments(Remark(CodingAgentKind.Claude, "x", null), "", null, null);
            // No model chosen: no --model, and the runner's default effort (lane feature/cli-model-effort re-pointed this
            // pin from 1.3.0's list, which carried neither; the model and effort checks are in CheckModelAndEffort).
            var expectedClaude = new List<string>
            {
                "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--no-session-persistence", "--tools", "", "--strict-mcp-config", "--disable-slash-commands",
                "--settings", ExpectedClaudeSettings(CodingAgentCli.DefaultEffort), "--effort", CodingAgentCli.DefaultEffort,
                "--system-prompt", Persona,
            };
            check("cli runner: Claude Code's command line is exactly the lean flags the brief measured, persona last",
                string.Join("\u0001", claude) == string.Join("\u0001", expectedClaude));
            check("cli runner: Claude Code never runs with --bare, a model chosen or not",
                !claude.Contains("--bare") &&
                !CodingAgentCli.BuildArguments(Chosen(CodingAgentKind.Claude, "opus", "high", null), "", null, null).Contains("--bare"));
            check("WITNESS cli runner: with no model chosen Claude Code gets no --model, so it runs on its own default",
                !claude.Contains("--model"));
            JsonObject settings = ParsedSettings(claude);
            JsonArray excluded = settings == null ? null : settings["claudeMdExcludes"] as JsonArray;
            check("cli runner: Claude Code's settings exclude the user's CLAUDE.md and rules",
                excluded != null && excluded.Count == 2 && (string)excluded[0] == "**/.claude/CLAUDE.md" &&
                (string)excluded[1] == "**/.claude/rules/**");

            bool claudeEveryShape = true, codexEveryShape = true;
            foreach (CliRequest shape in new[]
            {
                Remark(CodingAgentKind.Claude, "text", null),
                Remark(CodingAgentKind.Claude, "image", SyntheticPng),
                new CliRequest { Agent = CodingAgentKind.Claude, Prompt = "Reply with OK.", Purpose = "validate" },
            })
                claudeEveryShape &= CodingAgentCli.BuildArguments(shape, "", null, null).Contains("--no-session-persistence");
            foreach (CliRequest shape in new[]
            {
                Remark(CodingAgentKind.Codex, "text", null),
                Remark(CodingAgentKind.Codex, "image", SyntheticPng),
                new CliRequest { Agent = CodingAgentKind.Codex, Prompt = "Reply with OK.", Purpose = "validate" },
            })
                codexEveryShape &= CodingAgentCli.BuildArguments(shape, "m", "C:\\x\\screen.png", "C:\\x\\i.md").Contains("--ephemeral");
            check("cli runner: every Claude Code call shape leaves no session behind (--no-session-persistence)", claudeEveryShape);
            check("cli runner: every Codex call shape leaves no session behind (--ephemeral)", codexEveryShape);

            // An effort chosen and no model: the effort replaces 1.3.0's fixed low, and -m carries the automatic pick.
            List<string> codex = CodingAgentCli.BuildArguments(Chosen(CodingAgentKind.Codex, "", "medium", SyntheticPng),
                "vision-second", "C:\\work\\screen.png", "C:\\work\\instructions.md");
            var expectedCodex = new List<string>
            {
                "exec", "--skip-git-repo-check", "--ephemeral", "-s", "read-only", "--ignore-user-config", "--ignore-rules",
                "-c", "model_reasoning_effort=medium", "-c", "include_permissions_instructions=false",
                "-c", "include_environment_context=false",
            };
            foreach (string feature in CodingAgentCli.CodexDisabledFeatures) { expectedCodex.Add("--disable"); expectedCodex.Add(feature); }
            expectedCodex.AddRange(new[]
            {
                "-c", "model_instructions_file=\"C:\\\\work\\\\instructions.md\"", "-m", "vision-second",
                "-i", "C:\\work\\screen.png", "--json", "-",
            });
            check("cli runner: Codex's command line is exactly the lean flags, the instructions file, the pick and '-' for stdin",
                string.Join("\u0001", codex) == string.Join("\u0001", expectedCodex));
            check("cli runner: Codex disables the sixteen features the brief measured",
                CodingAgentCli.CodexDisabledFeatures.Length == 16);
            int image = codex.IndexOf("-i");
            check("cli runner: Codex's greedy -i list is ended by an option, so it cannot swallow the prompt argument",
                image >= 0 && image + 2 < codex.Count && codex[image + 2].StartsWith("--", StringComparison.Ordinal));
            check("cli runner: the instructions path is a TOML basic string, so a backslash or an apostrophe survives",
                CodingAgentCli.TomlBasicString("C:\\Users\\O'Brien\\i.md") == "\"C:\\\\Users\\\\O'Brien\\\\i.md\"");
        }

        // ---- input, files, environment, answers ----

        private static void CheckInput(Action<string, bool> check)
        {
            using (var scratch = new FakeCliScratch())
            {
                var log = new List<string>();
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("hello there") };
                CodingAgentCli runner = scratch.NewRunner(fake, log);

                const string prompt = "SCREEN-PROMPT-7731 what do you see";
                CliAnswer claude = Wait(runner.AskAsync(Remark(CodingAgentKind.Claude, prompt, SyntheticPng), CancellationToken.None));
                FakeCliCall c = fake.Calls.Find(delegate(FakeCliCall x) { return x.IsModelCall; });
                JsonObject message = null;
                try { message = JsonNode.Parse(c.Input.Trim()) as JsonObject; } catch { message = null; }
                JsonArray content = message == null ? null : message["message"]?["content"] as JsonArray;
                check("cli runner: Claude Code's prompt goes on stdin as ONE stream-json user message, never on the command line",
                    c != null && c.Input.EndsWith("\n", StringComparison.Ordinal) && c.Input.Trim().IndexOf('\n') < 0 &&
                    message != null && (string)message["type"] == "user" && content != null && content.Count == 2 &&
                    (string)content[1]["text"] == prompt && !c.Arguments.Exists(delegate(string a) { return a.Contains("SCREEN-PROMPT-7731"); }));
                check("cli runner: Claude Code's image goes inline as a base64 PNG block ahead of the text",
                    content != null && content.Count == 2 && (string)content[0]["type"] == "image" &&
                    (string)content[0]["source"]["media_type"] == "image/png" &&
                    (string)content[0]["source"]["data"] == Convert.ToBase64String(SyntheticPng));
                check("cli runner: Claude Code's persona is its --system-prompt", c != null && c.After("--system-prompt") == Persona);
                check("cli runner: Claude Code runs with its auto-memory off",
                    c != null && c.Environment.ContainsKey("CLAUDE_CODE_DISABLE_AUTO_MEMORY") && c.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] == "1");
                check("cli runner: every call runs in the module's own working folder",
                    c != null && string.Equals(c.WorkingDirectory, Path.Combine(scratch.Data, "cli", "work"), StringComparison.OrdinalIgnoreCase));
                check("cli runner: a call starts the CLI's own binary with no shell and no window, every argument in ArgumentList",
                    c != null && string.Equals(c.Executable, scratch.ClaudeExe, StringComparison.OrdinalIgnoreCase) &&
                    !c.UsesShell && c.NoWindow && c.ArgumentString.Length == 0);
                check("cli runner: all three streams are redirected as UTF-8 with no byte-order mark",
                    c != null && c.AllRedirected && c.Utf8WithoutBom);
                check("cli runner: Claude Code's answer and token count are read from the stream's result line",
                    claude.Ok && claude.Text == "hello there" && claude.InputTokens == 1112);

                fake.Clear();
                CliAnswer codex = Wait(runner.AskAsync(Remark(CodingAgentKind.Codex, prompt, SyntheticPng), CancellationToken.None));
                FakeCliCall x2 = fake.Calls.Find(delegate(FakeCliCall x) { return x.IsModelCall; });
                check("cli runner: Codex's prompt is exactly its stdin, read through the '-' argument, never on the command line",
                    x2 != null && x2.Input == prompt && x2.Arguments[x2.Arguments.Count - 1] == "-" &&
                    !x2.Arguments.Exists(delegate(string a) { return a.Contains("SCREEN-PROMPT-7731"); }));
                check("cli runner: Codex reads the image from a private file that holds the PNG's bytes while it runs",
                    x2 != null && x2.ImageAtCall != null && Convert.ToBase64String(x2.ImageAtCall) == Convert.ToBase64String(SyntheticPng) &&
                    x2.ImagePath.StartsWith(Path.Combine(scratch.Data, "cli", "calls"), StringComparison.OrdinalIgnoreCase));
                check("cli runner: Codex's persona is its instructions file", x2 != null && x2.InstructionsAtCall == Persona);
                check("cli runner: the per-call files and their folder are deleted when the call ends",
                    x2 != null && x2.ImagePath != null && !File.Exists(x2.ImagePath) && !File.Exists(x2.InstructionsPath) &&
                    !Directory.Exists(Path.GetDirectoryName(x2.ImagePath)));
                check("cli runner: a directly started codex.exe is told npm manages it, as codex.js tells it",
                    x2 != null && x2.Environment.ContainsKey("CODEX_MANAGED_BY_NPM") && x2.Environment["CODEX_MANAGED_BY_NPM"] == "1" &&
                    x2.Environment.ContainsKey("CODEX_MANAGED_PACKAGE_ROOT") &&
                    x2.Environment["CODEX_MANAGED_PACKAGE_ROOT"].EndsWith(Path.Combine("@openai", "codex"), StringComparison.OrdinalIgnoreCase));
                check("cli runner: Codex's answer is the last agent_message and its token count the turn's",
                    codex.Ok && codex.Text == "hello there" && codex.InputTokens == 4321);
                check("cli runner: the log names the outcome and never the prompt or the answer",
                    log.Exists(delegate(string l) { return l.StartsWith("cli: codex remark ok", StringComparison.Ordinal); }) &&
                    !log.Exists(delegate(string l) { return l.Contains("SCREEN-PROMPT-7731") || l.Contains("hello there"); }));
            }
        }

        // ---- the Codex model pick ----

        private static void CheckModelPick(Action<string, bool> check)
        {
            check("cli runner: Codex's model for a text turn is the lowest-priority LISTED entry",
                CodingAgentCli.PickModel(FakeCliProcess.Catalog, false) == "text-only-low");
            check("cli runner: Codex's model for a screenshot turn is the lowest-priority listed entry that takes images",
                CodingAgentCli.PickModel(FakeCliProcess.Catalog, true) == "vision-second");
            check("cli runner: a hidden model is never picked, however low its priority",
                CodingAgentCli.PickModel("{\"models\":[{\"slug\":\"h\",\"priority\":0,\"visibility\":\"hide\",\"input_modalities\":[\"text\"]}]}", false) == null);
            check("cli runner: a slug that would read as an option is never picked",
                CodingAgentCli.PickModel("{\"models\":[{\"slug\":\"-m\",\"priority\":0,\"visibility\":\"list\",\"input_modalities\":[\"text\"]}]}", false) == null);

            using (var scratch = new FakeCliScratch())
            {
                var log = new List<string>();
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("ok") };
                CodingAgentCli runner = scratch.NewRunner(fake, log);
                CliAnswer text = Wait(runner.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                CliAnswer vision = Wait(runner.AskAsync(Remark(CodingAgentKind.Codex, "v", SyntheticPng), CancellationToken.None));
                List<FakeCliCall> execs = fake.Calls.FindAll(delegate(FakeCliCall x) { return x.Is("exec"); });
                check("cli runner: each Codex call sends -m with the pick for its kind of turn",
                    execs.Count == 2 && execs[0].After("-m") == "text-only-low" && execs[1].After("-m") == "vision-second" &&
                    text.Model == "text-only-low" && vision.Model == "vision-second");
                check("cli runner: Codex's catalog is fetched once per CLI version, not once per call",
                    runner.CatalogFetchesForDiagnostics == 1 &&
                    fake.Calls.FindAll(delegate(FakeCliCall x) { return x.Is("debug", "models"); }).Count == 1);

                var fresh = new FakeCliProcess { Respond = FakeCliProcess.Answering("ok") };
                CodingAgentCli second = scratch.NewRunner(fresh, log);
                Wait(second.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                check("cli runner: a new runner reads the saved pick and asks Codex for nothing but the call",
                    second.CatalogFetchesForDiagnostics == 0 &&
                    fresh.Calls.FindAll(delegate(FakeCliCall x) { return !x.Is("exec"); }).Count == 0);

                File.WriteAllText(scratch.CodexExe, "an updated binary");
                File.SetLastWriteTimeUtc(scratch.CodexExe, DateTime.UtcNow.AddMinutes(1));
                Wait(second.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                check("cli runner: an updated Codex (a different binary) is asked for its catalog again",
                    second.CatalogFetchesForDiagnostics == 1);

                fresh.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    if (call.Is("exec"))
                        return Task.FromResult(FakeCliProcess.Result(1,
                            FakeCliProcess.CodexFailure("The 'text-only-low' model requires a newer version of Codex. Please upgrade to the latest app or CLI and try again."), ""));
                    return FakeCliProcess.Answering("ok")(call, token);
                };
                CliAnswer refused = Wait(second.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                fresh.Respond = FakeCliProcess.Answering("ok");
                Wait(second.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                check("cli runner: a model the server refused is not picked again from the cache",
                    refused.Outcome == CliOutcome.CliTooOld && second.CatalogFetchesForDiagnostics == 2);
            }
        }

        // ---- the outcome classes ----

        private static CliAnswer AnswerTo(CodingAgentKind agent, CliProcessResult result)
        {
            return AnswerTo(agent, result, new List<string>());
        }

        private static CliAnswer AnswerTo(CodingAgentKind agent, CliProcessResult result, List<string> log)
        {
            return AnswerTo(Remark(agent, "p", null), result, log);
        }

        private static CliAnswer AnswerTo(CliRequest request, CliProcessResult result, List<string> log)
        {
            using (var scratch = new FakeCliScratch())
            {
                var fake = new FakeCliProcess();
                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    return call.IsModelCall ? Task.FromResult(result) : FakeCliProcess.Answering("ok")(call, token);
                };
                return Wait(scratch.NewRunner(fake, log).AskAsync(request, CancellationToken.None));
            }
        }

        private static void CheckClasses(Action<string, bool> check)
        {
            using (var scratch = new FakeCliScratch())
            {
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("ok") };
                CodingAgentCli runner = scratch.NewRunner(fake, new List<string>());
                runner.EnvironmentSource = delegate { return new CliEnvironment { PathValue = "", AppData = "", UserProfile = "" }; };
                CliAnswer missing = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("cli runner: Validate with no CLI installed says not installed and starts nothing",
                    missing.Outcome == CliOutcome.NotInstalled && fake.Calls.Count == 0 &&
                    CodingAgentCliText.Describe(CodingAgentKind.Claude, missing, null).StartsWith("✗ Claude Code is not installed", StringComparison.Ordinal));
            }

            CliAnswer claudeOut = AnswerTo(CodingAgentKind.Claude,
                FakeCliProcess.Result(1, FakeCliProcess.ClaudeStream("Not logged in · Please run /login", true), ""));
            check("cli runner: Claude Code's 'Not logged in' is not signed in",
                claudeOut.Outcome == CliOutcome.NotSignedIn &&
                CodingAgentCliText.Describe(CodingAgentKind.Claude, claudeOut, null).Contains("sign in with /login"));
            CliAnswer claudeExpired = AnswerTo(CodingAgentKind.Claude,
                FakeCliProcess.Result(1, FakeCliProcess.ClaudeStream("OAuth token has expired. Please obtain a new token or refresh your existing token.", true), ""));
            check("cli runner: Claude Code's expired OAuth token is a sign-in that expired", claudeExpired.Outcome == CliOutcome.SignInExpired);

            foreach (string phrase in new[]
            {
                "Your access token could not be refreshed because your refresh token has expired. Please log out and sign in again.",
                "Your access token could not be refreshed because your refresh token was already used. Please log out and sign in again.",
                "Your access token could not be refreshed because your refresh token was revoked. Please log out and sign in again.",
            })
            {
                CliAnswer expired = AnswerTo(CodingAgentKind.Codex, FakeCliProcess.Result(1, FakeCliProcess.CodexFailure(phrase), ""));
                check("cli runner: Codex says its sign-in expired when " + phrase.Substring(phrase.IndexOf("refresh token", StringComparison.Ordinal), 30),
                    expired.Outcome == CliOutcome.SignInExpired &&
                    CodingAgentCliText.Describe(CodingAgentKind.Codex, expired, null).Contains("expired, was already used or was revoked"));
            }
            CliAnswer codexOut = AnswerTo(CodingAgentKind.Codex,
                FakeCliProcess.Result(1, FakeCliProcess.CodexFailure("unexpected status 401 Unauthorized: Missing bearer or basic authentication in header"), ""));
            check("cli runner: Codex answered 401 is not signed in", codexOut.Outcome == CliOutcome.NotSignedIn &&
                CodingAgentCliText.Describe(CodingAgentKind.Codex, codexOut, null).Contains("codex login"));
            CliAnswer tooOld = AnswerTo(CodingAgentKind.Codex, FakeCliProcess.Result(1,
                FakeCliProcess.CodexFailure("The 'gpt-x' model requires a newer version of Codex. Please upgrade to the latest app or CLI and try again."), ""));
            check("cli runner: 'requires a newer version of Codex' tells the user to press Update CLI",
                tooOld.Outcome == CliOutcome.CliTooOld &&
                CodingAgentCliText.Describe(CodingAgentKind.Codex, tooOld, null).Contains("Press Update CLI"));
            var refusedLog = new List<string>();
            CliAnswer refused = AnswerTo(CodingAgentKind.Codex, FakeCliProcess.Result(1,
                FakeCliProcess.CodexFailure("The requested model is not supported for this account."), ""), refusedLog);
            check("cli runner: a model the server will not serve is a refused model, with the CLI's own words on the pane",
                refused.Outcome == CliOutcome.ModelRefused && refused.Said.Contains("not supported for this account") &&
                CodingAgentCliText.Describe(CodingAgentKind.Codex, refused, null).Contains("not supported for this account"));
            check("cli runner: what a failed CLI said reaches the pane and never the log, which names its class",
                refusedLog.Exists(delegate(string l) { return l.StartsWith("cli: codex remark model-refused", StringComparison.Ordinal); }) &&
                !refusedLog.Exists(delegate(string l) { return l.Contains("not supported for this account"); }));
            // Claude Code's own refusals of a model (lane feature/cli-model-effort; measured 2026-10-09 on 2.1.293 with an id
            // that does not exist): each phrase on its own, so each is proved by itself.
            CliAnswer unknownModel = AnswerTo(Chosen(CodingAgentKind.Claude, "haiku", "low", null), FakeCliProcess.Result(1,
                FakeCliProcess.ClaudeStream("There's an issue with the selected model (claude-nonexistent-9). It may not exist or you may not have access to it. Run --model to pick a different model.",
                    true, "<synthetic>", "claude-nonexistent-9"), ""), new List<string>());
            string unknownWords = CodingAgentCliText.Describe(CodingAgentKind.Claude, unknownModel, null);
            check("cli runner: Claude Code's 'issue with the selected model' is a refused model, named by the alias asked for, with where to choose another",
                unknownModel.Outcome == CliOutcome.ModelRefused && unknownModel.Model.Length == 0 &&
                unknownWords.StartsWith("✗ Claude Code refused the model haiku: There's an issue with the selected model", StringComparison.Ordinal) &&
                unknownWords.EndsWith("Choose another model in the CLI card, then press Validate again.", StringComparison.Ordinal));
            // Review finding F14: a refusal of the module's own default says so, and what else the card offers.
            const string HaikuIsTheDefault = " The model haiku is this module's default, and an organisation or a plan can withhold a model: " +
                                             "choose another one, or Claude Code's default, in the CLI card.";
            check("cli runner: a refused model that is the module's default ends by saying so, and that Claude Code's default is a choice",
                CodingAgentCliText.Describe(CodingAgentKind.Claude, unknownModel, null, "haiku") == unknownWords + HaikuIsTheDefault);
            var refusedSlug = new CliAnswer { Outcome = CliOutcome.ModelRefused, RequestedModel = "gpt-selftest-default-1", Effort = "low", Said = "not supported" };
            check("cli runner: ...and on Codex that Automatic is",
                CodingAgentCliText.Describe(CodingAgentKind.Codex, refusedSlug, null, "gpt-selftest-default-1").EndsWith(
                    " The model gpt-selftest-default-1 is this module's default, and an organisation or a plan can withhold a model: choose another one, or Automatic, in the CLI card.",
                    StringComparison.Ordinal));
            var refusedEffort = new CliAnswer
            {
                Outcome = CliOutcome.ModelRefused, RequestedModel = "haiku", Effort = "low", Said = "the reasoning effort 'low' is not supported",
            };
            check("WITNESS cli runner: a refused model that is not the module's default, a module with no default, a refusal about the effort and an answer that is no refusal say nothing of a default",
                CodingAgentCliText.Describe(CodingAgentKind.Claude, unknownModel, null, "opus") == unknownWords &&
                CodingAgentCliText.Describe(CodingAgentKind.Claude, unknownModel, null, "") == unknownWords &&
                CodingAgentCliText.Describe(CodingAgentKind.Claude, refusedEffort, null, "haiku") == CodingAgentCliText.Describe(CodingAgentKind.Claude, refusedEffort, null) &&
                !CodingAgentCliText.Describe(CodingAgentKind.Claude, new CliAnswer { Outcome = CliOutcome.NotSignedIn, RequestedModel = "haiku" }, null, "haiku")
                    .Contains("this module's default"));
            CliAnswer unrecognized = AnswerTo(CodingAgentKind.Claude, FakeCliProcess.Result(1, "",
                "[claude-code:unrecognized_model] {\"model\":\"claude-nonexistent-9\",\"query_source\":\"sdk\"}\n"));
            check("cli runner: Claude Code's [claude-code:unrecognized_model] on stderr alone is a refused model",
                unrecognized.Outcome == CliOutcome.ModelRefused);
            CliAnswer restricted = AnswerTo(CodingAgentKind.Claude, FakeCliProcess.Result(1,
                FakeCliProcess.ClaudeStream("claude-opus-5-5 is restricted by your organization's settings.", true), ""));
            check("cli runner: a model the organisation restricts is a refused model, and with none chosen it is the default that was refused",
                restricted.Outcome == CliOutcome.ModelRefused &&
                CodingAgentCliText.Describe(CodingAgentKind.Claude, restricted, null).StartsWith("✗ Claude Code refused its default model:", StringComparison.Ordinal));

            CliAnswer silent = AnswerTo(CodingAgentKind.Claude, FakeCliProcess.Result(0, "", ""));
            check("cli runner: a call that ends with nothing to say is no answer, not a success", silent.Outcome == CliOutcome.NoAnswer);
            CliAnswer crashed = AnswerTo(CodingAgentKind.Codex, FakeCliProcess.Result(3, "", "thread 'main' panicked"));
            check("WITNESS cli runner: an unrecognised failure is a plain failure naming the exit code",
                crashed.Outcome == CliOutcome.Failed &&
                CodingAgentCliText.Describe(CodingAgentKind.Codex, crashed, null).StartsWith("✗ Codex failed (exit 3)", StringComparison.Ordinal));

            using (var scratch = new FakeCliScratch())
            {
                FakeCliCall seen = null;
                var fake = new FakeCliProcess();
                fake.Respond = async delegate(FakeCliCall call, CancellationToken token)
                {
                    if (!call.IsModelCall) return await FakeCliProcess.Answering("ok")(call, token).ConfigureAwait(false);
                    seen = call;
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    return FakeCliProcess.Result(0, "", "");
                };
                CodingAgentCli runner = scratch.NewRunner(fake, new List<string>());
                CliRequest slow = Remark(CodingAgentKind.Claude, "p", null);
                slow.Timeout = TimeSpan.FromMilliseconds(300);
                // Waited for here rather than through Wait, so a bound that never fires is THIS check's failure (after
                // ten seconds) and not a throw that the group's catch would name instead.
                Task<CliAnswer> bounded = runner.AskAsync(slow, CancellationToken.None);
                bool ended = bounded.Wait(TimeSpan.FromSeconds(10));
                CliAnswer timedOut = ended ? bounded.Result : null;
                check("cli runner: a call past its bound is timed out, and the child is told to end",
                    timedOut != null && timedOut.Outcome == CliOutcome.TimedOut && seen != null && seen.TokenCancelled &&
                    CodingAgentCliText.Describe(CodingAgentKind.Claude, timedOut, slow.Timeout).Contains("did not answer within"));
                if (!ended) return;   // the held call still owns the gate; the cancellation check below would read busy
                using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                {
                    CliRequest waiting = Remark(CodingAgentKind.Claude, "p", null);
                    CliAnswer cancelled = Wait(runner.AskAsync(waiting, cancel.Token));
                    check("cli runner: a call its caller cancels is cancelled, not timed out", cancelled.Outcome == CliOutcome.Cancelled);
                }
            }
        }

        // ---- single flight, update, staging ----

        private static void CheckSingleFlightAndUpdate(Action<string, bool> check)
        {
            check("cli runner: npm's retirement name is recognised for the live package's own name",
                CodingAgentCli.IsNpmStagingName(".codex-AbCd1234", new[] { "codex" }) &&
                CodingAgentCli.IsNpmStagingName(".codex-win32-x64-Zz09yY8x", new[] { "codex", "codex-win32-x64" }));
            check("cli runner: no other name is a staging folder (the live package, a short or long hash, a stray character)",
                !CodingAgentCli.IsNpmStagingName("codex", new[] { "codex" }) &&
                !CodingAgentCli.IsNpmStagingName(".codex-AbCd123", new[] { "codex" }) &&
                !CodingAgentCli.IsNpmStagingName(".codex-AbCd12345", new[] { "codex" }) &&
                !CodingAgentCli.IsNpmStagingName(".codex-AbCd_234", new[] { "codex" }) &&
                !CodingAgentCli.IsNpmStagingName("..codex-AbCd1234", new[] { "codex" }) &&
                !CodingAgentCli.IsNpmStagingName(".codex-AbCd1234", new[] { "claude-code" }));

            using (var scratch = new FakeCliScratch())
            {
                var log = new List<string>();
                var hold = new TaskCompletionSource<CliProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool updated = false;
                var fake = new FakeCliProcess();
                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    if (call.IsModelCall) return hold.Task;
                    if (call.Is("--version"))
                        return Task.FromResult(FakeCliProcess.Result(0, updated ? "2.1.300 (Claude Code)" : "2.1.292 (Claude Code)", ""));
                    return FakeCliProcess.Answering("ok")(call, token);
                };
                CodingAgentCli runner = scratch.NewRunner(fake, log);

                string staging = Path.Combine(scratch.ClaudeScope, ".claude-code-AbCd1234");
                FakeCliScratch.Touch(Path.Combine(staging, "bin", "claude.exe"), new string('x', 2048));
                string shortName = Path.Combine(scratch.ClaudeScope, ".claude-code-short");
                string longName = Path.Combine(scratch.ClaudeScope, ".claude-code-AbCd12345");
                string otherPackage = Path.Combine(scratch.ClaudeScope, ".other-AbCd1234");
                Directory.CreateDirectory(shortName);
                Directory.CreateDirectory(longName);
                Directory.CreateDirectory(otherPackage);
                string strayFile = Path.Combine(scratch.ClaudeScope, ".claude-code-ZzZz9999");
                File.WriteAllText(strayFile, "a file, not a folder");

                Task<CliAnswer> first = runner.AskAsync(Remark(CodingAgentKind.Claude, "first", null), CancellationToken.None);
                // Waited for here rather than through Wait: without the gate the second call sits on the held fake, and
                // that must be THIS check's failure, not a throw the group's catch would name instead.
                Task<CliAnswer> secondCall = runner.AskAsync(Remark(CodingAgentKind.Claude, "second", null), CancellationToken.None);
                bool answeredAtOnce = secondCall.Wait(TimeSpan.FromSeconds(5));
                check("cli runner: a second call while one is running answers busy at once and starts nothing",
                    answeredAtOnce && secondCall.Result.Outcome == CliOutcome.Busy &&
                    fake.Calls.FindAll(delegate(FakeCliCall x) { return x.IsModelCall; }).Count == 1);
                if (!answeredAtOnce)
                {
                    hold.SetResult(FakeCliProcess.Result(0, FakeCliProcess.ClaudeStream("done", false), ""));
                    return;   // both calls held the same fake; the update checks below need the gate this run lacks
                }
                string refusedUpdate = Wait(runner.UpdateAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("cli runner: Update CLI refuses while a call is in flight and runs nothing",
                    refusedUpdate.StartsWith("⚠ Not now", StringComparison.Ordinal) &&
                    !fake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("update"); }) && Directory.Exists(staging));
                hold.SetResult(FakeCliProcess.Result(0, FakeCliProcess.ClaudeStream("done", false), ""));
                check("WITNESS cli runner: the held call still answers once released", Wait(first).Ok);

                var updateHold = new TaskCompletionSource<CliProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool stagingThereWhileRunning = false;
                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    if (call.Is("update"))
                    {
                        stagingThereWhileRunning = Directory.Exists(staging);
                        return updateHold.Task;
                    }
                    if (call.Is("--version"))
                        return Task.FromResult(FakeCliProcess.Result(0, updated ? "2.1.300 (Claude Code)" : "2.1.292 (Claude Code)", ""));
                    return FakeCliProcess.Answering("ok")(call, token);
                };
                Task<string> update = runner.UpdateAsync(CodingAgentKind.Claude, CancellationToken.None);
                SpinWait.SpinUntil(delegate { return fake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("update"); }); }, 5000);
                CliAnswer duringUpdate = Wait(runner.AskAsync(Remark(CodingAgentKind.Claude, "during", null), CancellationToken.None));
                bool stillThereBeforeExit = Directory.Exists(staging);
                updated = true;
                updateHold.SetResult(FakeCliProcess.Result(0, "Successfully updated", ""));
                string done = Wait(update);
                check("cli runner: no call starts while an update runs", duringUpdate.Outcome == CliOutcome.Busy);
                check("cli runner: the staging folder is untouched until the update process has exited",
                    stagingThereWhileRunning && stillThereBeforeExit);
                check("cli runner: Update CLI reports the old and the new version",
                    done.StartsWith("✓ Claude Code updated: 2.1.292 -> 2.1.300.", StringComparison.Ordinal));
                check("cli runner: after the update, npm's staging folder beside the package is removed and said",
                    !Directory.Exists(staging) && done.Contains("Removed 1 leftover npm staging folder"));
                check("cli runner: the live package, near-miss names, another package's folder and a file are all kept",
                    File.Exists(scratch.ClaudeExe) && Directory.Exists(shortName) && Directory.Exists(longName) &&
                    Directory.Exists(otherPackage) && File.Exists(strayFile));

                FakeCliScratch.Touch(Path.Combine(staging, "bin", "claude.exe"), "again");
                runner.UpdateBound = TimeSpan.FromMilliseconds(300);
                fake.Respond = async delegate(FakeCliCall call, CancellationToken token)
                {
                    if (call.Is("update")) { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
                    return await FakeCliProcess.Answering("ok")(call, token).ConfigureAwait(false);
                };
                string stuck = Wait(runner.UpdateAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("cli runner: an update stopped at its bound removes nothing, since npm may still be working there",
                    stuck.StartsWith("✗ Claude Code's update did not finish", StringComparison.Ordinal) && Directory.Exists(staging));
            }
        }

        // ---- the card's details ----

        private static void CheckDetails(Action<string, bool> check)
        {
            using (var scratch = new FakeCliScratch())
            {
                var log = new List<string>();
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("OK") };
                CodingAgentCli runner = scratch.NewRunner(fake, log);
                CodingAgentCli.CliDetails claude = Wait(runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("cli runner: the card names Claude Code's version and the account `claude auth status` names",
                    claude.Installed && claude.Version == "2.1.292" && claude.SignedIn == "someone@example.invalid (max)");
                CodingAgentCli.CliDetails codex = Wait(runner.RefreshDetailsAsync(CodingAgentKind.Codex, CancellationToken.None));
                check("cli runner: the card names Codex's version, its pick and the sign-in method it reports",
                    codex.Installed && codex.Version == "0.160.1" && codex.TextModel == "text-only-low" &&
                    codex.VisionModel == "vision-second" && codex.SignedIn == "a ChatGPT account (Codex does not say which)");
                check("cli runner: a Codex API-key sign-in never shows the masked key",
                    CodingAgentCli.DescribeCodexLoginStatus("Logged in using an API key - sk-proj-***abcd") ==
                    "an API key (Codex does not say which account)");
                check("cli runner: a signed-out Claude Code says how to sign in",
                    CodingAgentCli.DescribeClaudeAuthStatus("{\"loggedIn\":false}", false).StartsWith("✗ Not signed in. To sign in, run claude", StringComparison.Ordinal));
                check("cli runner: a token from the user's own environment is named as that, not as the card's",
                    CodingAgentCli.DescribeClaudeAuthStatus("{\"loggedIn\":true,\"authMethod\":\"oauth_token\"}", false)
                        .StartsWith("A sign-in token from CLAUDE_CODE_OAUTH_TOKEN in your environment", StringComparison.Ordinal));
                check("cli runner: a signed-out Codex says how to sign in",
                    CodingAgentCli.DescribeCodexLoginStatus("Not logged in").StartsWith("✗ Not signed in. To sign in, run codex login", StringComparison.Ordinal));
                check("cli runner: the card's details stay in the cache it serves the pane from",
                    runner.CachedDetails(CodingAgentKind.Claude) == claude);

                CliAnswer validated = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None));
                string status = runner.LastValidation(CodingAgentKind.Claude);
                // Re-pointed in round 2: the line names the CLI it tested, since the card's row shows either CLI's.
                check("cli runner: Validate's result is kept for the card's Status row with the CLI, its time, duration, model and effort",
                    validated.Ok && status != null && status.StartsWith("✓ Claude Code: answered at ", StringComparison.Ordinal) &&
                    status.EndsWith(" s, on " + FakeCliProcess.DefaultAnsweredModel + " at " + CodingAgentCli.DefaultEffort + " effort", StringComparison.Ordinal));
                check("WITNESS cli runner: a CLI not validated this session has no Status line yet",
                    runner.LastValidation(CodingAgentKind.Codex) == null);
                check("cli runner: no log line carries an account, a key or anything the CLI said",
                    log.Count > 0 && !log.Exists(delegate(string l) { return l.Contains("@") || l.Contains("sk-") || l.Contains("ChatGPT"); }));
            }
        }

        // ---- installs the PATH does not lead to (bug report 2026-10-07) ----

        private static readonly bool Arm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                                           System.Runtime.InteropServices.Architecture.Arm64;

        private static void CheckLocateElsewhere(Action<string, bool> check)
        {
            using (var scratch = new FakeCliScratch())
            {
                // A host started before the install: its own PATH lacks the folder the PATH saved since then has.
                string later = Path.Combine(scratch.Root, "installed-later");
                FakeCliScratch.Touch(Path.Combine(later, "claude.exe"), "");
                check("cli runner: a CLI on the PATH saved since the host started is found though the host's own PATH lacks it",
                    CodingAgentLocator.Locate(CodingAgentKind.Claude,
                        new CliEnvironment { PathValue = scratch.Root, PersistedPathValue = later, AppData = "", UserProfile = "" }) != null);

                // WinGet's package folders, with nothing on PATH (WinGet reached them through a link the trust rules refuse).
                string local = Path.Combine(scratch.Root, "local");
                string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                var winget = new CliEnvironment { PathValue = "", AppData = "", UserProfile = "", LocalAppData = local };
                FakeCliScratch.Touch(Path.Combine(packages, "Anthropic.ClaudeCodeHelper_Microsoft.Winget.Source_8wekyb3d8bbwe", "claude.exe"), "");
                check("cli runner: a WinGet package whose id only begins like Claude Code's is not Claude Code",
                    CodingAgentLocator.Locate(CodingAgentKind.Claude, winget) == null);
                string claudePackage = Path.Combine(packages, "Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe");
                FakeCliScratch.Touch(Path.Combine(claudePackage, "claude.exe"), "");
                CliInstall wingetClaude = CodingAgentLocator.Locate(CodingAgentKind.Claude, winget);
                check("cli runner: Claude Code from WinGet is found in its package folder with nothing on PATH, and known as WinGet's",
                    wingetClaude != null && wingetClaude.Source == CliSource.WinGet &&
                    string.Equals(wingetClaude.Executable, Path.Combine(claudePackage, "claude.exe"), StringComparison.OrdinalIgnoreCase));
                string codexName = Arm ? "codex-aarch64-pc-windows-msvc.exe" : "codex-x86_64-pc-windows-msvc.exe";
                FakeCliScratch.Touch(Path.Combine(packages, "OpenAI.Codex_Microsoft.Winget.Source_8wekyb3d8bbwe", codexName), "");
                CliInstall wingetCodex = CodingAgentLocator.Locate(CodingAgentKind.Codex, winget);
                check("cli runner: Codex from WinGet is found under its release name, and known as WinGet's",
                    wingetCodex != null && wingetCodex.Source == CliSource.WinGet &&
                    string.Equals(Path.GetFileName(wingetCodex.Executable), codexName, StringComparison.OrdinalIgnoreCase));
                CliInstall wingetOnPath = CodingAgentLocator.Locate(CodingAgentKind.Claude,
                    new CliEnvironment { PathValue = claudePackage, AppData = "", UserProfile = "" });
                check("cli runner: WinGet's package folder put on PATH (where WinGet makes no link) is still WinGet's install",
                    wingetOnPath != null && wingetOnPath.Source == CliSource.WinGet);
                CliInstall ordinary = CodingAgentLocator.Locate(CodingAgentKind.Claude,
                    new CliEnvironment { PathValue = later, AppData = "", UserProfile = "" });
                check("WITNESS cli runner: a claude.exe in an ordinary PATH folder belongs to no installer",
                    ordinary != null && ordinary.Source == CliSource.Path);

                // VS Code's Claude Code extension: the newest copy this machine can run, never another platform's.
                string profile = Path.Combine(scratch.Root, "profile");
                string extensions = Path.Combine(profile, ".vscode", "extensions");
                string own = Arm ? "win32-arm64" : "win32-x64";
                string older = Path.Combine(extensions, "anthropic.claude-code-2.1.289-" + own, "resources", "native-binary", "claude.exe");
                string newer = Path.Combine(extensions, "anthropic.claude-code-2.1.292-" + own, "resources", "native-binary", "claude.exe");
                string foreign = Path.Combine(extensions, "anthropic.claude-code-2.1.300-" + (Arm ? "darwin-arm64" : "win32-arm64"),
                    "resources", "native-binary", "claude.exe");
                FakeCliScratch.Touch(older, "");
                FakeCliScratch.Touch(newer, "");
                FakeCliScratch.Touch(foreign, "");
                var vscode = new CliEnvironment { PathValue = "", AppData = "", UserProfile = profile };
                CliInstall fromExtension = CodingAgentLocator.Locate(CodingAgentKind.Claude, vscode);
                check("cli runner: with no Claude Code of its own, the newest copy this machine can run inside VS Code's extension is used, and known as the extension's",
                    fromExtension != null && fromExtension.Source == CliSource.VsCodeExtension &&
                    string.Equals(fromExtension.Executable, newer, StringComparison.OrdinalIgnoreCase));
                check("cli runner: Codex is never taken from a VS Code extension folder",
                    CodingAgentLocator.Locate(CodingAgentKind.Codex, vscode) == null);
                string foreignOnly = Path.Combine(scratch.Root, "foreign-profile");
                FakeCliScratch.Touch(Path.Combine(foreignOnly, ".vscode", "extensions",
                    "anthropic.claude-code-2.1.300-" + (Arm ? "darwin-arm64" : "win32-arm64"), "resources", "native-binary", "claude.exe"), "");
                check("cli runner: an extension copy built for a platform this machine cannot run is never used, even alone",
                    CodingAgentLocator.Locate(CodingAgentKind.Claude, new CliEnvironment { PathValue = "", AppData = "", UserProfile = foreignOnly }) == null);
                FakeCliScratch.Touch(Path.Combine(profile, ".local", "bin", "claude.exe"), "");
                CliInstall installed = CodingAgentLocator.Locate(CodingAgentKind.Claude, vscode);
                check("cli runner: Claude Code's own installer outranks the extension's copy",
                    installed != null && installed.Source == CliSource.NativeInstaller);
            }
        }

        private static void CheckUpdatedElsewhere(Action<string, bool> check)
        {
            using (var scratch = new FakeCliScratch())
            {
                string local = Path.Combine(scratch.Root, "local");
                FakeCliScratch.Touch(Path.Combine(local, "Microsoft", "WinGet", "Packages",
                    "Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe", "claude.exe"), "");
                var log = new List<string>();
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("ok") };
                CodingAgentCli runner = scratch.NewRunner(fake, log);
                var winget = new CliEnvironment { PathValue = "", AppData = "", UserProfile = "", LocalAppData = local };
                runner.EnvironmentSource = delegate { return winget; };
                string answer = Wait(runner.UpdateAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("cli runner: Update CLI on a WinGet install names WinGet's upgrade and starts nothing",
                    answer.StartsWith("⚠ This Claude Code was installed with WinGet", StringComparison.Ordinal) &&
                    answer.Contains("winget upgrade Anthropic.ClaudeCode") && fake.Calls.Count == 0 &&
                    log.Contains("cli: claude update not run, source=winget"));
                CodingAgentCli.CliDetails details = Wait(runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("cli runner: the card says a WinGet install is WinGet's", details.Installed && details.Where == "installed with WinGet");

                string profile = Path.Combine(scratch.Root, "profile");
                FakeCliScratch.Touch(Path.Combine(profile, ".vscode", "extensions", "anthropic.claude-code-2.1.292-" + (Arm ? "win32-arm64" : "win32-x64"),
                    "resources", "native-binary", "claude.exe"), "");
                var vscode = new CliEnvironment { PathValue = "", AppData = "", UserProfile = profile };
                runner.EnvironmentSource = delegate { return vscode; };
                fake.Clear();
                string extension = Wait(runner.UpdateAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("cli runner: Update CLI leaves the copy inside VS Code's extension to VS Code and starts nothing",
                    extension.StartsWith("⚠ This Claude Code is the copy inside VS Code's Claude Code extension", StringComparison.Ordinal) &&
                    fake.Calls.Count == 0);

                runner.EnvironmentSource = delegate { return scratch.PathOnly; };
                string npm = Wait(runner.UpdateAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("WITNESS cli runner: an npm install is still updated by the CLI's own update",
                    fake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("update"); }) && npm.StartsWith("✓", StringComparison.Ordinal));
            }
        }

        // ---- the saved sign-in token (owner request 2026-10-07) ----

        private const string SelfTestToken = "sk-ant-oat01-SELFTEST-not-a-real-token-0123456789abcdefABCDEF_-";

        private static bool Carries(FakeCliCall call, string token)
        {
            string value;
            return call != null && call.Environment.TryGetValue("CLAUDE_CODE_OAUTH_TOKEN", out value) && value == token;
        }

        private static FakeCliCall LastModelCall(FakeCliProcess fake, bool codex)
        {
            return fake.Calls.FindLast(delegate(FakeCliCall x) { return x.IsModelCall && x.IsCodex == codex; });
        }

        private static void CheckToken(Action<string, bool> check)
        {
            string token;
            check("cli runner: an API key is refused as a sign-in token, with the way to make one",
                (CodingAgentCli.CheckClaudeToken("sk-ant-api03-abcdef", out token) ?? "").Contains("claude setup-token"));
            check("cli runner: a token with a space inside is refused as not copied whole",
                CodingAgentCli.CheckClaudeToken("sk-ant-oat01-abc def", out token) != null);
            check("WITNESS cli runner: a pasted token's line break is trimmed and the token accepted",
                CodingAgentCli.CheckClaudeToken(SelfTestToken + "\r\n", out token) == null && token == SelfTestToken);
            // 1.3.2: what the owner's 1.3.1 saved (a web address), and any character a bearer token cannot hold.
            check("cli runner: a web address is refused as a sign-in token, by name",
                (CodingAgentCli.CheckClaudeToken("https://claude.ai/settings/claude-code", out token) ?? "")
                    .StartsWith("That is a web address, not a sign-in token", StringComparison.Ordinal));
            check("cli runner: a character no bearer token holds is refused, and named",
                (CodingAgentCli.CheckClaudeToken("sk-ant-oat01-abc:def", out token) ?? "").Contains("it has a \":\" in it"));
            check("WITNESS cli runner: every character RFC 6750 allows in a bearer token is accepted, = at the end included",
                CodingAgentCli.CheckClaudeToken("sk-ant-oat01-Az09-._~+/xyz==", out token) == null);

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ANTHROPIC_API_KEY"] = "k", ["anthropic_auth_token"] = "b", ["CLAUDE_CODE_USE_BEDROCK"] = "1",
                ["CLAUDE_CODE_USE_VERTEX"] = "1", ["CLAUDE_CODE_USE_FOUNDRY"] = "1", ["PATH"] = "p",
            };
            var untouched = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
            CodingAgentCli.ApplyClaudeToken(environment, SelfTestToken);
            bool outrankersGone = true;
            foreach (string name in CodingAgentCli.OutrankingClaudeCredentials) outrankersGone &= !environment.ContainsKey(name);
            check("cli runner: the saved token goes on a Claude Code child and every credential that would outrank it comes off",
                environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN") && environment["CLAUDE_CODE_OAUTH_TOKEN"] == SelfTestToken &&
                outrankersGone && environment.Count == 2);
            CodingAgentCli.ApplyClaudeToken(untouched, null);
            check("WITNESS cli runner: with no token saved, the child's environment is the user's own",
                untouched.Count == 6 && untouched.ContainsKey("ANTHROPIC_API_KEY") && !untouched.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));

            using (var scratch = new FakeCliScratch())
            {
                var log = new List<string>();
                var fake = new FakeCliProcess();
                fake.Respond = delegate(FakeCliCall call, CancellationToken cancel)
                {
                    if (call.Is("auth", "status") && Carries(call, SelfTestToken))
                        return Task.FromResult(FakeCliProcess.Result(0, "{\"loggedIn\":true,\"authMethod\":\"oauth_token\",\"apiProvider\":\"firstParty\"}", ""));
                    return FakeCliProcess.Answering("OK")(call, cancel);
                };
                CodingAgentCli runner = scratch.NewRunner(fake, log);
                string read;
                check("WITNESS cli runner: a new module folder has no token saved",
                    runner.ReadClaudeToken(out read) == CodingAgentCli.ClaudeTokenState.None);
                string error;
                check("cli runner: an API key typed into the token field is not saved",
                    !runner.TrySetClaudeToken("sk-ant-api03-abcdef", out error) && error != null &&
                    runner.ReadClaudeToken(out read) == CodingAgentCli.ClaudeTokenState.None);

                // 1.3.3: Validate SAVES a typed token that answers, and only one that answers (ValidateAsync says why).
                Func<FakeCliCall, CancellationToken, Task<CliProcessResult>> answering = fake.Respond;
                fake.Respond = delegate(FakeCliCall call, CancellationToken cancel)
                {
                    if (call.IsModelCall && !call.IsCodex)
                        return Task.FromResult(FakeCliProcess.Result(1,
                            FakeCliProcess.ClaudeStream("Failed to authenticate. API Error: 401 OAuth access token is invalid.", true), ""));
                    return answering(call, cancel);
                };
                CliAnswer typedRefused = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None, SelfTestToken));
                check("cli runner: a typed token Claude Code refuses is not saved",
                    !typedRefused.Ok && typedRefused.UsedUnsavedToken && !typedRefused.TypedTokenSaved &&
                    runner.ReadClaudeToken(out read) == CodingAgentCli.ClaudeTokenState.None);
                fake.Respond = answering;

                CliAnswer typed = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None, SelfTestToken));
                check("cli runner: Validate tests a token typed and not applied yet, and saves it when it answers",
                    typed.Ok && typed.UsedSavedToken && typed.TypedTokenSaved && Carries(LastModelCall(fake, false), SelfTestToken) &&
                    runner.ReadClaudeToken(out read) == CodingAgentCli.ClaudeTokenState.Saved && read == SelfTestToken);
                check("cli runner: Validate's tick on a typed token says it is now saved and needs no Apply",
                    CodingAgentCliText.Describe(CodingAgentKind.Claude, typed, null).EndsWith(
                        "That was the token typed in this card, and it is now saved: no Apply needed.", StringComparison.Ordinal));
                check("cli runner: the Status line after Validate keeps a typed token is that Validate's, not forgotten by the save",
                    (runner.LastValidation(CodingAgentKind.Claude) ?? "").StartsWith("✓ Claude Code: answered at ", StringComparison.Ordinal));

                CliAnswer onSaved = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("WITNESS cli runner: a tick on the saved token says nothing about the typed one",
                    onSaved.Ok && !onSaved.UsedUnsavedToken && !CodingAgentCliText.Describe(CodingAgentKind.Claude, onSaved, null).Contains("typed in this card"));
                string validatedBefore = runner.LastValidation(CodingAgentKind.Claude);
                bool stored = runner.TrySetClaudeToken(" " + SelfTestToken + "\n", out error);
                string file = Path.Combine(scratch.Data, "cli", CodingAgentCli.ClaudeTokenFileName);
                string onDisk = File.Exists(file) ? File.ReadAllText(file) : "";
                check("cli runner: the token is saved sealed, never readable in the module's folder",
                    stored && onDisk.Length > 0 && !onDisk.Contains(SelfTestToken) && !onDisk.Contains("sk-ant") &&
                    runner.ReadClaudeToken(out read) == CodingAgentCli.ClaudeTokenState.Saved && read == SelfTestToken);
                check("cli runner: a new token drops the last Validate, which ran on the old sign-in",
                    validatedBefore != null && runner.LastValidation(CodingAgentKind.Claude) == null);

                CliAnswer claude = Wait(runner.AskAsync(Remark(CodingAgentKind.Claude, "p", null), CancellationToken.None));
                check("cli runner: a Claude Code call carries the saved token, and its answer says it ran on it",
                    claude.Ok && claude.UsedSavedToken && Carries(LastModelCall(fake, false), SelfTestToken));
                CliAnswer codex = Wait(runner.AskAsync(Remark(CodingAgentKind.Codex, "p", null), CancellationToken.None));
                Wait(runner.RefreshDetailsAsync(CodingAgentKind.Codex, CancellationToken.None));
                check("cli runner: no Codex call or probe ever carries the Claude Code token",
                    codex.Ok && !codex.UsedSavedToken && LastModelCall(fake, true) != null &&
                    !fake.Calls.Exists(delegate(FakeCliCall x) { return x.IsCodex && Carries(x, SelfTestToken); }));
                fake.Clear();
                CodingAgentCli.CliDetails details = Wait(runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("cli runner: the card says the saved token is the sign-in, since Claude Code names no account for one",
                    details.SignedIn.StartsWith("The sign-in token saved in this card", StringComparison.Ordinal));
                FakeCliCall statusCall = fake.Calls.Find(delegate(FakeCliCall x) { return x.Is("auth", "status"); });
                FakeCliCall versionCall = fake.Calls.Find(delegate(FakeCliCall x) { return x.Is("--version"); });
                check("cli runner: auth status carries the saved token and --version does not",
                    Carries(statusCall, SelfTestToken) && versionCall != null && !Carries(versionCall, SelfTestToken));
                string updatedWithToken = Wait(runner.UpdateAsync(CodingAgentKind.Claude, CancellationToken.None));
                FakeCliCall updateCall = fake.Calls.Find(delegate(FakeCliCall x) { return x.Is("update"); });
                check("cli runner: Update CLI never carries the sign-in token, which it does not need and a refused one would stop",
                    updateCall != null && !Carries(updateCall, SelfTestToken) && updatedWithToken.StartsWith("✓", StringComparison.Ordinal));

                fake.Respond = delegate(FakeCliCall call, CancellationToken cancel)
                {
                    if (call.IsModelCall && !call.IsCodex)
                        return Task.FromResult(FakeCliProcess.Result(1,
                            FakeCliProcess.ClaudeStream("Failed to authenticate. API Error: 401 OAuth access token is invalid.", true), ""));
                    return FakeCliProcess.Answering("OK")(call, cancel);
                };
                CliAnswer refused = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None));
                string refusedWords = CodingAgentCliText.Describe(CodingAgentKind.Claude, refused, null);
                check("cli runner: a refused saved token says to replace or remove it, not to sign in with /login",
                    refused.Outcome == CliOutcome.NotSignedIn && refused.UsedSavedToken &&
                    refusedWords.StartsWith("✗ Claude Code refused the sign-in token in this card", StringComparison.Ordinal) &&
                    !refusedWords.Contains("/login"));
                fake.Respond = delegate(FakeCliCall call, CancellationToken cancel)
                {
                    if (call.IsModelCall && !call.IsCodex)
                        return Task.FromResult(FakeCliProcess.Result(1, "",
                            "Your organization requires remote managed settings to load, but they could not be loaded. Run `claude auth login` to re-authenticate, check your network connection, or contact your administrator.\n"));
                    return FakeCliProcess.Answering("OK")(call, cancel);
                };
                CliAnswer managed = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None));
                check("cli runner: a token refused where managed settings are required is still named as the token's refusal",
                    managed.Outcome == CliOutcome.SignInExpired &&
                    CodingAgentCliText.Describe(CodingAgentKind.Claude, managed, null).StartsWith("✗ Claude Code refused the sign-in token", StringComparison.Ordinal));

                File.WriteAllText(file, Convert.ToBase64String(Encoding.UTF8.GetBytes("sealed for some other account")));
                int modelCallsBefore = fake.Calls.FindAll(delegate(FakeCliCall x) { return x.IsModelCall; }).Count;
                CliAnswer unreadable = Wait(runner.AskAsync(Remark(CodingAgentKind.Claude, "p", null), CancellationToken.None));
                check("cli runner: a token this account cannot unseal stops the call before it starts, rather than using another sign-in",
                    unreadable.Outcome == CliOutcome.TokenUnreadable &&
                    fake.Calls.FindAll(delegate(FakeCliCall x) { return x.IsModelCall; }).Count == modelCallsBefore &&
                    CodingAgentCliText.Describe(CodingAgentKind.Claude, unreadable, null) == CodingAgentCliText.TokenUnreadableSentence);
                check("cli runner: the card says a saved token cannot be read",
                    Wait(runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None)).SignedIn == CodingAgentCliText.TokenUnreadableSentence);

                // What a 1.3.1 install could save: a value that unseals fine and is not a token (the owner's was a web
                // address). It is never handed to Claude Code, and the card and the call say what it is.
                File.WriteAllText(file, Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                    Encoding.UTF8.GetBytes("https://example.invalid/not/a/token"), CodingAgentCli.ClaudeTokenEntropy,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser)));
                int callsBeforeNotAToken = fake.Calls.FindAll(delegate(FakeCliCall x) { return x.IsModelCall; }).Count;
                CliAnswer notAToken = Wait(runner.AskAsync(Remark(CodingAgentKind.Claude, "p", null), CancellationToken.None));
                check("cli runner: a saved value that is not a token stops the call before it starts, and says so",
                    runner.ReadClaudeToken(out read) == CodingAgentCli.ClaudeTokenState.NotAToken && read == null &&
                    notAToken.Outcome == CliOutcome.TokenNotAToken &&
                    fake.Calls.FindAll(delegate(FakeCliCall x) { return x.IsModelCall; }).Count == callsBeforeNotAToken &&
                    CodingAgentCliText.Describe(CodingAgentKind.Claude, notAToken, null) == CodingAgentCliText.TokenNotATokenSentence);
                check("cli runner: the card says what is saved is not a sign-in token",
                    Wait(runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None)).SignedIn == CodingAgentCliText.TokenNotATokenSentence);

                string removed = runner.RemoveClaudeToken();
                check("cli runner: Remove token deletes the token, says so, and logs the delete",
                    removed.StartsWith("✓ Removed.", StringComparison.Ordinal) && !File.Exists(file) &&
                    log.Contains("cli: claude sign-in token removed"));
                fake.Respond = FakeCliProcess.Answering("OK");
                CliAnswer after = Wait(runner.AskAsync(Remark(CodingAgentKind.Claude, "p", null), CancellationToken.None));
                check("cli runner: after Remove token a Claude Code call runs on the CLI's own sign-in",
                    after.Ok && !after.UsedSavedToken && !Carries(LastModelCall(fake, false), SelfTestToken));
                check("cli runner: no log line carries the token, and its save is logged",
                    log.Contains("cli: claude sign-in token saved") &&
                    !log.Exists(delegate(string l) { return l.Contains(SelfTestToken) || l.Contains("sk-ant"); }));
            }
        }

        // ---- the model and the effort (lane feature/cli-model-effort, owner decision 2026-10-09) ----

        private static bool HasArgument(FakeCliCall call, string flag)
        {
            return call != null && (call.Arguments.Contains(flag) ||
                call.Arguments.Exists(delegate(string a) { return a.StartsWith(flag, StringComparison.Ordinal); }));
        }

        private static string EnvironmentOf(FakeCliCall call, string name)
        {
            return call == null ? null : ValueOf(call.Environment, name);
        }

        private static string ValueOf(IDictionary<string, string> environment, string name)
        {
            string value;
            return environment != null && environment.TryGetValue(name, out value) ? value : null;
        }

        private static void CheckModelAndEffort(Action<string, bool> check)
        {
            string model, effort;
            bool everyPair = true;
            foreach (string alias in CodingAgentCli.ClaudeModelAliases)
                foreach (string level in CodingAgentCli.Efforts)
                    everyPair &= CodingAgentCli.CheckChoice(CodingAgentKind.Claude, alias, level, out model, out effort) == null &&
                                 model == alias && effort == level;
            check("WITNESS cli runner: haiku, sonnet and opus at low, medium and high are each passed on as chosen",
                everyPair && CodingAgentCli.ClaudeModelAliases.Length == 3 && CodingAgentCli.Efforts.Length == 3);
            bool modelsRefused = true;
            foreach (string notAnAlias in new[] { "claude-haiku-5-5", "Haiku", "--bare", "opus[1m]", "haiku sonnet" })
                modelsRefused &= CodingAgentCli.CheckChoice(CodingAgentKind.Claude, notAnAlias, "low", out model, out effort) != null &&
                                 model.Length == 0 && effort.Length == 0;
            check("cli runner: a Claude Code model that is not an alias is refused, a full model id and an option included",
                modelsRefused);
            bool effortsRefused = true;
            foreach (string notOffered in new[] { "xhigh", "max", "LOW", "minimal", "-c" })
                effortsRefused &= CodingAgentCli.CheckChoice(CodingAgentKind.Claude, "haiku", notOffered, out model, out effort) != null &&
                                  CodingAgentCli.CheckChoice(CodingAgentKind.Codex, "", notOffered, out model, out effort) != null;
            check("cli runner: an effort that is not low, medium or high is refused for either CLI, xhigh and max included",
                effortsRefused);
            check("cli runner: no effort named is the runner's default, so no call inherits the user's own",
                CodingAgentCli.CheckChoice(CodingAgentKind.Claude, null, " ", out model, out effort) == null && model == "" &&
                effort == CodingAgentCli.DefaultEffort && CodingAgentCli.DefaultEffort == "low");
            bool slugsRefused = true;
            foreach (string notASlug in new[] { "-m", "GPT-6", "gpt 6", "org/gpt-6", "gpt_6", ".gpt", new string('a', 65) })
                slugsRefused &= CodingAgentCli.CheckChoice(CodingAgentKind.Codex, notASlug, "low", out model, out effort) != null;
            check("cli runner: a Codex model a user chose is a lowercase slug, never one that reads as an option or carries another character",
                slugsRefused);
            check("WITNESS cli runner: a Codex slug of letters, digits, dots and dashes is passed on, trimmed",
                CodingAgentCli.CheckChoice(CodingAgentKind.Codex, " gpt-6.1-sol ", "high", out model, out effort) == null &&
                model == "gpt-6.1-sol" && effort == "high" &&
                CodingAgentCli.CheckChoice(CodingAgentKind.Codex, new string('a', 64), "low", out model, out effort) == null);

            // The command lines.
            List<string> claude = CodingAgentCli.BuildArguments(Chosen(CodingAgentKind.Claude, "sonnet", "high", null), "", null, null);
            var expectedClaude = new List<string>
            {
                "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--no-session-persistence", "--tools", "", "--strict-mcp-config", "--disable-slash-commands",
                "--settings", ExpectedClaudeSettings("high"), "--model", "sonnet", "--effort", "high",
                "--system-prompt", Persona,
            };
            check("cli runner: Claude Code gets --model with the alias chosen and --effort with the level chosen, ahead of the persona",
                claude != null && string.Join("|", claude) == string.Join("|", expectedClaude));
            bool everyShape = true;
            foreach (CliRequest shape in new[]
            {
                Remark(CodingAgentKind.Claude, "text", null), Remark(CodingAgentKind.Claude, "image", SyntheticPng),
                new CliRequest { Agent = CodingAgentKind.Claude, Prompt = "Reply with OK.", Purpose = "validate" },
                Chosen(CodingAgentKind.Claude, "haiku", "medium", SyntheticPng),
            })
            {
                List<string> args = CodingAgentCli.BuildArguments(shape, "", null, null);
                string level = args == null ? null : args[args.IndexOf("--effort") + 1];
                string alias = args == null || args.IndexOf("--model") < 0 ? "" : args[args.IndexOf("--model") + 1];
                everyShape &= args != null && Array.IndexOf(CodingAgentCli.Efforts, level) >= 0 &&
                              alias == (shape.Model ?? "") && args.FindAll(delegate(string a) { return a == "--effort"; }).Count == 1;
            }
            check("cli runner: every Claude Code call shape names one allowed effort, and --model only with the alias chosen",
                everyShape);
            // The effort lever in --settings' env block is the call's own at every effort (review finding F1): a user's
            // settings.json env block would otherwise put its own CLAUDE_CODE_EFFORT_LEVEL back inside the child.
            bool settingsFollowEffort = true;
            foreach (string level in CodingAgentCli.Efforts)
            {
                List<string> args = CodingAgentCli.BuildArguments(Chosen(CodingAgentKind.Claude, "haiku", level, null), "", null, null);
                JsonObject parsed = ParsedSettings(args);
                JsonObject env = parsed == null ? null : parsed["env"] as JsonObject;
                settingsFollowEffort &= env != null && (string)env["CLAUDE_CODE_EFFORT_LEVEL"] == level &&
                                        args[args.IndexOf("--effort") + 1] == level;
            }
            List<string> codex = CodingAgentCli.BuildArguments(Chosen(CodingAgentKind.Codex, "gpt-user-1", "high", null), "text-only-low", null, null);
            check("cli runner: Codex gets the effort chosen and the model chosen, in place of the automatic pick",
                codex != null && codex[codex.IndexOf("-m") + 1] == "gpt-user-1" && !codex.Contains("text-only-low") &&
                codex.Contains("model_reasoning_effort=high") && codex.FindAll(delegate(string a) { return a.StartsWith("model_reasoning_effort=", StringComparison.Ordinal); }).Count == 1);
            check("cli runner: a refused model or effort builds no command line at all",
                CodingAgentCli.BuildArguments(Chosen(CodingAgentKind.Claude, "claude-haiku-5-5", "low", null), "", null, null) == null &&
                CodingAgentCli.BuildArguments(Chosen(CodingAgentKind.Codex, "", "xhigh", null), "text-only-low", null, null) == null);

            // Through the runner.
            using (var scratch = new FakeCliScratch())
            {
                var log = new List<string>();
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("OK") };
                CodingAgentCli runner = scratch.NewRunner(fake, log);
                CliAnswer badModel = Wait(runner.AskAsync(Chosen(CodingAgentKind.Claude, "claude-haiku-5-5", "low", null), CancellationToken.None));
                CliAnswer badEffort = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "", "xhigh", null), CancellationToken.None));
                check("cli runner: a refused model or effort starts nothing at all, not even a version check or Codex's catalog",
                    badModel.Outcome == CliOutcome.ChoiceRefused && badEffort.Outcome == CliOutcome.ChoiceRefused && fake.Calls.Count == 0);
                check("cli runner: the refusal names the value and says where to choose again",
                    CodingAgentCliText.Describe(CodingAgentKind.Claude, badModel, null).StartsWith(
                        "✗ Claude Code was not started: \"claude-haiku-5-5\" is not a model this module runs Claude Code on", StringComparison.Ordinal) &&
                    CodingAgentCliText.Describe(CodingAgentKind.Codex, badEffort, null).EndsWith(
                        "\"xhigh\" is not an effort this module runs it at (low, medium or high). Choose again in the CLI card and press Apply.", StringComparison.Ordinal));
                check("cli runner: a refused setting's value stays on the pane, out of the log, which names the class",
                    log.Exists(delegate(string l) { return l.StartsWith("cli: claude remark choice-refused", StringComparison.Ordinal); }) &&
                    !log.Exists(delegate(string l) { return l.Contains("claude-haiku-5-5") || l.Contains("xhigh"); }));
                // F18: no exit code for a child that was never started (the live check read "exit=0" as a clean run).
                check("cli runner: a call refused before anything started logs that it never started, and no exit code",
                    log.Exists(delegate(string l) { return l.StartsWith("cli: claude remark choice-refused not-started ms=", StringComparison.Ordinal); }) &&
                    !log.Exists(delegate(string l) { return l.Contains("choice-refused") && l.Contains(" exit="); }));

                CliAnswer onSonnet = Wait(runner.AskAsync(Chosen(CodingAgentKind.Claude, "sonnet", "high", null), CancellationToken.None));
                FakeCliCall sonnetCall = LastModelCall(fake, false);
                check("WITNESS cli runner: a call that ran logs the exit code its child left",
                    log.Exists(delegate(string l) { return l.StartsWith("cli: claude remark ok exit=0 ms=", StringComparison.Ordinal); }));
                check("cli runner: a Claude Code model call runs with the advisor tool and the title request off",
                    onSonnet.Ok && EnvironmentOf(sonnetCall, "CLAUDE_CODE_DISABLE_ADVISOR_TOOL") == "1" &&
                    EnvironmentOf(sonnetCall, "CLAUDE_CODE_DISABLE_TERMINAL_TITLE") == "1");
                JsonObject sentSettings = ParsedSettings(sonnetCall == null ? null : sonnetCall.Arguments);
                JsonObject sentEnv = sentSettings == null ? null : sentSettings["env"] as JsonObject;
                check("cli runner: a Claude Code model call's --settings sets the call's own effort and both levers in its env block",
                    settingsFollowEffort && sentEnv != null && sentEnv.Count == 3 && (string)sentEnv["CLAUDE_CODE_EFFORT_LEVEL"] == "high" &&
                    (string)sentEnv["CLAUDE_CODE_DISABLE_ADVISOR_TOOL"] == "1" && (string)sentEnv["CLAUDE_CODE_DISABLE_TERMINAL_TITLE"] == "1");
                // Vacuous unless this process's own environment sets it; the dictionary check below is the one that proves
                // the removal whatever the environment holds.
                check("cli runner: no Claude Code model call carries CLAUDE_CODE_EFFORT_LEVEL, which outranks --effort",
                    sonnetCall != null && EnvironmentOf(sonnetCall, "CLAUDE_CODE_EFFORT_LEVEL") == null);
                var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["claude_code_effort_level"] = "xhigh", ["PATH"] = "p", ["ANTHROPIC_MODEL"] = "opus",
                };
                CodingAgentCli.ApplyModelCallEnvironment(environment);
                // Read with TryGetValue, so a lever that is missing fails THIS check by its label rather than throwing and
                // taking the rest of the group with it (the title-request mutation found that on 2026-10-09).
                check("cli runner: CLAUDE_CODE_EFFORT_LEVEL comes off a model call's child, and the two levers go on",
                    !environment.ContainsKey("CLAUDE_CODE_EFFORT_LEVEL") && ValueOf(environment, "CLAUDE_CODE_DISABLE_ADVISOR_TOOL") == "1" &&
                    ValueOf(environment, "CLAUDE_CODE_DISABLE_TERMINAL_TITLE") == "1");
                check("WITNESS cli runner: the rest of the user's environment stays on a model call's child",
                    ValueOf(environment, "PATH") == "p" && ValueOf(environment, "ANTHROPIC_MODEL") == "opus");

                Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "gpt-user-1", "high", null), CancellationToken.None));
                Wait(runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None));
                Wait(runner.RefreshDetailsAsync(CodingAgentKind.Codex, CancellationToken.None));
                Wait(runner.UpdateAsync(CodingAgentKind.Claude, CancellationToken.None));
                List<FakeCliCall> probes = fake.Calls.FindAll(delegate(FakeCliCall x) { return !x.IsModelCall; });
                bool clean = probes.Count >= 5;
                foreach (FakeCliCall probe in probes)
                    clean &= !HasArgument(probe, "--model") && !HasArgument(probe, "--effort") && !HasArgument(probe, "-m") &&
                             !HasArgument(probe, "model_reasoning_effort=") && !HasArgument(probe, "-c");
                check("cli runner: --version, auth status, login status, debug models and update never carry a model or an effort (" +
                      probes.Count + " probes)", clean &&
                    probes.Exists(delegate(FakeCliCall x) { return x.Is("debug", "models"); }) && probes.Exists(delegate(FakeCliCall x) { return x.Is("update"); }));
                check("cli runner: no version check, auth status, login status, catalog fetch or update carries a --settings (" +
                      probes.Count + " probes)", probes.Count >= 5 &&
                    !probes.Exists(delegate(FakeCliCall x) { return HasArgument(x, "--settings"); }));
                FakeCliCall codexCall = LastModelCall(fake, true);
                check("WITNESS cli runner: the model calls beside those probes carried theirs",
                    HasArgument(sonnetCall, "--model") && HasArgument(sonnetCall, "--effort") && HasArgument(sonnetCall, "--settings") &&
                    HasArgument(codexCall, "-m") && HasArgument(codexCall, "model_reasoning_effort=high"));

                // Validate tests what is on screen.
                fake.Clear();
                CliAnswer validated = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None, null, "opus", "medium"));
                FakeCliCall validateCall = LastModelCall(fake, false);
                string expectedOn = "on " + FakeCliProcess.AnsweredModelFor("opus") + " at medium effort";
                check("cli runner: Validate tests the model and effort on screen, applied or not, and names the model that answered",
                    validated.Ok && validateCall != null && validateCall.After("--model") == "opus" && validateCall.After("--effort") == "medium" &&
                    CodingAgentCliText.Describe(CodingAgentKind.Claude, validated, null).EndsWith(" s " + expectedOn + ".", StringComparison.Ordinal));
                check("cli runner: the Status row after Validate names the model that answered and the effort",
                    (runner.LastValidation(CodingAgentKind.Claude) ?? "").EndsWith(" s, " + expectedOn, StringComparison.Ordinal));
                CliAnswer codexValidated = Wait(runner.ValidateAsync(CodingAgentKind.Codex, CancellationToken.None, null, "gpt-user-1", "low"));
                check("cli runner: Validate on Codex names the model chosen on screen, which is the one it sent",
                    codexValidated.Ok && CodingAgentCliText.Describe(CodingAgentKind.Codex, codexValidated, null)
                        .EndsWith(" on gpt-user-1 at low effort.", StringComparison.Ordinal));
                // Round 2: the card's Status row is the most recent Validate of EITHER CLI, naming it, so a pending Codex
                // Validate never sits under an older Claude Code tick (the first on-screen walk showed both, two green ticks).
                string latest = runner.LatestValidation() ?? "(none)";
                check("cli runner: the card's Validate row is the most recent Validate of either CLI, and names that CLI: " + latest,
                    latest.StartsWith("✓ Codex: answered at ", StringComparison.Ordinal) && latest.EndsWith(" s, on gpt-user-1 at low effort", StringComparison.Ordinal) &&
                    (runner.LastValidation(CodingAgentKind.Claude) ?? "").StartsWith("✓ Claude Code: answered at ", StringComparison.Ordinal));
                int startsBefore = fake.Calls.Count;
                CliAnswer refusedValidate = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None, null, "opus", "max"));
                check("cli runner: Validate on a refused setting starts nothing, its version check included, and says why",
                    refusedValidate.Outcome == CliOutcome.ChoiceRefused && refusedValidate.Version.Length == 0 &&
                    fake.Calls.Count == startsBefore &&
                    (runner.LastValidation(CodingAgentKind.Claude) ?? "").StartsWith("✗ Claude Code: model or effort setting refused at ", StringComparison.Ordinal));
                check("WITNESS cli runner: ...and a later Validate of the other CLI takes the row's place",
                    (runner.LatestValidation() ?? "").StartsWith("✗ Claude Code: model or effort setting refused at ", StringComparison.Ordinal));

                // Round 2: a Validate reads the card's details of the CLI it tested when none are kept fresh, after its call
                // and before it answers, so the "signed in as" row of a CLI chosen on screen and not saved fills; a pane open
                // reads only the saved CLI's, and KeptDetails, which the other row reads, starts nothing.
                using (var other = new FakeCliScratch())
                {
                    var otherFake = new FakeCliProcess { Respond = FakeCliProcess.Answering("OK") };
                    CodingAgentCli fresh = other.NewRunner(otherFake, new List<string>());
                    CodingAgentCli.CliDetails before = fresh.KeptDetails(CodingAgentKind.Codex);
                    bool startedByPeek = SpinWait.SpinUntil(delegate { return otherFake.Calls.Count > 0 || fresh.IsBusy; }, TimeSpan.FromSeconds(1.5));
                    check("cli runner: the kept details of a CLI no one has read are none, and asking for them starts nothing",
                        before == null && !startedByPeek);
                    CliAnswer codexOnScreen = Wait(fresh.ValidateAsync(CodingAgentKind.Codex, CancellationToken.None, null, "", "low"));
                    CodingAgentCli.CliDetails afterValidate = fresh.KeptDetails(CodingAgentKind.Codex);
                    check("cli runner: a Validate reads the details of the CLI it tested, so its signed-in row fills, and nothing of it runs on after its answer",
                        codexOnScreen.Ok && afterValidate != null && afterValidate.SignedIn == "a ChatGPT account (Codex does not say which)" &&
                        !fresh.IsBusy && otherFake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("login", "status"); }));
                    otherFake.Clear();
                    Wait(fresh.ValidateAsync(CodingAgentKind.Codex, CancellationToken.None, null, "", "low"));
                    check("WITNESS cli runner: ...and a second Validate while they are fresh reads them no more",
                        !otherFake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("login", "status"); }) &&
                        ReferenceEquals(fresh.KeptDetails(CodingAgentKind.Codex), afterValidate));
                }
                // ...and none after a call its caller cancelled (the module shutting down): the read would run on the
                // cancelled token, both probes would come back empty, and that reading would be kept, the card then saying
                // Claude Code did not say whether it is signed in. The fake honours the token, as a real process start does.
                using (var stopping = new FakeCliScratch())
                using (var lifetime = new CancellationTokenSource())
                {
                    var stopFake = new FakeCliProcess();
                    stopFake.Respond = delegate(FakeCliCall call, CancellationToken token)
                    {
                        if (call.IsModelCall) lifetime.Cancel();
                        if (token.IsCancellationRequested) return Task.FromCanceled<CliProcessResult>(token);
                        return FakeCliProcess.Answering("OK")(call, token);
                    };
                    CodingAgentCli shuttingDown = stopping.NewRunner(stopFake, new List<string>());
                    CliAnswer cancelledValidate = Wait(shuttingDown.ValidateAsync(CodingAgentKind.Claude, lifetime.Token, null, "", "low"));
                    check("cli runner: a Validate whose call was cancelled reads no details, which would run on the cancelled token and keep a failed reading",
                        cancelledValidate.Outcome == CliOutcome.Cancelled && shuttingDown.KeptDetails(CodingAgentKind.Claude) == null &&
                        !stopFake.Calls.Exists(delegate(FakeCliCall x) { return !x.IsModelCall; }));
                }
            }
        }

        private static void CheckAnsweredModel(Action<string, bool> check)
        {
            // Asked for haiku: the init line echoes haiku, the assistant message says sonnet answered, and modelUsage
            // names Claude Code's side request first.
            CodingAgentCli.ClaudeStreamReading read = CodingAgentCli.ReadClaudeStream(
                FakeCliProcess.ClaudeStream("ok", false, "claude-sonnet-5-5", "claude-haiku-5-5"));
            check("cli runner: the model that answered is the assistant message's, not the init line's echo or modelUsage's first key",
                read.Found && read.Model == "claude-sonnet-5-5");
            CodingAgentCli.ClaudeStreamReading errored = CodingAgentCli.ReadClaudeStream(
                FakeCliProcess.ClaudeStream("There's an issue with the selected model (claude-nonexistent-9).", true, "<synthetic>", "claude-nonexistent-9"));
            check("cli runner: an errored call's <synthetic> model and its init echo are never taken for the model that answered",
                errored.Found && errored.IsError && errored.Model.Length == 0);
            string withFallback = "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5-5\"}\n" +
                "{\"type\":\"system\",\"subtype\":\"model_fallback\",\"original_model\":\"claude-opus-5-5\",\"fallback_model\":\"claude-sonnet-5-5\"}\n" +
                FakeCliProcess.ClaudeStream("ok", false, "claude-sonnet-5-5", "claude-opus-5-5");
            CodingAgentCli.ClaudeStreamReading fellBack = CodingAgentCli.ReadClaudeStream(withFallback);
            check("cli runner: Claude Code's model_fallback event is recorded, the model asked of the server and the one that answered",
                fellBack.FallbackFrom == "claude-opus-5-5" && fellBack.FallbackTo == "claude-sonnet-5-5" && fellBack.Model == "claude-sonnet-5-5");

            using (var scratch = new FakeCliScratch())
            {
                var log = new List<string>();
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("OK") };
                CodingAgentCli runner = scratch.NewRunner(fake, log);
                check("WITNESS cli runner: before any call answers, no model is kept as the last one that answered",
                    runner.LastAnswered(CodingAgentKind.Claude) == null && runner.LastAnswered(CodingAgentKind.Codex) == null);

                CliAnswer same = Wait(runner.AskAsync(Chosen(CodingAgentKind.Claude, "haiku", "low", null), CancellationToken.None));
                check("WITNESS cli runner: asked for haiku and answered on haiku's family, the answer says no more than that",
                    same.Ok && same.Model == FakeCliProcess.AnsweredModelFor("haiku") && !same.AnsweredOtherModel &&
                    !CodingAgentCliText.Describe(CodingAgentKind.Claude, same, null).Contains("asked for"));

                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    if (!call.IsModelCall || call.IsCodex) return FakeCliProcess.Answering("OK")(call, token);
                    return Task.FromResult(FakeCliProcess.Result(0, withFallback, ""));
                };
                CliAnswer other = Wait(runner.AskAsync(Chosen(CodingAgentKind.Claude, "haiku", "low", null), CancellationToken.None));
                string otherWords = CodingAgentCliText.Describe(CodingAgentKind.Claude, other, null);
                check("cli runner: asked for haiku and answered on another model, the answer is still a success and says both",
                    other.Ok && other.RequestedModel == "haiku" && other.Model == "claude-sonnet-5-5" && other.AnsweredOtherModel &&
                    otherWords.Contains(" s on claude-sonnet-5-5 (asked for haiku; Claude Code fell back to it from claude-opus-5-5) at low effort."));
                check("cli runner: the log line names the effort, the alias asked for, the fallback and the model that answered",
                    log.Exists(delegate(string l)
                    {
                        return l.StartsWith("cli: claude remark ok ", StringComparison.Ordinal) &&
                               l.EndsWith(" effort=low asked=haiku fallbackFrom=claude-opus-5-5 model=claude-sonnet-5-5", StringComparison.Ordinal);
                    }));
                CliAnswer kept = runner.LastAnswered(CodingAgentKind.Claude);
                check("cli runner: the last model that answered is kept per CLI for the Status rows, with what was asked for and the effort",
                    kept != null && kept.Model == "claude-sonnet-5-5" && kept.RequestedModel == "haiku" && kept.Effort == "low" &&
                    kept.Text.Length == 0 && CodingAgentCliText.RanOn(kept).StartsWith("on claude-sonnet-5-5 (asked for haiku", StringComparison.Ordinal) &&
                    runner.LastAnswered(CodingAgentKind.Codex) == null);
                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    return call.IsModelCall && !call.IsCodex
                        ? Task.FromResult(FakeCliProcess.Result(1, FakeCliProcess.ClaudeStream("Not logged in · Please run /login", true), ""))
                        : FakeCliProcess.Answering("OK")(call, token);
                };
                Wait(runner.AskAsync(Chosen(CodingAgentKind.Claude, "opus", "low", null), CancellationToken.None));
                check("WITNESS cli runner: a call that did not answer leaves the last answer as it was",
                    runner.LastAnswered(CodingAgentKind.Claude) != null && runner.LastAnswered(CodingAgentKind.Claude).Model == "claude-sonnet-5-5");
                // Round 2: the model that last answered names the last REAL call, never a Validate: after an unapplied
                // Validate of gpt-5.6-luna the first on-screen walk's Status card read "its automatic pick at low effort,
                // last answered on gpt-5.6-luna". The Validate's answer is its own row's.
                fake.Respond = FakeCliProcess.Answering("OK");
                CliAnswer tested = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None, null, "opus", "medium"));
                CliAnswer afterTest = runner.LastAnswered(CodingAgentKind.Claude);
                check("cli runner: a Validate is never kept as the model that last answered, which stays the last real call's",
                    tested.Ok && tested.Model == FakeCliProcess.AnsweredModelFor("opus") && afterTest != null &&
                    afterTest.Model == "claude-sonnet-5-5" && afterTest.RequestedModel == "haiku" &&
                    (runner.LastValidation(CodingAgentKind.Claude) ?? "").EndsWith(" on " + FakeCliProcess.AnsweredModelFor("opus") + " at medium effort", StringComparison.Ordinal));
                Wait(runner.AskAsync(Chosen(CodingAgentKind.Claude, "opus", "medium", null), CancellationToken.None));
                check("WITNESS cli runner: ...while the real call after it is kept",
                    runner.LastAnswered(CodingAgentKind.Claude) != null &&
                    runner.LastAnswered(CodingAgentKind.Claude).Model == FakeCliProcess.AnsweredModelFor("opus"));
                string error;
                runner.TrySetClaudeToken("sk-ant-oat01-SELFTEST-model-effort-0123456789abcdef", out error);
                check("cli runner: a new sign-in token forgets the model that last answered, which may be another organisation's",
                    runner.LastAnswered(CodingAgentKind.Claude) == null);
                CliAnswer typed = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None,
                    "sk-ant-oat01-SELFTEST-model-effort-typed-0123456789", "sonnet", "low"));
                // Re-pointed in round 2: until then a Validate that saved the typed token kept its own answer as the model
                // that last answered, which the save had forgotten; a Validate is never that now, so the save leaves none.
                check("cli runner: a Validate that saves the typed token leaves no model as the last that answered, the old sign-in's forgotten and its own a test",
                    typed.Ok && typed.TypedTokenSaved && runner.LastAnswered(CodingAgentKind.Claude) == null &&
                    (runner.LastValidation(CodingAgentKind.Claude) ?? "").StartsWith("✓ Claude Code: answered at ", StringComparison.Ordinal));

                // F5, F12: an Ok stream whose assistant line names no model the runner takes, haiku asked for; never said
                // as if haiku had answered. With a model_fallback line and still no usable assistant model, the model
                // fallen back to is the one that answered.
                string unnamed = FakeCliProcess.ClaudeStream("OK", false, "claude[not-a-slug]", "haiku");
                string unnamedFallback =
                    "{\"type\":\"system\",\"subtype\":\"model_fallback\",\"original_model\":\"claude-opus-5-5\",\"fallback_model\":\"claude-sonnet-5-5\"}\n" +
                    FakeCliProcess.ClaudeStream("OK", false, "claude[not-a-slug]", "claude-opus-5-5");
                string stream = unnamed;
                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    if (!call.IsModelCall || call.IsCodex) return FakeCliProcess.Answering("OK")(call, token);
                    return Task.FromResult(FakeCliProcess.Result(0, stream, ""));
                };
                CliAnswer noModel = Wait(runner.AskAsync(Chosen(CodingAgentKind.Claude, "haiku", "low", null), CancellationToken.None));
                string noModelWords = CodingAgentCliText.Describe(CodingAgentKind.Claude, noModel, null);
                check("cli runner: a stream that names no model is never said as if the alias asked for had answered",
                    noModel.Ok && noModel.Model.Length == 0 && noModel.RequestedModel == "haiku" &&
                    CodingAgentCliText.RanOn(noModel) == "on a model it did not name (asked for haiku) at low effort" &&
                    noModelWords.EndsWith(" s on a model it did not name (asked for haiku) at low effort.", StringComparison.Ordinal) &&
                    !noModelWords.Contains("on haiku"));
                stream = unnamedFallback;
                CliAnswer fallbackOnly = Wait(runner.AskAsync(Chosen(CodingAgentKind.Claude, "haiku", "low", null), CancellationToken.None));
                check("cli runner: with no model named but a fallback reported, the model fallen back to is the one said to have answered",
                    fallbackOnly.Ok && fallbackOnly.Model.Length == 0 && fallbackOnly.AnsweredModel == "claude-sonnet-5-5" &&
                    fallbackOnly.AnsweredOtherModel && CodingAgentCliText.RanOn(fallbackOnly) ==
                        "on claude-sonnet-5-5 (asked for haiku; Claude Code fell back to it from claude-opus-5-5) at low effort");
                var elsewhere = new CliAnswer
                {
                    Outcome = CliOutcome.Ok, Model = "claude-haiku-selftest-1", RequestedModel = "haiku", Effort = "low",
                    FallbackFrom = "claude-opus-5-5", FallbackTo = "claude-sonnet-5-5",
                };
                check("cli runner: a fallback that names another model than the one that answered is said with both its ends",
                    CodingAgentCliText.RanOn(elsewhere) ==
                        "on claude-haiku-selftest-1 (Claude Code fell back from claude-opus-5-5 to claude-sonnet-5-5) at low effort");
                // F10: the Status rows' short form says the model and every note, without the effort. Pinned answer by answer
                // as text (round 2): the loop this replaced compared RanOn with RanOnShort plus the effort, which holds by
                // construction (RanOn is RanOnShort and then the effort), so it could not fail on any note dropped from both.
                string shortNoModel = CodingAgentCliText.RanOnShort(noModel);
                string shortFallback = CodingAgentCliText.RanOnShort(fallbackOnly);
                string shortElsewhere = CodingAgentCliText.RanOnShort(elsewhere);
                string shortOther = CodingAgentCliText.RanOnShort(other);
                check("cli runner: the Status rows' short form names the model and every note, without the effort: " + shortFallback,
                    shortNoModel == "on a model it did not name (asked for haiku)" &&
                    shortFallback == "on claude-sonnet-5-5 (asked for haiku; Claude Code fell back to it from claude-opus-5-5)" &&
                    shortElsewhere == "on claude-haiku-selftest-1 (Claude Code fell back from claude-opus-5-5 to claude-sonnet-5-5)" &&
                    shortOther == "on claude-sonnet-5-5 (asked for haiku; Claude Code fell back to it from claude-opus-5-5)");
                var nothingKnown = new CliAnswer { Outcome = CliOutcome.Ok, Effort = "low" };
                check("WITNESS cli runner: with no model asked for and none named, only the effort is said, and the short form says nothing",
                    CodingAgentCliText.RanOn(nothingKnown) == "at low effort" && CodingAgentCliText.RanOnShort(nothingKnown).Length == 0);

                // F10, F13: both modules' rows say what last answered through LastAnsweredOn, beside the SAVED choice. A call
                // that asked for the saved choice is said as RanOn (the CLI row) or RanOnShort (the Status row); one that
                // asked for another says what it asked for, in the parenthesis the other notes share, its effort included.
                check("WITNESS cli runner: an answer to a call that asked for the saved choice is said as RanOn in the CLI row and RanOnShort in the Status row",
                    CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Claude, other, "haiku", "low", true) == CodingAgentCliText.RanOn(other) &&
                    CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Claude, other, "haiku", "low", false) == CodingAgentCliText.RanOnShort(other) &&
                    CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Claude, other, " haiku ", "", false) == CodingAgentCliText.RanOnShort(other) &&
                    CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Claude, null, "haiku", "low", true).Length == 0);
                string otherModel = CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Claude, fallbackOnly, "opus", "low", false);
                check("cli runner: an answer to a call that asked for another model than the saved one says what that call asked for, in one parenthesis: " +
                      otherModel, otherModel == "on claude-sonnet-5-5 (that call asked for haiku at low effort; Claude Code fell back to it from claude-opus-5-5)" &&
                    CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Claude, fallbackOnly, "opus", "low", true) == otherModel);
                check("cli runner: ...and one that asked for another effort says so too",
                    CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Claude, other, "haiku", "medium", false) ==
                        "on claude-sonnet-5-5 (that call asked for haiku at low effort; Claude Code fell back to it from claude-opus-5-5)");
                check("cli runner: ...a model the stream did not name is a model it did not name, and a call that asked for none asked for Claude Code's default or the automatic pick",
                    CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Claude, noModel, "sonnet", "low", false) ==
                        "on a model it did not name (that call asked for haiku at low effort)" &&
                    CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Claude, new CliAnswer { Outcome = CliOutcome.Ok, Model = "claude-selftest-x", Effort = "low" },
                        "opus", "low", false) == "on claude-selftest-x (that call asked for Claude Code's default at low effort)" &&
                    CodingAgentCliText.LastAnsweredOn(CodingAgentKind.Codex, new CliAnswer { Outcome = CliOutcome.Ok, Model = "text-only-low", Effort = "low" },
                        "vision-second", "low", true) == "on text-only-low (that call asked for the automatic pick at low effort)");
            }
        }

        // ---- text shown from outside (review finding F3) ----

        private static void CheckDisplayable(Action<string, bool> check)
        {
            string hostile = "Vi" + (char)0x202E + "sion" + (char)0x2028 + "Later" + (char)0x85 + "x" + (char)0x07 + "y" +
                             (char)0x2066 + "z" + (char)0x09 + "w" + (char)0x2029 + "v";
            check("cli runner: text from outside is shown with no control or bidi character, and every kind of line break as a space",
                CodingAgentCli.OneLine(hostile) == "Vision Later xyz w v");
            string ordinary = "Caf" + (char)0xE9 + " " + (char)0xDC + "n" + (char)0xEF + " " + (char)0xB7 + " 5 " + (char)0x2713 + " (" + (char)0x3A9 + ")";
            check("WITNESS cli runner: ordinary text, accents and symbols included, is shown as it came",
                CodingAgentCli.OneLine(ordinary) == ordinary && CodingAgentCli.Displayable(ordinary) == ordinary);
            string catalog = new JsonObject
            {
                ["models"] = new JsonArray(new JsonObject
                {
                    ["slug"] = "bidi-name", ["display_name"] = "Bidi" + (char)0x202E + "Name" + (char)0x2028 + "Two",
                    ["priority"] = 1, ["visibility"] = "list", ["input_modalities"] = new JsonArray("text"),
                }),
            }.ToJsonString();
            List<CodingAgentCli.CodexModelEntry> listed = CodingAgentCli.ListModels(catalog);
            check("cli runner: a catalog's display name reaches the pane with no bidi control or line break in it",
                listed.Count == 1 && listed[0].DisplayName == "BidiName Two");
            string model, effort;
            string problem = CodingAgentCli.CheckChoice(CodingAgentKind.Claude, "op" + (char)0x202E + "us" + (char)0x2028 + "x", "low",
                out model, out effort);
            check("cli runner: a refused settings value is quoted back with no bidi control or line break in it",
                problem != null && problem.StartsWith("\"opus x\" is not a model this module runs Claude Code on", StringComparison.Ordinal));
        }

        // ---- a sign-in change while a call runs (review finding F6) ----

        private const string SignInA = "sk-ant-oat01-SELFTEST-sign-in-a-0123456789abcdef";
        private const string SignInB = "sk-ant-oat01-SELFTEST-sign-in-b-0123456789abcdef";

        private static void CheckSignInChange(Action<string, bool> check)
        {
            using (var scratch = new FakeCliScratch())
            {
                var hold = new TaskCompletionSource<CliProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool holdModel = false, holdStatus = false;
                var fake = new FakeCliProcess();
                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    if ((holdModel && call.IsModelCall && !call.IsCodex) || (holdStatus && call.Is("auth", "status"))) return hold.Task;
                    return FakeCliProcess.Answering("OK")(call, token);
                };
                CodingAgentCli runner = scratch.NewRunner(fake, new List<string>());
                string error;
                runner.TrySetClaudeToken(SignInA, out error);

                // A remark still running on token A when Remove token is pressed.
                holdModel = true;
                Task<CliAnswer> remark = runner.AskAsync(Chosen(CodingAgentKind.Claude, "haiku", "low", null), CancellationToken.None);
                SpinWait.SpinUntil(delegate { return fake.Calls.Exists(delegate(FakeCliCall x) { return x.IsModelCall; }); }, 5000);
                string removed = runner.RemoveClaudeToken();
                hold.SetResult(FakeCliProcess.Result(0, FakeCliProcess.ClaudeStream("OK", false, FakeCliProcess.AnsweredModelFor("haiku"), "haiku"), ""));
                CliAnswer late = Wait(remark);
                check("cli runner: a call that answers after Remove token was pressed records nothing as the last answer, which was the old sign-in's",
                    late.Ok && removed.StartsWith("✓ Removed.", StringComparison.Ordinal) && runner.LastAnswered(CodingAgentKind.Claude) == null);

                // A Validate still running on token A when token B is applied.
                runner.TrySetClaudeToken(SignInA, out error);
                hold = new TaskCompletionSource<CliProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                fake.Clear();
                Task<CliAnswer> validating = runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None, null, "sonnet", "low");
                SpinWait.SpinUntil(delegate { return fake.Calls.Exists(delegate(FakeCliCall x) { return x.IsModelCall; }); }, 5000);
                runner.TrySetClaudeToken(SignInB, out error);
                hold.SetResult(FakeCliProcess.Result(0, FakeCliProcess.ClaudeStream("OK", false, FakeCliProcess.AnsweredModelFor("sonnet"), "sonnet"), ""));
                CliAnswer validated = Wait(validating);
                check("cli runner: a Validate that answers after another token was applied records neither its Status line nor the model that answered",
                    validated.Ok && runner.LastValidation(CodingAgentKind.Claude) == null && runner.LastAnswered(CodingAgentKind.Claude) == null);

                // The card's details still being read on token B when Remove token is pressed.
                holdModel = false;
                holdStatus = true;
                hold = new TaskCompletionSource<CliProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                fake.Clear();
                Task<CodingAgentCli.CliDetails> reading = runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None);
                SpinWait.SpinUntil(delegate { return fake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("auth", "status"); }); }, 5000);
                runner.RemoveClaudeToken();
                hold.SetResult(FakeCliProcess.Result(0, "{\"loggedIn\":true,\"authMethod\":\"oauth_token\"}", ""));
                CodingAgentCli.CliDetails stale = Wait(reading);
                check("cli runner: the card's details read on the old sign-in are not kept once the token changed",
                    stale != null && stale.Installed && runner.KeptDetailsForDiagnostics(CodingAgentKind.Claude) == null);

                holdStatus = false;
                CliAnswer calm = Wait(runner.AskAsync(Chosen(CodingAgentKind.Claude, "opus", "low", null), CancellationToken.None));
                CliAnswer calmValidate = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None, null, "haiku", "low"));
                CodingAgentCli.CliDetails calmDetails = Wait(runner.RefreshDetailsAsync(CodingAgentKind.Claude, CancellationToken.None));
                CliAnswer kept = runner.LastAnswered(CodingAgentKind.Claude);
                // The call's answer is the one kept, the Validate's being its own row's (round 2).
                check("WITNESS cli runner: with the sign-in unchanged, a call, a Validate and a details read are each kept",
                    calm.Ok && calmValidate.Ok && kept != null && kept.Model == FakeCliProcess.AnsweredModelFor("opus") &&
                    runner.LastValidation(CodingAgentKind.Claude) != null &&
                    ReferenceEquals(runner.KeptDetailsForDiagnostics(CodingAgentKind.Claude), calmDetails));

                // A token typed in the card that answers and cannot be saved (here one the token check refuses, which the
                // module's own check would have stopped first): its answer describes no sign-in the module holds.
                CliAnswer typedUnsaved = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None,
                    "sk-ant-oat01-SELFTEST has-a-space-0123456789", "sonnet", "low"));
                check("cli runner: a Validate on a typed token that answered and could not be saved leaves the last answer the saved sign-in's",
                    typedUnsaved.Ok && typedUnsaved.UsedUnsavedToken && !typedUnsaved.TypedTokenSaved &&
                    ReferenceEquals(runner.LastAnswered(CodingAgentKind.Claude), kept));

                // Round 2 (the fix pass's skeptic): the card's own read, through the single-flight gate, still running on the
                // old sign-in when Remove token is pressed. It is dropped, and the pane's rebuild found it in flight and
                // started none, so one more read has to follow on its own or the card says "Checking…" until reopened.
                runner.TrySetClaudeToken(SignInA, out error);
                hold = new TaskCompletionSource<CliProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                holdStatus = true;
                fake.Clear();
                runner.CachedDetails(CodingAgentKind.Claude);
                SpinWait.SpinUntil(delegate { return fake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("auth", "status"); }); }, 5000);
                runner.RemoveClaudeToken();
                bool rebuildFoundNone = runner.CachedDetails(CodingAgentKind.Claude) == null;
                holdStatus = false;
                hold.SetResult(FakeCliProcess.Result(0, "{\"loggedIn\":true,\"authMethod\":\"oauth_token\"}", ""));
                bool readAgain = SpinWait.SpinUntil(delegate { return runner.KeptDetailsForDiagnostics(CodingAgentKind.Claude) != null; }, 5000);
                Func<int> statusReads = delegate { return fake.Calls.FindAll(delegate(FakeCliCall x) { return x.Is("auth", "status"); }).Count; };
                check("cli runner: a details read dropped for a sign-in change is followed by a fresh one, though the pane's own read found it in flight (" +
                      statusReads() + " status reads)",
                    rebuildFoundNone && readAgain && statusReads() == 2 &&
                    runner.KeptDetailsForDiagnostics(CodingAgentKind.Claude).SignedIn == "someone@example.invalid (max)");
                // The same read with the sign-in left alone: one read, no second. Started once the read above has let go of
                // its flag, so the token saved here is not one that moved under it.
                SpinWait.SpinUntil(delegate { return !runner.DetailsReadInFlightForDiagnostics(CodingAgentKind.Claude); }, 5000);
                runner.TrySetClaudeToken(SignInB, out error);
                fake.Clear();
                runner.CachedDetails(CodingAgentKind.Claude);
                bool readOnce = SpinWait.SpinUntil(delegate { return runner.KeptDetailsForDiagnostics(CodingAgentKind.Claude) != null; }, 5000);
                bool readTwice = SpinWait.SpinUntil(delegate { return statusReads() > 1; }, TimeSpan.FromSeconds(1));
                check("WITNESS cli runner: ...and a read the sign-in did not change while it ran is not followed by another",
                    readOnce && !readTwice && statusReads() == 1);
            }
        }

        private static void CheckCodexChoice(Action<string, bool> check)
        {
            List<CodingAgentCli.CodexModelEntry> listed = CodingAgentCli.ListModels(FakeCliProcess.Catalog);
            string order = string.Join(",", listed.ConvertAll(delegate(CodingAgentCli.CodexModelEntry e) { return e.Slug; }));
            check("cli runner: Codex's model list is its catalog's listed models, lowest priority first: " + order,
                order == "text-only-low,vision-second,vision-later");
            CodingAgentCli.CodexModelEntry textOnly = listed.Count == 3 ? listed[0] : null;
            CodingAgentCli.CodexModelEntry second = listed.Count == 3 ? listed[1] : null;
            CodingAgentCli.CodexModelEntry later = listed.Count == 3 ? listed[2] : null;
            check("cli runner: each listed model carries its display name, the efforts its catalog lists and whether it takes images",
                textOnly != null && textOnly.DisplayName == "Text Only" && string.Join("|", textOnly.Efforts) == "low|medium" && !textOnly.TakesImages &&
                second.DisplayName == "Vision Second" && string.Join("|", second.Efforts) == "low|high" && second.TakesImages &&
                later.DisplayName == "Vision Later" && string.Join("|", later.Efforts) == "medium|high" && later.TakesImages);
            check("cli runner: the list never offers a hidden model, or a slug the runner would refuse as a user's choice",
                !order.Contains("hidden-first") && !order.Contains("-reads-as-an-option") && !order.Contains("Upper") &&
                CodingAgentCli.PickModel(FakeCliProcess.Catalog, false) == "text-only-low");

            // What the checks before a call read (review finding F2): every entry a user could name, any visibility, no cap.
            Dictionary<string, CodingAgentCli.CodexModelEntry> facts = CodingAgentCli.CatalogFacts(FakeCliProcess.Catalog, listed);
            check("cli runner: the checks know every catalog model a user could name, a hidden one included, and a listed one as the list has it",
                facts.Count == 5 && facts.ContainsKey("hidden-text-only") && !facts["hidden-text-only"].TakesImages &&
                string.Join("|", facts["hidden-text-only"].Efforts) == "medium" && facts.ContainsKey("hidden-first") &&
                !facts.ContainsKey("Org/Upper_Case") && !facts.ContainsKey("-reads-as-an-option") &&
                textOnly != null && ReferenceEquals(facts["text-only-low"], textOnly));
            var many = new JsonArray();
            int past = CodingAgentCli.MaximumListedModels + 6;
            for (int i = 0; i < past; i++)
                many.Add(new JsonObject
                {
                    ["slug"] = "many-" + i, ["priority"] = i, ["visibility"] = "list",
                    ["input_modalities"] = i == past - 1 ? new JsonArray("text") : new JsonArray("text", "image"),
                });
            string manyCatalog = new JsonObject { ["models"] = many }.ToJsonString();
            List<CodingAgentCli.CodexModelEntry> manyListed = CodingAgentCli.ListModels(manyCatalog);
            Dictionary<string, CodingAgentCli.CodexModelEntry> manyFacts = CodingAgentCli.CatalogFacts(manyCatalog, manyListed);
            string lastOne = "many-" + (past - 1);
            check("cli runner: the list stops at its cap and the checks still know the models past it: " + manyListed.Count + " listed, " +
                  manyFacts.Count + " known",
                manyListed.Count == CodingAgentCli.MaximumListedModels && manyFacts.Count == past &&
                manyFacts.ContainsKey(lastOne) && !manyFacts[lastOne].TakesImages);
            // F17's reading of a refusal: about the effort only with "reasoning" or "effort" in it and the effort standing alone.
            check("cli runner: a refusal is about the effort only when it says reasoning or effort and names the effort itself",
                CodingAgentCli.RefusalNamesEffort("Unsupported value: 'reasoning.effort' does not support 'low' with this model.", "low") &&
                !CodingAgentCli.RefusalNamesEffort("reasoning models are not allowed for this account", "low") &&
                !CodingAgentCli.RefusalNamesEffort("The requested model is not supported at low priority", "low") &&
                !CodingAgentCli.RefusalNamesEffort("Unsupported value: 'reasoning.effort' does not support 'low'", ""));

            using (var scratch = new FakeCliScratch())
            {
                var log = new List<string>();
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("ok") };
                CodingAgentCli runner = scratch.NewRunner(fake, log);
                CliAnswer automatic = Wait(runner.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                CliAnswer chosen = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "gpt-user-1", "medium", null), CancellationToken.None));
                FakeCliCall chosenCall = LastModelCall(fake, true);
                check("cli runner: a Codex model the user chose goes to Codex as -m in place of the pick, at the effort chosen",
                    automatic.Ok && automatic.RequestedModel.Length == 0 && automatic.Model == "text-only-low" &&
                    chosen.Ok && chosen.Model == "gpt-user-1" && chosen.RequestedModel == "gpt-user-1" && !chosen.AnsweredOtherModel &&
                    chosenCall != null && chosenCall.After("-m") == "gpt-user-1" && chosenCall.Arguments.Contains("model_reasoning_effort=medium"));
                CodingAgentCli.CliDetails details = Wait(runner.RefreshDetailsAsync(CodingAgentKind.Codex, CancellationToken.None));
                check("cli runner: the card's details carry the list, from the same catalog fetch as the pick",
                    details.CodexModels.Count == 3 && details.CodexModels[1].Slug == "vision-second" && runner.CatalogFetchesForDiagnostics == 1);

                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    if (call.Is("exec") && call.After("-m") == "gpt-user-1")
                        return Task.FromResult(FakeCliProcess.Result(1,
                            FakeCliProcess.CodexFailure("The requested model is not supported for this account."), ""));
                    return FakeCliProcess.Answering("ok")(call, token);
                };
                CliAnswer refused = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "gpt-user-1", "low", null), CancellationToken.None));
                string refusedWords = CodingAgentCliText.Describe(CodingAgentKind.Codex, refused, null);
                CliAnswer afterRefusal = Wait(runner.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                check("cli runner: a refused Codex model the user chose is said by its name, and the automatic pick is left alone",
                    refused.Outcome == CliOutcome.ModelRefused &&
                    refusedWords.StartsWith("✗ Codex refused the model gpt-user-1: The requested model is not supported", StringComparison.Ordinal) &&
                    refusedWords.EndsWith("Choose another model in the CLI card, then press Validate again.", StringComparison.Ordinal) &&
                    afterRefusal.Ok && afterRefusal.Model == "text-only-low" && runner.CatalogFetchesForDiagnostics == 1);

                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    return call.IsModelCall && !call.IsCodex
                        ? Task.FromResult(FakeCliProcess.Result(1, FakeCliProcess.ClaudeStream(
                            "There's an issue with the selected model (claude-nonexistent-9). It may not exist or you may not have access to it.", true), ""))
                        : FakeCliProcess.Answering("ok")(call, token);
                };
                // No model chosen, so only the Codex-only half of the forget rule keeps the pick.
                CliAnswer claudeRefused = Wait(runner.AskAsync(Remark(CodingAgentKind.Claude, "p", null), CancellationToken.None));
                Wait(runner.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                check("cli runner: a Claude Code model refusal leaves Codex's automatic pick alone",
                    claudeRefused.Outcome == CliOutcome.ModelRefused && runner.CatalogFetchesForDiagnostics == 1);

                // F8: an effort the model's own catalog entry does not list is refused before anything starts, naming both,
                // for a chosen slug and for the automatic pick (text-only-low lists low and medium).
                fake.Respond = FakeCliProcess.Answering("ok");
                fake.Clear();
                CliAnswer chosenPair = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "text-only-low", "high", null), CancellationToken.None));
                CliRequest automaticHigh = Remark(CodingAgentKind.Codex, "t", null);
                automaticHigh.Effort = "high";
                CliAnswer automaticPair = Wait(runner.AskAsync(automaticHigh, CancellationToken.None));
                check("cli runner: an effort a Codex model's catalog entry does not list is refused before the call starts, naming the model and the effort",
                    chosenPair.Outcome == CliOutcome.ChoiceRefused && automaticPair.Outcome == CliOutcome.ChoiceRefused &&
                    !fake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("exec"); }) &&
                    CodingAgentCliText.Describe(CodingAgentKind.Codex, chosenPair, null) ==
                        "✗ Codex was not started: the model text-only-low takes low or medium effort in Codex's own catalog, not high. Choose again in the CLI card and press Apply." &&
                    CodingAgentCliText.Describe(CodingAgentKind.Codex, automaticPair, null).StartsWith(
                        "✗ Codex was not started: Codex's automatic pick, text-only-low, takes low or medium effort in Codex's own catalog, not high.", StringComparison.Ordinal));
                CliAnswer listedPair = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "text-only-low", "medium", null), CancellationToken.None));
                CliAnswer unlistedPair = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "not-listed-9", "high", null), CancellationToken.None));
                CliAnswer noEfforts = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "hidden-first", "high", null), CancellationToken.None));
                check("WITNESS cli runner: an effort the entry lists, a slug the catalog does not hold and an entry that lists no efforts all run",
                    listedPair.Ok && unlistedPair.Ok && noEfforts.Ok);

                // F17: a refusal of the automatic pick whose words are about the effort keeps the pick, and says which row.
                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    if (call.Is("exec"))
                        return Task.FromResult(FakeCliProcess.Result(1, FakeCliProcess.CodexFailure(
                            "Unsupported value: 'reasoning.effort' does not support 'low' with this model. Supported values are: 'medium' and 'high'."), ""));
                    return FakeCliProcess.Answering("ok")(call, token);
                };
                int fetchesBefore = runner.CatalogFetchesForDiagnostics;
                CliAnswer effortRefused = Wait(runner.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                fake.Respond = FakeCliProcess.Answering("ok");
                CliAnswer afterEffortRefusal = Wait(runner.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                check("cli runner: a refusal of the automatic pick whose words are about the effort keeps the pick cached and points at the effort row",
                    effortRefused.Outcome == CliOutcome.ModelRefused && afterEffortRefusal.Ok && runner.CatalogFetchesForDiagnostics == fetchesBefore &&
                    CodingAgentCliText.Describe(CodingAgentKind.Codex, effortRefused, null).EndsWith(
                        "Its words are about the effort (low): choose another effort, or another model, in the CLI card, then press Validate again.", StringComparison.Ordinal));

                // The screenshot check on a model the user chose.
                fake.Respond = FakeCliProcess.Answering("ok");
                fake.Clear();
                CliAnswer blind = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "text-only-low", "low", SyntheticPng), CancellationToken.None));
                check("cli runner: a chosen Codex model its catalog says takes no images is sent no screenshot, and no call starts",
                    blind.Outcome == CliOutcome.ModelCannotSee && !fake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("exec"); }) &&
                    CodingAgentCliText.Describe(CodingAgentKind.Codex, blind, null).StartsWith(
                        "✗ Codex's model text-only-low takes no images (its own catalog says so), so the screenshot was not sent.", StringComparison.Ordinal));
                CliAnswer hiddenBlind = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "hidden-text-only", "medium", SyntheticPng), CancellationToken.None));
                check("cli runner: a chosen Codex model the catalog hides and says takes no images is sent no screenshot either",
                    hiddenBlind.Outcome == CliOutcome.ModelCannotSee && !fake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("exec"); }) &&
                    runner.CodexModelTakesImages("hidden-text-only") == false);
                CliAnswer sees = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "vision-later", "medium", SyntheticPng), CancellationToken.None));
                FakeCliCall seesCall = LastModelCall(fake, true);
                check("WITNESS cli runner: a chosen Codex model that takes images is sent the screenshot as chosen, not the vision pick",
                    sees.Ok && seesCall != null && seesCall.After("-m") == "vision-later" && seesCall.ImageAtCall != null);
                CliAnswer unknown = Wait(runner.AskAsync(Chosen(CodingAgentKind.Codex, "not-listed-9", "low", SyntheticPng), CancellationToken.None));
                FakeCliCall unknownCall = LastModelCall(fake, true);
                check("cli runner: a chosen Codex model the catalog does not list is sent the screenshot, since not knowing is not a no",
                    unknown.Ok && unknownCall != null && unknownCall.After("-m") == "not-listed-9" && unknownCall.ImageAtCall != null);
                check("cli runner: whether a Codex model takes images is answered from the cached list, without asking Codex",
                    runner.CodexModelTakesImages("vision-second") == true && runner.CodexModelTakesImages("text-only-low") == false &&
                    runner.CodexModelTakesImages("not-listed-9") == null && runner.CatalogFetchesForDiagnostics == 1);

                // F7: a cache read off another Codex binary (an update since) is not the installed Codex's word. File probes
                // alone answer that, so nothing is started; with the binary as it was, the same cache answers again.
                int startsBefore = fake.Calls.Count;
                CodingAgentCli.CliDetails keptBefore = runner.KeptDetailsForDiagnostics(CodingAgentKind.Codex);
                DateTime written = File.GetLastWriteTimeUtc(scratch.CodexExe);
                File.SetLastWriteTimeUtc(scratch.CodexExe, written.AddMinutes(5));
                bool unknownAfterUpdate = runner.CodexModelTakesImages("text-only-low") == null && runner.CachedCodexModels().Count == 0;
                File.SetLastWriteTimeUtc(scratch.CodexExe, written);
                Func<CliEnvironment> installed = runner.EnvironmentSource;
                runner.EnvironmentSource = delegate { return new CliEnvironment { PathValue = "", AppData = "", UserProfile = "" }; };
                bool unknownUninstalled = runner.CodexModelTakesImages("text-only-low") == null && runner.CachedCodexModels().Count == 0;
                runner.EnvironmentSource = installed;
                // Waited for, not counted at once (round 2): a read started on the pool (a Task.Run, as CachedDetails starts
                // one) records its first child only after this thread has moved on, so a count taken straight away passed
                // with such a start in flight. Any trace of one inside the wait is a start: a child recorded, a probe
                // running, the card's read in flight, or the details it keeps replaced (a read that found no Codex while the
                // environment above was swapped starts no child, and still replaces them).
                bool startedToFindOut = SpinWait.SpinUntil(delegate
                {
                    return fake.Calls.Count > startsBefore || runner.IsBusy || runner.DetailsReadInFlightForDiagnostics(CodingAgentKind.Codex) ||
                           !ReferenceEquals(runner.KeptDetailsForDiagnostics(CodingAgentKind.Codex), keptBefore);
                }, TimeSpan.FromSeconds(1.5));
                check("cli runner: after Codex's binary changes, or with no Codex found, the cached list and the image check read as unknown, and nothing is started to find out",
                    unknownAfterUpdate && unknownUninstalled && !startedToFindOut && fake.Calls.Count == startsBefore);
                check("WITNESS cli runner: with the binary as it was, the same cache answers again",
                    runner.CodexModelTakesImages("text-only-low") == false && runner.CachedCodexModels().Count == 3);

                var fresh = new FakeCliProcess { Respond = FakeCliProcess.Answering("ok") };
                CodingAgentCli reopened = scratch.NewRunner(fresh, log);
                check("cli runner: the list is saved beside the pick, so a new runner has it without asking Codex again",
                    reopened.CodexModelTakesImages("vision-later") == true &&
                    Wait(reopened.RefreshDetailsAsync(CodingAgentKind.Codex, CancellationToken.None)).CodexModels.Count == 3 &&
                    reopened.CatalogFetchesForDiagnostics == 0);
                check("cli runner: what the checks know is saved beside the list, so a new runner knows a hidden model takes no images",
                    reopened.CodexModelTakesImages("hidden-text-only") == false && reopened.CatalogFetchesForDiagnostics == 0);

                string cache = Path.Combine(scratch.Data, "cli", "codex-model-pick.json");
                string whole = File.ReadAllText(cache);
                JsonObject saved = JsonNode.Parse(whole) as JsonObject;
                saved.Remove("models");
                File.WriteAllText(cache, saved.ToJsonString());
                var older = new FakeCliProcess { Respond = FakeCliProcess.Answering("ok") };
                CodingAgentCli third = scratch.NewRunner(older, log);
                Wait(third.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                check("cli runner: a pick saved before the list was kept is asked for again once, so the dropdown gets its list",
                    third.CatalogFetchesForDiagnostics == 1 && third.CodexModelTakesImages("vision-second") == true);

                JsonObject withoutFacts = JsonNode.Parse(whole) as JsonObject;
                withoutFacts.Remove("catalog");
                File.WriteAllText(cache, withoutFacts.ToJsonString());
                var earlier = new FakeCliProcess { Respond = FakeCliProcess.Answering("ok") };
                CodingAgentCli fourth = scratch.NewRunner(earlier, log);
                Wait(fourth.AskAsync(Remark(CodingAgentKind.Codex, "t", null), CancellationToken.None));
                check("cli runner: a pick saved before every entry's facts were kept is asked for again once, so the checks know the hidden models",
                    fourth.CatalogFetchesForDiagnostics == 1 && fourth.CodexModelTakesImages("hidden-text-only") == false);
            }

            // Round 2 (the fix pass's skeptic): an automatic pick no user could have typed (IsUsableSlug takes it,
            // IsCodexModelName does not) has its catalog entry kept for the effort check too, so an effort the entry does not
            // list is refused before the call, as for a chosen slug; until then the check found no entry and sent it.
            string oddCatalog = new JsonObject
            {
                ["models"] = new JsonArray(
                    new JsonObject
                    {
                        ["slug"] = "Org/Pick_Low", ["display_name"] = "Odd Pick", ["priority"] = 1, ["visibility"] = "list",
                        ["input_modalities"] = new JsonArray("text", "image"),
                        ["supported_reasoning_levels"] = new JsonArray("medium", "high"),
                    },
                    new JsonObject
                    {
                        ["slug"] = "plain-later", ["display_name"] = "Plain Later", ["priority"] = 5, ["visibility"] = "list",
                        ["input_modalities"] = new JsonArray("text", "image"), ["supported_reasoning_levels"] = new JsonArray("low"),
                    }),
            }.ToJsonString();
            using (var oddScratch = new FakeCliScratch())
            {
                var oddFake = new FakeCliProcess();
                oddFake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    if (call.Is("debug", "models")) return Task.FromResult(FakeCliProcess.Result(0, oddCatalog, ""));
                    return FakeCliProcess.Answering("ok")(call, token);
                };
                CodingAgentCli odd = oddScratch.NewRunner(oddFake, new List<string>());
                CliRequest atLow = Remark(CodingAgentKind.Codex, "t", null);
                atLow.Effort = "low";
                CliAnswer refusedPick = Wait(odd.AskAsync(atLow, CancellationToken.None));
                check("cli runner: an automatic pick no user could type is held to its own catalog entry's efforts, and refused before the call",
                    refusedPick.Outcome == CliOutcome.ChoiceRefused && !oddFake.Calls.Exists(delegate(FakeCliCall x) { return x.Is("exec"); }) &&
                    CodingAgentCliText.Describe(CodingAgentKind.Codex, refusedPick, null).StartsWith(
                        "✗ Codex was not started: Codex's automatic pick, Org/Pick_Low, takes medium or high effort in Codex's own catalog, not low.",
                        StringComparison.Ordinal));
                CliRequest atMedium = Remark(CodingAgentKind.Codex, "t", null);
                atMedium.Effort = "medium";
                CliAnswer pickRuns = Wait(odd.AskAsync(atMedium, CancellationToken.None));
                FakeCliCall pickCall = LastModelCall(oddFake, true);
                var reread = new FakeCliProcess { Respond = FakeCliProcess.Answering("ok") };
                CodingAgentCli fromCache = oddScratch.NewRunner(reread, new List<string>());
                CliAnswer cachedRefusal = Wait(fromCache.AskAsync(atLow, CancellationToken.None));
                check("WITNESS cli runner: ...and runs at an effort it lists, and a new runner reading the saved pick holds it to the same entry",
                    pickRuns.Ok && pickCall != null && pickCall.After("-m") == "Org/Pick_Low" &&
                    cachedRefusal.Outcome == CliOutcome.ChoiceRefused && fromCache.CatalogFetchesForDiagnostics == 0);
            }
        }
    }
}
