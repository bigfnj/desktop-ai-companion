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
            foreach (string key in new[] { "CLAUDE_CODE_DISABLE_AUTO_MEMORY", "CODEX_MANAGED_BY_NPM", "CODEX_MANAGED_PACKAGE_ROOT" })
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

        /// <summary>Claude Code's stream for one turn: an init line, then the result line.</summary>
        internal static string ClaudeStream(string result, bool isError)
        {
            var init = new JsonObject { ["type"] = "system", ["subtype"] = "init", ["model"] = "fake-default" };
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
            };
            return init.ToJsonString() + "\n" + final.ToJsonString() + "\n";
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
        /// lowest-priority entry is hidden, and a listed one would read as an option.</summary>
        internal const string Catalog =
            "{\"models\":[" +
            "{\"slug\":\"vision-later\",\"priority\":7,\"visibility\":\"list\",\"input_modalities\":[\"text\",\"image\"]}," +
            "{\"slug\":\"hidden-first\",\"priority\":0,\"visibility\":\"hide\",\"input_modalities\":[\"text\",\"image\"]}," +
            "{\"slug\":\"-reads-as-an-option\",\"priority\":0,\"visibility\":\"list\",\"input_modalities\":[\"text\",\"image\"]}," +
            "{\"slug\":\"vision-second\",\"priority\":2,\"visibility\":\"list\",\"input_modalities\":[\"text\",\"image\"]}," +
            "{\"slug\":\"text-only-low\",\"priority\":1,\"visibility\":\"list\",\"input_modalities\":[\"text\"]}" +
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
                return Task.FromResult(call.IsCodex ? Result(0, CodexStream(answer), "") : Result(0, ClaudeStream(answer, false), ""));
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
            var expectedClaude = new List<string>
            {
                "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--no-session-persistence", "--tools", "", "--strict-mcp-config", "--disable-slash-commands",
                "--settings", CodingAgentCli.ClaudeSettingsJson, "--system-prompt", Persona,
            };
            check("cli runner: Claude Code's command line is exactly the lean flags the brief measured, persona last",
                string.Join("\u0001", claude) == string.Join("\u0001", expectedClaude));
            check("cli runner: Claude Code never runs with --model or --bare",
                !claude.Contains("--model") && !claude.Contains("--bare"));
            check("cli runner: Claude Code's settings exclude the user's CLAUDE.md and rules",
                CodingAgentCli.ClaudeSettingsJson == "{\"claudeMdExcludes\":[\"**/.claude/CLAUDE.md\",\"**/.claude/rules/**\"]}");

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

            List<string> codex = CodingAgentCli.BuildArguments(Remark(CodingAgentKind.Codex, "x", SyntheticPng),
                "vision-second", "C:\\work\\screen.png", "C:\\work\\instructions.md");
            var expectedCodex = new List<string>
            {
                "exec", "--skip-git-repo-check", "--ephemeral", "-s", "read-only", "--ignore-user-config", "--ignore-rules",
                "-c", "model_reasoning_effort=low", "-c", "include_permissions_instructions=false",
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
            using (var scratch = new FakeCliScratch())
            {
                var fake = new FakeCliProcess();
                fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                {
                    return call.IsModelCall ? Task.FromResult(result) : FakeCliProcess.Answering("ok")(call, token);
                };
                return Wait(scratch.NewRunner(fake, log).AskAsync(Remark(agent, "p", null), CancellationToken.None));
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
                    CodingAgentCli.DescribeClaudeAuthStatus("{\"loggedIn\":false}").StartsWith("✗ Not signed in. To sign in, run claude", StringComparison.Ordinal));
                check("cli runner: a signed-out Codex says how to sign in",
                    CodingAgentCli.DescribeCodexLoginStatus("Not logged in").StartsWith("✗ Not signed in. To sign in, run codex login", StringComparison.Ordinal));
                check("cli runner: the card's details stay in the cache it serves the pane from",
                    runner.CachedDetails(CodingAgentKind.Claude) == claude);

                CliAnswer validated = Wait(runner.ValidateAsync(CodingAgentKind.Claude, CancellationToken.None));
                string status = runner.LastValidation(CodingAgentKind.Claude);
                check("cli runner: Validate's result is kept for the card's Status row with its time and duration",
                    validated.Ok && status != null && status.StartsWith("✓ answered at ", StringComparison.Ordinal) && status.EndsWith(" s", StringComparison.Ordinal));
                check("WITNESS cli runner: a CLI not validated this session has no Status line yet",
                    runner.LastValidation(CodingAgentKind.Codex) == null);
                check("cli runner: no log line carries an account, a key or anything the CLI said",
                    log.Count > 0 && !log.Exists(delegate(string l) { return l.Contains("@") || l.Contains("sk-") || l.Contains("ChatGPT"); }));
            }
        }
    }
}
