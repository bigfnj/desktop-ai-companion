using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;   // ProtectedData: the saved sign-in token, sealed the way AI Brain seals its cloud key
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
//
// The model and the effort (lane feature/cli-model-effort, owner decision 2026-10-09, which reverses 1.3.0's "no model
// chooser"): each call names the model its module's settings chose and an explicit effort, because with neither the
// child ran on whatever the user's own setup resolved. Measured that day on Claude Code 2.1.293 (8 real calls by the
// coordinator): with no --model the child inherited the user's ANTHROPIC_MODEL=opus and ran claude-opus-5-5, and the
// user's ~/.claude/settings.json raised its effort to xhigh (--settings ADDS to the user's settings); one identical
// one-word call cost $0.0407 there against $0.0011 with --model haiku (Claude Code's list-price estimate; on a
// subscription it is usage-limit draw). The values are checked here, where the argument list is built (CheckChoice),
// and the model that ANSWERED is read back from the stream, so a pane can say which one ran.
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
        /// <summary>A sign-in token is saved in this module's folder and this Windows account cannot unseal it (it was
        /// saved by another user, or the folder came from another machine). Nothing is started: running on the CLI's own
        /// sign-in instead would put the call on an account the user did not choose.</summary>
        TokenUnreadable,
        /// <summary>What is saved as the sign-in token is not one (aibrain 1.3.2: a 1.3.1 install saved a web address
        /// there, which CheckClaudeToken then let through). Nothing is started on it.</summary>
        TokenNotAToken,
        /// <summary>The model or the effort the call names is not one the runner passes on (CheckChoice): it came from a
        /// settings file, which a user or another version can edit. Nothing is started, a probe included.</summary>
        ChoiceRefused,
        /// <summary>A screenshot turn on a Codex model the USER chose, which Codex's own catalog says takes no images.
        /// Nothing is started: the model is the user's, so it is not swapped for one that can see.</summary>
        ModelCannotSee,
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
        /// <summary>A sign-in token typed in the card and not applied yet, which Validate tests in place of the saved one
        /// (the CLI card's buttons act on what is on screen). Claude Code only; already checked by CheckClaudeToken.</summary>
        internal string UnsavedClaudeToken;
        /// <summary>The model the module's settings chose for this CLI (lane feature/cli-model-effort): a Claude Code alias
        /// (haiku, sonnet or opus) or a Codex slug. Empty chooses none: Claude Code then runs on its own default (what the
        /// user's terminal would run, their ANTHROPIC_MODEL and settings included) and Codex on the runner's automatic
        /// pick. Checked by CheckChoice before anything starts.</summary>
        internal string Model;
        /// <summary>The reasoning effort: low, medium or high. Every model call names one; empty is
        /// <see cref="CodingAgentCli.DefaultEffort"/>.</summary>
        internal string Effort;
        /// <summary>Validate's test call, set by ValidateAsync alone. Its answer is the card's Validate row's
        /// (LastValidation) and is never kept as the model that last answered, which names the last real call: a
        /// remark, an audition sample or a summary (round 2 of lane feature/cli-model-effort, 2026-10-09). The first
        /// on-screen walk read "its automatic pick at low effort, last answered on gpt-5.6-luna" after an unapplied
        /// Validate of Luna, as if Luna were the automatic pick.</summary>
        internal bool Validation;
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
        /// <summary>The call ran (or was refused) on the sign-in token saved in this module, not the CLI's own sign-in, so
        /// a refused sign-in is that token's and the pane says to replace or remove it.</summary>
        internal bool UsedSavedToken;
        /// <summary>The token was one typed in the card and not applied, which Validate tests in place of the saved one;
        /// its answer then says so, because a tick there is easily read as the saved token working.</summary>
        internal bool UsedUnsavedToken;
        /// <summary>Validate saved the typed token because it answered (1.3.3); false when it did not answer, or the save
        /// failed, whose reason is <see cref="TypedTokenSaveError"/>.</summary>
        internal bool TypedTokenSaved;
        internal string TypedTokenSaveError = "";
        /// <summary>The model the call asked for: Claude Code's alias, or the Codex slug the user chose; "" when it asked
        /// for none (Claude Code's default, Codex's automatic pick). <see cref="Model"/> is the one that ran: for Claude
        /// Code the model its stream says answered, for Codex the slug it was sent (its stream names none).</summary>
        internal string RequestedModel = "";
        /// <summary>The effort the call ran at.</summary>
        internal string Effort = "";
        /// <summary>Claude Code's own fallback, when its stream reported one (a system event, subtype model_fallback): the
        /// model asked of the server, and the one that answered instead.</summary>
        internal string FallbackFrom = "";
        internal string FallbackTo = "";
        /// <summary>Why a ChoiceRefused call was not started, in the pane's words. The value came from a settings file, so
        /// it is said on the pane and never logged.</summary>
        internal string ChoiceProblem = "";
        internal bool Ok { get { return Outcome == CliOutcome.Ok; } }

        /// <summary>The model that answered, as far as the stream says: the one its assistant messages named (Model), else
        /// the one Claude Code reported falling back to (FallbackTo); "" when it named neither (review finding F5). Never the
        /// alias asked for: that is what was asked, not what answered.</summary>
        internal string AnsweredModel { get { return Model.Length > 0 ? Model : FallbackTo; } }

        /// <summary>Claude Code answered on a model whose id does not name the family asked for (asked for haiku, answered
        /// on claude-sonnet-5-5). Not a failure: a fact Validate and the Status rows say, because a model the user chose to
        /// save usage on and did not get is worth knowing. Never true for Codex, whose Model is the slug it was sent.</summary>
        internal bool AnsweredOtherModel
        {
            get
            {
                return RequestedModel.Length > 0 && AnsweredModel.Length > 0 &&
                       AnsweredModel.IndexOf(RequestedModel, StringComparison.OrdinalIgnoreCase) < 0;
            }
        }
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

    /// <summary>The environment values the locator reads, behind a seam so a test hands it a scratch tree. A field a test
    /// leaves null is a place the locator does not look.</summary>
    internal sealed class CliEnvironment
    {
        internal string PathValue;
        internal string AppData;
        internal string UserProfile;
        /// <summary>The machine's and the user's PATH as saved NOW, which a terminal opened now would start with. This
        /// process's own PATH is the one it was started with, so an install made since is missing from it.</summary>
        internal string PersistedPathValue;
        internal string LocalAppData;
        internal string ProgramFiles;

        internal static CliEnvironment Current()
        {
            return new CliEnvironment
            {
                PathValue = Environment.GetEnvironmentVariable("PATH") ?? "",
                AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                PersistedPathValue = PersistedPath(),
                LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
        }

        /// <summary>The machine's PATH then the user's, in the order Windows joins them for a new process.</summary>
        private static string PersistedPath()
        {
            var parts = new List<string>();
            foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
            {
                try
                {
                    string value = Environment.GetEnvironmentVariable("PATH", target);
                    if (!string.IsNullOrEmpty(value)) parts.Add(value);
                }
                catch { }
            }
            return string.Join(Path.PathSeparator.ToString(), parts);
        }
    }

    /// <summary>Where a located CLI came from, which decides who updates it.</summary>
    internal enum CliSource
    {
        /// <summary>A binary found in a PATH folder that no installer below owns.</summary>
        Path = 0,
        Npm,
        /// <summary>Claude Code's own installer (%USERPROFILE%\.local\bin).</summary>
        NativeInstaller,
        /// <summary>A WinGet portable package. WinGet updates it, and the CLI's own update says it is up to date when it
        /// is not ("Claude is up to date!" on a WinGet install, the setup docs).</summary>
        WinGet,
        /// <summary>The copy of Claude Code inside VS Code's Claude Code extension, which VS Code replaces with the
        /// extension.</summary>
        VsCodeExtension,
    }

    /// <summary>A located CLI: the real binary, where it came from, and when npm installed it, the folder its package sits
    /// in, so an update's leftover staging folders can be found beside it.</summary>
    internal sealed class CliInstall
    {
        internal CodingAgentKind Agent;
        internal string Executable;
        internal CliSource Source;
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
    /// target second; then the PATH saved for the machine and the user NOW, because a long-running host keeps the PATH it
    /// was started with, so an install made since is on every new terminal's PATH but not on this one; then the places
    /// the installers put the binary whatever PATH says: npm's default prefix, Claude Code's native installer, WinGet's
    /// package folders, and last the copy inside VS Code's Claude Code extension.
    ///
    /// WinGet (bug report 2026-10-07, the owner's other workstation: "Claude Code is not installed" with Claude Code
    /// installed). Both CLIs ship as WinGet PORTABLE packages (manifests Anthropic.ClaudeCode 2.1.292, OpenAI.Codex
    /// 0.161.0): the binary is unpacked into %LOCALAPPDATA%\Microsoft\WinGet\Packages\&lt;id&gt;_&lt;source&gt; (machine scope:
    /// %ProgramFiles%\WinGet\Packages) and is reached either through a SYMBOLIC LINK in WinGet\Links, which the trust rules
    /// below rightly refuse, or, where WinGet may not make links (the maintainer's machine), through that package folder added to the
    /// saved user PATH, which a host started before the install never sees. The package folder is read directly, so
    /// neither shape needs a link followed. Codex's binary there keeps its release name (codex-x86_64-pc-windows-msvc.exe;
    /// WinGet's "codex" is the link's name).
    ///
    /// EVERY candidate, the shims included, goes through AiExecutablePolicy.ResolveConfigured, AI Brain's trust rules: a
    /// drive-qualified local path with no reparse point on the way, so a relative, UNC, mapped-network or linked location
    /// is neither probed nor run.
    /// </summary>
    internal static class CodingAgentLocator
    {
        private static readonly string[] ClaudePackageNames = { "claude-code" };
        private static readonly string[] CodexPackageNames = { "codex", "codex-win32-x64", "codex-win32-arm64" };
        internal const string ClaudeWinGetId = "Anthropic.ClaudeCode";
        internal const string CodexWinGetId = "OpenAI.Codex";

        internal static CliInstall Locate(CodingAgentKind agent, CliEnvironment environment)
        {
            if (agent == CodingAgentKind.None || environment == null) return null;
            string exeName = CodingAgents.ExecutableName(agent);
            var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pathValue in new[] { environment.PathValue, environment.PersistedPathValue })
            {
                CliInstall onPath = OnPath(agent, pathValue, exeName, searched);
                if (onPath != null) return Classified(onPath);
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
                if (native != null) return new CliInstall { Agent = agent, Executable = native, Source = CliSource.NativeInstaller };
            }
            CliInstall winget = FromWinGet(agent, environment);
            if (winget != null) return winget;
            return agent == CodingAgentKind.Claude ? FromVsCodeExtension(environment) : null;
        }

        /// <summary>The first install in one PATH value's folders, skipping a folder an earlier value already searched.</summary>
        private static CliInstall OnPath(CodingAgentKind agent, string pathValue, string exeName, HashSet<string> searched)
        {
            foreach (string raw in (pathValue ?? "").Split(Path.PathSeparator))
            {
                string directory;
                try { directory = Environment.ExpandEnvironmentVariables((raw ?? "").Trim().Trim('"')); }
                catch { continue; }
                if (directory.Length == 0 || !searched.Add(directory.TrimEnd('\\', '/'))) continue;
                CliInstall found = InDirectory(agent, directory, exeName);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>A binary found on PATH in a folder an installer owns is that installer's: WinGet's package folder is
        /// what WinGet puts on PATH where it cannot make a link.</summary>
        private static CliInstall Classified(CliInstall install)
        {
            if (install.Source != CliSource.Path) return install;
            string exe = install.Executable ?? "";
            if (exe.IndexOf(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase) >= 0) install.Source = CliSource.WinGet;
            else if (exe.IndexOf(@"\extensions\anthropic.claude-code-", StringComparison.OrdinalIgnoreCase) >= 0)
                install.Source = CliSource.VsCodeExtension;
            return install;
        }

        /// <summary>The CLI's WinGet portable package, user scope first, then machine scope, read from the package folder
        /// itself (never through WinGet's link). Null when neither holds it.</summary>
        internal static CliInstall FromWinGet(CodingAgentKind agent, CliEnvironment environment)
        {
            string id = agent == CodingAgentKind.Claude ? ClaudeWinGetId : CodexWinGetId;
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(environment.LocalAppData))
                roots.Add(Path.Combine(environment.LocalAppData, "Microsoft", "WinGet", "Packages"));
            if (!string.IsNullOrWhiteSpace(environment.ProgramFiles))
                roots.Add(Path.Combine(environment.ProgramFiles, "WinGet", "Packages"));
            foreach (string root in roots)
            {
                string[] packages;
                try { packages = Directory.GetDirectories(root, id + "_*"); }
                catch { continue; }
                Array.Sort(packages, StringComparer.OrdinalIgnoreCase);
                foreach (string package in packages)
                    foreach (string name in WinGetExecutableNames(agent))
                    {
                        string exe = AiExecutablePolicy.ResolveConfigured(Path.Combine(package, name), name);
                        if (exe != null) return new CliInstall { Agent = agent, Executable = exe, Source = CliSource.WinGet };
                    }
            }
            return null;
        }

        /// <summary>The binary's name inside the package: Claude Code's is claude.exe; Codex's keeps its release name, this
        /// machine's architecture first (an x64 binary also runs on Windows on Arm, an Arm one never on x64).</summary>
        private static IEnumerable<string> WinGetExecutableNames(CodingAgentKind agent)
        {
            if (agent == CodingAgentKind.Claude)
            {
                yield return "claude.exe";
                yield break;
            }
            if (RuntimeInformation.OSArchitecture == Architecture.Arm64) yield return "codex-aarch64-pc-windows-msvc.exe";
            yield return "codex-x86_64-pc-windows-msvc.exe";
            yield return "codex.exe";
        }

        /// <summary>
        /// The Claude Code binary VS Code's Claude Code extension carries (resources\native-binary\claude.exe in
        /// %USERPROFILE%\.vscode\extensions\anthropic.claude-code-&lt;version&gt;-&lt;platform&gt;; three versions sat side by side on
        /// the maintainer's machine on 2026-10-07, since VS Code removes a replaced one later). The newest one this machine can run, its
        /// own architecture first. Last in the order: it is the extension's copy, used only where no Claude Code of its own
        /// is installed, and it signs in with the same account, because it keeps its sign-in where the CLI does.
        /// </summary>
        internal static CliInstall FromVsCodeExtension(CliEnvironment environment)
        {
            if (string.IsNullOrWhiteSpace(environment.UserProfile)) return null;
            bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            string own = arm ? "win32-arm64" : "win32-x64";
            string best = null;
            Version bestVersion = null;
            bool bestOwn = false;
            foreach (string folder in new[] { ".vscode", ".vscode-insiders" })
            {
                string[] extensions;
                try { extensions = Directory.GetDirectories(Path.Combine(environment.UserProfile, folder, "extensions"), "anthropic.claude-code-*"); }
                catch { continue; }
                foreach (string extension in extensions)
                {
                    Version version;
                    string platform;
                    if (!TryParseExtensionFolder(Path.GetFileName(extension), out version, out platform)) continue;
                    bool isOwn = platform.Length == 0 || string.Equals(platform, own, StringComparison.OrdinalIgnoreCase);
                    bool runs = isOwn || (arm && string.Equals(platform, "win32-x64", StringComparison.OrdinalIgnoreCase));
                    if (!runs) continue;
                    string exe = AiExecutablePolicy.ResolveConfigured(
                        Path.Combine(extension, "resources", "native-binary", "claude.exe"), "claude.exe");
                    if (exe == null) continue;
                    if (best == null || (isOwn && !bestOwn) || (isOwn == bestOwn && version > bestVersion))
                    {
                        best = exe;
                        bestVersion = version;
                        bestOwn = isOwn;
                    }
                }
            }
            return best == null ? null : new CliInstall { Agent = CodingAgentKind.Claude, Executable = best, Source = CliSource.VsCodeExtension };
        }

        /// <summary>"anthropic.claude-code-2.1.292-win32-x64" is version 2.1.292 for win32-x64; no platform is universal.</summary>
        internal static bool TryParseExtensionFolder(string name, out Version version, out string platform)
        {
            version = null;
            platform = "";
            const string prefix = "anthropic.claude-code-";
            if (name == null || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = name.Substring(prefix.Length);
            int dash = rest.IndexOf('-');
            string number = dash < 0 ? rest : rest.Substring(0, dash);
            platform = dash < 0 ? "" : rest.Substring(dash + 1);
            return Version.TryParse(number, out version);
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
                        Agent = agent, Executable = exe, Source = CliSource.Npm, NpmScopeDirectory = scope, NpmPackageRoot = root,
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
                            Agent = agent, Executable = exe, Source = CliSource.Npm, NpmScopeDirectory = scope, NpmPackageRoot = root,
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
        // per screenshot question to 6,262 with the answer still right, and Codex's from 21,521 to 12,307. These are the
        // CLAUDE.md exclusions every model call's --settings carries (ClaudeModelCallSettings).
        internal static readonly string[] ClaudeMdExcludes = { "**/.claude/CLAUDE.md", "**/.claude/rules/**" };

        internal static readonly string[] CodexDisabledFeatures =
        {
            "apps", "plugins", "multi_agent", "skill_search", "tool_suggest", "browser_use", "browser_use_external",
            "computer_use", "image_generation", "goals", "hooks", "in_app_browser", "worktrees", "mentions_v2",
            "shell_tool", "unified_exec",
        };

        // The model and the effort a call may name (lane feature/cli-model-effort). Claude Code is named by ALIAS, never by
        // a full id: the user's two Claude organisations serve different catalogs (one has no claude-haiku-5-5, no
        // claude-sonnet-5-5 and no max effort, measured 2026-10-09), a saved sign-in token can land in either, and an alias
        // resolves inside whichever organisation serves the call. Codex is named by a slug its own catalog lists. The
        // efforts are the three every catalog seen serves; xhigh and max are not offered, because both modules' calls are
        // bounded (AI Brain's remark at 120 s by default and its audition samples at 20 to 90 s, Remembrance's summary at
        // ten minutes) and one organisation serves no max at all.
        internal static readonly string[] ClaudeModelAliases = { "haiku", "sonnet", "opus" };
        internal static readonly string[] Efforts = { "low", "medium", "high" };

        /// <summary>The effort of a call whose module names none: Codex's fixed effort before this lane, and the lightest,
        /// so no call inherits the user's own (an xhigh in their settings reached every companion call). Each module names
        /// its own default; this is the floor under a caller that names nothing.</summary>
        internal const string DefaultEffort = "low";

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
            // Whether the call itself was handed to the process runner. Every refusal before that point (the choice, the
            // install, the folder, the token, the screenshot check) ends with no model call started, Codex's version and
            // catalog probes aside, and its log line says so in place of an exit code it never had: the coordinator's live
            // check read "choice-refused exit=0 ms=0" as a CLI that ran and exited cleanly (2026-10-09, F18).
            bool started = false;
            // The sign-in this call runs on, taken before its token is read below (see _claudeSignInGeneration).
            int signIn = ClaudeSignInGeneration();
            try
            {
                string model, effort;
                string problem = CheckChoice(request.Agent, request.Model, request.Effort, out model, out effort);
                if (problem != null)
                {
                    // Refused before anything starts, a version check or a catalog fetch included: the value is not one
                    // the runner passes on, and nothing a probe could learn changes that.
                    answer.Outcome = CliOutcome.ChoiceRefused;
                    answer.ChoiceProblem = problem;
                    return answer;
                }
                answer.RequestedModel = model;
                answer.Effort = effort;
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
                string savedToken = null;
                if (request.Agent == CodingAgentKind.Claude && !string.IsNullOrEmpty(request.UnsavedClaudeToken))
                {
                    savedToken = request.UnsavedClaudeToken;
                    answer.UsedSavedToken = true;
                    answer.UsedUnsavedToken = true;
                }
                else if (request.Agent == CodingAgentKind.Claude)
                {
                    ClaudeTokenState tokenState = ReadClaudeToken(out savedToken);
                    answer.UsedSavedToken = tokenState != ClaudeTokenState.None;
                    if (tokenState == ClaudeTokenState.Unreadable)
                    {
                        answer.Outcome = CliOutcome.TokenUnreadable;
                        return answer;
                    }
                    if (tokenState == ClaudeTokenState.NotAToken)
                    {
                        answer.Outcome = CliOutcome.TokenNotAToken;
                        return answer;
                    }
                }

                string imagePath = null;
                string instructionsPath = null;
                string codexPick = null;
                if (request.Agent == CodingAgentKind.Codex)
                {
                    CodexPick pick = await CodexPickAsync(install, cancellationToken).ConfigureAwait(false);
                    answer.Version = pick.Version ?? "";
                    bool screenshot = request.ImagePng != null && request.ImagePng.Length > 0;
                    codexPick = screenshot ? pick.Vision : pick.Text;
                    answer.Model = (model.Length > 0 ? model : codexPick) ?? "";
                    // An effort the model's own catalog entry does not list is refused before the call starts, naming the
                    // model and the effort, for the user's slug and for the automatic pick alike (CodexEffortProblem).
                    string pairProblem = CodexEffortProblem(pick, model.Length > 0 ? model : codexPick, model.Length == 0, effort);
                    if (pairProblem != null)
                    {
                        answer.Outcome = CliOutcome.ChoiceRefused;
                        answer.ChoiceProblem = pairProblem;
                        return answer;
                    }
                    // A model the user chose that Codex's own catalog says takes no images gets no screenshot, and is not
                    // swapped for one that does: AI Brain's rule for a model the user chose where every call costs
                    // (AiModelPolicy.ChooseModel on a cloud primary, R-022: a backend that REPORTS a model blind is a hard
                    // gate, and no model the user did not choose is sent). Refused rather than sent as text, because the
                    // text a text turn needs is the OCR AI Brain reads BEFORE its capture, which this call does not have;
                    // AI Brain asks CodexModelTakesImages before its capture and reads the screen as text for such a model
                    // (AiBrain.SendsScreenshot, aibrain 1.5.0), so this refusal is the backstop for a catalog it had not
                    // seen cached yet. A slug the catalog does not hold is sent as chosen: not knowing is not a no (F102).
                    // The catalog's facts, not the dropdown's list: a hidden entry is still Codex's word on the slug (F2).
                    if (screenshot && model.Length > 0 && CodexModelTakesImages(pick, model) == false)
                    {
                        answer.Outcome = CliOutcome.ModelCannotSee;
                        return answer;
                    }
                    if (screenshot)
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

                ProcessStartInfo startInfo = NewStartInfo(install, working, savedToken);
                if (request.Agent == CodingAgentKind.Claude) ApplyModelCallEnvironment(startInfo.Environment);
                List<string> arguments = BuildArguments(request, codexPick, imagePath, instructionsPath);
                if (arguments == null)
                {
                    // Not reached past the check above; BuildArguments refuses on its own so that no caller can build a
                    // command line around a value it did not check.
                    answer.Outcome = CliOutcome.ChoiceRefused;
                    return answer;
                }
                foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
                string input = request.Agent == CodingAgentKind.Claude ? BuildClaudeInput(request) : (request.Prompt ?? "");

                TimeSpan timeout = request.Timeout > TimeSpan.Zero ? request.Timeout : DefaultCallTimeout;
                started = true;
                CliProcessResult result = await RunBoundedAsync(startInfo, input, timeout, cancellationToken, answer)
                    .ConfigureAwait(false);
                if (result == null) return answer;   // TimedOut or Cancelled, set by RunBoundedAsync
                Interpret(request.Agent, result, answer);
                // A model the server refused is not picked again from a stale cache: the next call asks the catalog. Only
                // the AUTOMATIC pick, and only Codex's: a model the user chose is theirs, so its refusal is said naming it
                // (Describe) and the pick, which it did not come from, is left alone; and Claude Code has no pick at all.
                // Nor when the refusal's words are about the EFFORT (F17): the model is not what was refused, and
                // forgetting the pick would fetch the catalog again before every call that then fails the same way.
                if (request.Agent == CodingAgentKind.Codex && answer.RequestedModel.Length == 0 &&
                    (answer.Outcome == CliOutcome.CliTooOld || answer.Outcome == CliOutcome.ModelRefused) &&
                    !RefusalNamesEffort(answer.Said, answer.Effort)) ForgetCodexPick();
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
                // A real call's answer only: Validate's test is the card's Validate row's, and never the model that last
                // answered (CliRequest.Validation). That also covers a token typed in the card and not saved, whose answer
                // describes no sign-in the module holds (F6's rule): only Validate sends one.
                if (answer.Ok && !request.Validation) RecordAnswered(request.Agent, answer, signIn);
                // Model ids and the effort word only: the values a refused choice held came from a settings file and stay
                // on the pane (ChoiceProblem), and what the CLI said never comes here.
                Log("cli: " + CodingAgents.IdOf(request.Agent) + " " + (request.Purpose ?? "call") + " " +
                    OutcomeWord(answer.Outcome) +
                    (started ? " exit=" + answer.ExitCode.ToString(CultureInfo.InvariantCulture) : " not-started") +
                    " ms=" + answer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    (answer.InputTokens >= 0 ? " inputTokens=" + answer.InputTokens.ToString(CultureInfo.InvariantCulture) : "") +
                    (answer.Effort.Length > 0 ? " effort=" + answer.Effort : "") +
                    (answer.RequestedModel.Length > 0 ? " asked=" + answer.RequestedModel : "") +
                    (answer.FallbackFrom.Length > 0 ? " fallbackFrom=" + answer.FallbackFrom : "") +
                    (answer.Model.Length > 0 ? " model=" + answer.Model : ""));
            }
        }

        /// <summary>Validate: one tiny call through the CLI, the same flags and path a real one takes. Its line is kept
        /// for the card's Status row (LastValidation, LatestValidation), and its answer is never kept as the model that last
        /// answered (CliRequest.Validation). <paramref name="unsavedClaudeToken"/>: a token typed in the card
        /// and not applied yet, tested in place of the saved one, and SAVED when it answers; null for the saved one.
        ///
        /// Why Validate saves it (1.3.3, the owner, 2026-10-07: "apply did not become clickable after validate was
        /// pressed"). Validate rebuilds the pane so the card shows what it learnt, and the host's rebuild never puts a
        /// typed secret back in its box (OptionsWindow's Secret editor shows a saved value only as a tooltip), so the
        /// typed token was gone from the screen and an Apply had nothing to save: 1.3.2's "press Apply to keep it" named
        /// a step that could not work. A token that just answered is the one the user meant, and Remove token already
        /// acts at once, so the token is the card's one value that does not wait for Apply. One that does not answer is
        /// not saved.
        ///
        /// <paramref name="model"/> and <paramref name="effort"/> (lane feature/cli-model-effort): the model and effort on
        /// screen, applied or not, the way the typed token is, so Validate tests what the card shows; null or empty for
        /// none. They are not saved by Validate: they wait for Apply like every other row. Its answer names the model that
        /// answered and the effort (CodingAgentCliText.Describe), because the model asked for is not always the one that
        /// runs.</summary>
        internal async Task<CliAnswer> ValidateAsync(CodingAgentKind agent, CancellationToken cancellationToken,
            string unsavedClaudeToken = null, string model = null, string effort = null)
        {
            // The sign-in this Validate runs on, taken before its call reads the token (F6): a token Apply or Remove token
            // while it runs means its Status line describes a sign-in no longer saved, so it is not recorded.
            int signIn = ClaudeSignInGeneration();
            CliAnswer answer = await AskAsync(new CliRequest
            {
                Agent = agent,
                SystemPrompt = "You check that a connection works. Reply with the single word OK and nothing else.",
                Prompt = "Reply with OK.",
                Timeout = ValidateTimeout,
                Purpose = "validate",
                UnsavedClaudeToken = unsavedClaudeToken,
                Model = model,
                Effort = effort,
                Validation = true,
            }, cancellationToken).ConfigureAwait(false);
            // Saved BEFORE the Status line is recorded: saving forgets the last Validate, which ran on the old sign-in,
            // and this one ran on the token now saved. It forgets the model that last answered too, and this answer does
            // not put one back: a Validate is never that (round 2).
            if (answer.Ok && answer.UsedUnsavedToken)
            {
                string saveError;
                int saved;
                answer.TypedTokenSaved = TrySetClaudeToken(unsavedClaudeToken, out saveError, out saved);
                answer.TypedTokenSaveError = answer.TypedTokenSaved ? "" : (saveError ?? "");
                // Recorded under the sign-in this save began: the answer ran on the token it saved, the deliberate keep.
                if (answer.TypedTokenSaved) signIn = saved;
            }
            // The card's details for this CLI, read now when none are kept fresh, and with them the version the answer names
            // (Codex's call learns it with its pick; Claude Code's does not). After the call, so they never delay it, and
            // after the save above, so they describe the token now saved; and awaited, so nothing of Validate's is still
            // running when its answer comes back (an Update CLI pressed next is not refused for it). Why Validate reads them
            // (round 2 of lane feature/cli-model-effort, 2026-10-09): each module's "Signed in as" row is one row per CLI,
            // live for the CLI chosen ON SCREEN, and a pane open reads only the SAVED CLI's, so that opening a pane starts no
            // CLI the user did not choose; the press on the CLI on screen is what reads that one's, and the rebuild the press
            // asks for shows them. Not after a call refused before it started (no private folder, busy, a refused setting),
            // and not after one the caller cancelled (the module shutting down): the read would only start its probes on
            // the cancelled token, and a read stopped that way keeps nothing (ReadDetailsAsync). After a CLI that is not
            // installed, yes (round 3, the review of round 2): ReadDetailsAsync answers that from the locator and starts
            // nothing, and without it the CLI's row kept saying to press the Validate just pressed.
            if (agent != CodingAgentKind.None &&
                answer.Outcome != CliOutcome.NoPrivateFolder && answer.Outcome != CliOutcome.Busy &&
                answer.Outcome != CliOutcome.ChoiceRefused && answer.Outcome != CliOutcome.Cancelled)
            {
                CliDetails details = await FreshDetailsAsync(agent, cancellationToken).ConfigureAwait(false);
                if (answer.Version.Length == 0 && details != null) answer.Version = details.Version;
            }
            if (agent != CodingAgentKind.None) RecordValidation(agent, answer, signIn, model, effort);
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
        /// The model and the effort a call would run with, checked where the argument list is built (both come from a
        /// settings file, which a user or another version can edit, and a value goes on a command line). Null when they
        /// may be passed on, with <paramref name="model"/> and <paramref name="effort"/> the values that will be (trimmed,
        /// the effort defaulted); otherwise the reason in the pane's words, both outs empty, and nothing is passed on.
        /// Claude Code: an alias of <see cref="ClaudeModelAliases"/>, exactly, or none. Codex: a slug of lowercase letters,
        /// digits, dots and dashes (IsCodexModelName), or none. The effort: one of <see cref="Efforts"/>, exactly.
        /// The property this holds is that only an allowlisted value reaches the argument list, checked on the very string
        /// that would be passed (the trimmed one). It says nothing about what a settings reader did first: a reader that
        /// cuts a long value could turn padded junk into an allowed word, which would then be passed, being allowed (review
        /// finding F4), and both modules' readers go through <see cref="SavedChoiceText"/>, whose cut cannot. Anything
        /// that looks the value up beside the runner (AI Brain's pre-capture image check) has to trim it the same way,
        /// which CodingAgentBackend does.
        /// </summary>
        internal static string CheckChoice(CodingAgentKind agent, string requestedModel, string requestedEffort,
            out string model, out string effort)
        {
            model = (requestedModel ?? "").Trim();
            effort = (requestedEffort ?? "").Trim();
            if (effort.Length == 0) effort = DefaultEffort;
            string problem = null;
            if (Array.IndexOf(Efforts, effort) < 0)
                problem = "\"" + Shown(effort) + "\" is not an effort this module runs it at (low, medium or high)";
            else if (agent == CodingAgentKind.Claude && model.Length > 0 && Array.IndexOf(ClaudeModelAliases, model) < 0)
                problem = "\"" + Shown(model) + "\" is not a model this module runs Claude Code on (haiku, sonnet, opus or Claude Code's default)";
            else if (agent == CodingAgentKind.Codex && model.Length > 0 && !IsCodexModelName(model))
                problem = "\"" + Shown(model) + "\" is not a Codex model name (lowercase letters, digits, dots and dashes)";
            if (problem == null) return null;
            model = "";
            effort = "";
            return problem;
        }

        /// <summary>A Codex model the user chose: `^[a-z0-9][a-z0-9.\-]{0,63}$`, so it can never read as an option, and is
        /// stricter than the automatic pick's IsUsableSlug, which takes what Codex's own catalog lists.</summary>
        internal static bool IsCodexModelName(string slug)
        {
            if (string.IsNullOrEmpty(slug) || slug.Length > 64) return false;
            for (int i = 0; i < slug.Length; i++)
            {
                char c = slug[i];
                bool letterOrDigit = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (!letterOrDigit && (i == 0 || (c != '.' && c != '-'))) return false;
            }
            return true;
        }

        /// <summary>The longest model or effort a module keeps from its settings file: longer than anything CheckChoice
        /// passes on (a Codex slug is at most 64 characters), so no value the runner would take is ever cut.</summary>
        internal const int MaximumSavedChoiceCharacters = 96;

        /// <summary>
        /// A model or effort as read from a module's settings file (AI Brain's AiSettings.Normalize and Remembrance's
        /// SummaryRoute.ReadChoice), before the module lowercases it; one reader for both, where two copies had drifted (AI
        /// Brain dropped control characters and Remembrance kept them, review finding F3). Control characters and unpaired
        /// surrogates are dropped as AI Brain's NormalizeString drops them, then the rest is made <see cref="Displayable"/>
        /// (the bidirectional controls dropped, a line or paragraph separator turned into a space) and trimmed, so the
        /// dropdown, the Status rows and a refusal all show the value that is checked and would be passed.
        ///
        /// A value still longer than <see cref="MaximumSavedChoiceCharacters"/> is cut and ENDS IN "…", so the runner
        /// refuses it (review finding F4): no alias, effort or slug holds that character, and a value that long is none of
        /// them anyway. Refused outright rather than trimmed again after the cut, because a cut and a trim could leave a
        /// prefix of padded junk ("opus", a hundred spaces, anything) as an allowed word, and a call would then run on a
        /// model nobody chose; the rule both modules keep for any value this version does not offer is to keep it, show it
        /// and have the runner refuse it by name, and an over-long value is one of those.
        /// </summary>
        internal static string SavedChoiceText(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            var kept = new StringBuilder(Math.Min(stored.Length, 4 * MaximumSavedChoiceCharacters));
            for (int i = 0; i < stored.Length; i++)
            {
                char c = stored[i];
                if (char.IsControl(c)) continue;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < stored.Length && char.IsLowSurrogate(stored[i + 1])) kept.Append(c).Append(stored[++i]);
                    continue;
                }
                if (char.IsLowSurrogate(c)) continue;
                kept.Append(c);
            }
            string value = Displayable(kept.ToString()).Trim();
            if (value.Length > MaximumSavedChoiceCharacters)
                value = UnicodeTextProgress.TruncateAtCodePointBoundary(value, MaximumSavedChoiceCharacters - 1) + "…";
            return value;
        }

        /// <summary>A settings value as a refusal quotes it: one line, at most forty characters.</summary>
        private static string Shown(string value)
        {
            string one = OneLine(value);
            return one.Length > 40 ? UnicodeTextProgress.TruncateAtCodePointBoundary(one, 40) + "…" : one;
        }

        /// <summary>
        /// The command line, without the prompt: the lean flags the brief measured, then the model and the effort. Pure, so
        /// the self-test pins it; null when <see cref="CheckChoice"/> refuses the request's model or effort, so no caller
        /// can build a command line around a value nobody checked.
        /// Claude Code: print mode, stream-json in and out (inline image input needs both, and stream-json output in print
        /// mode needs --verbose), no tools, no session written (AgentFlow watches ~/.claude/projects and would announce the
        /// companion's own call as a waiting session), no MCP servers or skills, a --settings built for the call (the user's
        /// CLAUDE.md and rules excluded, and the effort and the model-call levers in its env block: ClaudeModelCallSettings),
        /// --model with the alias chosen (none for Claude Code's default), --effort always, and the short system prompt in
        /// place of the coding agent's. No --bare: that mode wants an API key in place of the subscription login.
        /// The model and the effort reverse 1.3.0's "no --model: its default model" (owner decision 2026-10-09; the
        /// measurements are in the file header): the default was whatever the user's own setup resolved, which here was
        /// Opus at xhigh, and the effort is explicit on every call because the user's settings otherwise supply one.
        /// Codex: exec outside a repository, nothing persisted (--ephemeral, for the same AgentFlow reason, ~/.codex/sessions),
        /// a read-only sandbox, the user's config and rules not loaded, the effort chosen (low before this lane, fixed), the
        /// permission and environment preambles off, every optional feature off, the short instructions file in place of the
        /// base instructions, and -m with the model the user chose or else <paramref name="codexPick"/>, the automatic pick.
        /// `-i` takes a list, so an option has to follow it, and --json does; the prompt is "-", read from stdin.
        /// </summary>
        internal static List<string> BuildArguments(CliRequest request, string codexPick, string imagePath, string instructionsPath)
        {
            var args = new List<string>();
            if (request == null) return args;
            string model, effort;
            if (CheckChoice(request.Agent, request.Model, request.Effort, out model, out effort) != null) return null;
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
                // Built for THIS call from the effort CheckChoice let through, so the effort lever in it is the call's own.
                string settings = ClaudeModelCallSettings(effort);
                if (settings == null) return null;
                args.Add("--settings");
                args.Add(settings);
                if (model.Length > 0)
                {
                    args.Add("--model");
                    args.Add(model);
                }
                args.Add("--effort");
                args.Add(effort);
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
                args.Add("model_reasoning_effort=" + effort);
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
                string codexModel = model.Length > 0 ? model : codexPick;
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
            return message.ToJsonString(StdinJson) + "\n";
        }

        // The relaxed encoder: the default one escapes every '+' of the base64 image as + (valid JSON, and several
        // percent more bytes for a screenshot), a guard that exists for JSON embedded in HTML, which a pipe is not.
        private static readonly JsonSerializerOptions StdinJson = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

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
        /// than one per call. <paramref name="claudeToken"/> is the module's saved sign-in token, or null for the CLI's own
        /// sign-in; it reaches a Claude Code child only.</summary>
        private static ProcessStartInfo NewStartInfo(CliInstall install, string workingDirectory, string claudeToken)
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
                ApplyClaudeToken(startInfo.Environment, claudeToken);
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

        /// <summary>
        /// What a Claude Code MODEL call's child gets beside its start info (lane feature/cli-model-effort, measured
        /// 2026-10-09 on 2.1.293), and only a model call's: a version check, auth status and an update ask no model.
        /// CLAUDE_CODE_DISABLE_ADVISOR_TOOL=1, because every probe attached a server-side advisor tool with
        /// claude-opus-5-5 as the advisor even on --model haiku, which a long summary could spend the saving on;
        /// CLAUDE_CODE_DISABLE_TERMINAL_TITLE=1, which skips the extra Haiku request Claude Code makes for a terminal
        /// title (the env-vars reference); and CLAUDE_CODE_EFFORT_LEVEL taken off, because it outranks --effort, so a
        /// user who set it would otherwise decide every companion call's effort. The coordinator's eval checked the two
        /// levers live the same day, in this call's exact shape: the advisor lever removes the Opus advisor; the title
        /// request appears only when CLAUDE_AGENT_SDK_VERSION, an agent shell's variable, is set, which the companion's
        /// own environment lacks, and the lever stays because a companion started from such a shell passes it on.
        /// </summary>
        internal static void ApplyModelCallEnvironment(IDictionary<string, string> environment)
        {
            if (environment == null) return;
            environment["CLAUDE_CODE_DISABLE_ADVISOR_TOOL"] = "1";
            environment["CLAUDE_CODE_DISABLE_TERMINAL_TITLE"] = "1";
            environment.Remove("CLAUDE_CODE_EFFORT_LEVEL");
        }

        /// <summary>
        /// The --settings value of one Claude Code MODEL call: the CLAUDE.md exclusions (ClaudeMdExcludes) and an "env" block
        /// that sets CLAUDE_CODE_EFFORT_LEVEL to this call's effort and both levers above to "1". Null for an effort that is
        /// not one of <see cref="Efforts"/>, so nothing but an allowlisted word and fixed text reaches the command line, and
        /// it is built with the JSON serializer, never by joining strings.
        ///
        /// Why the env block (review finding F1, 2026-10-09): the child's own environment is not the last word. Claude Code
        /// writes every entry of a settings file's "env" block into its process environment, so a user whose
        /// ~/.claude/settings.json held "CLAUDE_CODE_EFFORT_LEVEL":"xhigh" got it back inside the child after
        /// ApplyModelCallEnvironment took it off, and it outranks --effort; a "CLAUDE_CODE_DISABLE_ADVISOR_TOOL":"0" there
        /// would bring the Opus advisor back the same way. Settings passed with --settings rank above the user's, the
        /// project's and the local settings files, below managed settings alone, and an env entry follows that order
        /// variable by variable, so the user's other env entries stay (Claude Code's settings and env-vars docs,
        /// "Settings precedence" and "Precedence", read 2026-10-09). The effort lever is SET to the call's effort rather
        /// than removed, because a settings file can set a variable and cannot remove one. The child-environment removal
        /// and --effort stay as well: a session that keeps the inherited value then still agrees. A managed settings file
        /// that sets one of these variables still wins, by design. A version check, auth status, debug models and an
        /// update carry no --settings at all.
        ///
        /// Measured live by the coordinator the same evening (2026-10-09, Claude Code 2.1.293; this lane makes no model
        /// call): with {"env":{"CLAUDE_CODE_EFFORT_LEVEL":"xhigh","CLAUDE_CODE_DISABLE_ADVISOR_TOOL":"0"}} planted in the
        /// child's working-directory .claude\settings.local.json, the build before this env block ran at an applied effort
        /// of xhigh with the Opus advisor attached, while Remembrance's header still said medium; this build ran at medium
        /// with no advisor (Claude Code's get_settings: flagSettings, this value, over localSettings). A plant in the
        /// project's .claude\settings.json was beaten the same way in probes that made no model call. The user's own
        /// ~/.claude/settings.json was not planted, by rule; it ranks below both of those files.
        /// </summary>
        internal static string ClaudeModelCallSettings(string effort)
        {
            if (Array.IndexOf(Efforts, effort) < 0) return null;
            var excludes = new JsonArray();
            foreach (string pattern in ClaudeMdExcludes) excludes.Add(pattern);
            var settings = new JsonObject
            {
                ["claudeMdExcludes"] = excludes,
                ["env"] = new JsonObject
                {
                    ["CLAUDE_CODE_EFFORT_LEVEL"] = effort,
                    ["CLAUDE_CODE_DISABLE_ADVISOR_TOOL"] = "1",
                    ["CLAUDE_CODE_DISABLE_TERMINAL_TITLE"] = "1",
                },
            };
            return settings.ToJsonString();
        }

        // ---- the saved sign-in token (Claude Code only) ------------------------------------------------------------
        //
        // Owner request, 2026-10-07: a module may run Claude Code on a long-lived token from `claude setup-token` instead
        // of the CLI's own sign-in, so the companion's calls can go to another account than the user's terminal does.
        // Claude Code reads that token from CLAUDE_CODE_OAUTH_TOKEN, so it is handed to THIS module's Claude Code children
        // and to nothing else: set as a user variable it would outrank /login for every Claude Code on the machine, the
        // user's own sessions included (the authentication docs' precedence list). The variables that outrank it in that
        // list are taken off the child, because a token saved here is the user's declared choice for these calls.
        //
        // Stored as AI Brain stores its cloud key: DPAPI for the current Windows user, in this runner's own folder, never
        // in a settings file, never logged and never shown (the pane says only that one is saved). Measured 2026-10-07
        // with a FAKE token on Claude Code 2.1.292: `claude auth status` answers authMethod "oauth_token" and names no
        // account, so the card can say a token is in use but not whose; a refused token ends the call with "Failed to
        // authenticate. API Error: 401 OAuth access token is invalid."; on a machine whose organisation requires remote
        // managed settings, with "Your organization requires remote managed settings to load, but they could not be
        // loaded. Run `claude auth login` to re-authenticate". It does not make --bare usable: bare mode never reads it.

        internal enum ClaudeTokenState { None, Saved, Unreadable, NotAToken }

        internal const string ClaudeTokenFileName = "claude-token.dpapi";
        internal const int MaximumClaudeTokenCharacters = 4096;
        // Internal so the self-check can seal a value the way an older version saved it; it separates this store from
        // other DPAPI data of the same user, and is not a secret.
        internal static readonly byte[] ClaudeTokenEntropy = Encoding.UTF8.GetBytes("DesktopAICompanion.CodingAgentCli.ClaudeToken");

        /// <summary>What outranks CLAUDE_CODE_OAUTH_TOKEN in Claude Code's credential order: a cloud provider's switch,
        /// a gateway bearer token and an API key. An apiKeyHelper in the user's settings outranks it too and cannot be
        /// taken off a child; the card's Signed in as row then names that method, not the token.</summary>
        internal static readonly string[] OutrankingClaudeCredentials =
        {
            "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY",
        };

        /// <summary>Put the token on a Claude Code child's environment, and take off what would outrank it. Nothing at all
        /// without a token: the child then inherits the user's environment as a terminal would.</summary>
        internal static void ApplyClaudeToken(IDictionary<string, string> environment, string token)
        {
            if (environment == null || string.IsNullOrEmpty(token)) return;
            foreach (string name in OutrankingClaudeCredentials) environment.Remove(name);
            environment["CLAUDE_CODE_OAUTH_TOKEN"] = token;
        }

        /// <summary>Null when <paramref name="value"/> can be a sign-in token, which comes back trimmed (a paste often
        /// carries the line break after it); otherwise the reason in the pane's words. It refuses what cannot work, not a
        /// prefix Anthropic may change: a space inside, an API key, a web address, and any character a bearer token cannot
        /// hold (RFC 6750's b64token: letters, digits and - . _ ~ + / with = at the end). The last two since 1.3.2: the
        /// owner's 1.3.1 install saved a 46-character web address as its token (2026-10-07), every Claude Code start then
        /// failed on it, Update CLI included, and the old check had let it through.</summary>
        internal static string CheckClaudeToken(string value, out string token)
        {
            token = (value ?? "").Trim();
            if (token.Length == 0) return "The sign-in token is empty.";
            if (token.Length > MaximumClaudeTokenCharacters)
                return "That is longer than a sign-in token can be (" +
                       MaximumClaudeTokenCharacters.ToString(CultureInfo.InvariantCulture) + " characters at most).";
            foreach (char character in token)
                if (character < '!' || character > '~')
                    return "A sign-in token is one unbroken line of letters, digits and punctuation, and this one has a space or another character inside it, so it was probably not copied whole.";
            if (token.StartsWith("sk-ant-api", StringComparison.Ordinal))
                return "That is an Anthropic API key, not a sign-in token. Run claude setup-token in a terminal and paste the token it prints.";
            if (token.IndexOf("://", StringComparison.Ordinal) >= 0 || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                return "That is a web address, not a sign-in token. Run claude setup-token in a terminal and paste the token it prints (it starts sk-ant-oat).";
            int equals = token.Length;
            while (equals > 0 && token[equals - 1] == '=') equals--;
            for (int i = 0; i < equals; i++)
            {
                char c = token[i];
                bool tokenCharacter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                                      c == '-' || c == '.' || c == '_' || c == '~' || c == '+' || c == '/';
                if (!tokenCharacter)
                    return "That is not a sign-in token: it has a \"" + c + "\" in it, and a token is only letters, digits and - . _ ~ + /. Run claude setup-token in a terminal and paste the token it prints.";
            }
            return null;
        }

        private string ClaudeTokenPath
        {
            get { return _scratchRoot == null ? null : Path.Combine(_scratchRoot, ClaudeTokenFileName); }
        }

        /// <summary>Seal and save a token, replacing any saved before. False, with the reason in the pane's words, when it
        /// is not a token or cannot be stored; the saved one, if any, is then left as it was.</summary>
        internal bool TrySetClaudeToken(string value, out string error)
        {
            int signIn;
            return TrySetClaudeToken(value, out error, out signIn);
        }

        /// <param name="signIn">The sign-in generation the save began (ForgetClaudeReadings), so Validate can record what it
        /// learnt on the token it has just saved; -1 when nothing was saved.</param>
        private bool TrySetClaudeToken(string value, out string error, out int signIn)
        {
            signIn = -1;
            string token;
            error = CheckClaudeToken(value, out token);
            if (error != null) return false;
            string path = ClaudeTokenPath;
            if (path == null)
            {
                error = "This module has no data folder of its own to keep a sign-in token in.";
                return false;
            }
            string sealedToken;
            try
            {
                sealedToken = Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(token), ClaudeTokenEntropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                error = "Windows would not encrypt the token (" + ex.GetType().Name + ").";
                return false;
            }
            if (!AtomicFile.TryWriteAllText(path, sealedToken, null))
            {
                error = "The token could not be written to this module's folder.";
                return false;
            }
            signIn = ForgetClaudeReadings();
            Log("cli: claude sign-in token saved");
            return true;
        }

        /// <summary>Whether a token is saved, and the token when this Windows account can unseal it.</summary>
        internal ClaudeTokenState ReadClaudeToken(out string token)
        {
            token = null;
            string path = ClaudeTokenPath;
            if (path == null) return ClaudeTokenState.None;
            try
            {
                if (!File.Exists(path)) return ClaudeTokenState.None;
                string text = File.ReadAllText(path).Trim();
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(text), ClaudeTokenEntropy, DataProtectionScope.CurrentUser);
                string unsealed;
                // Unsealed but not a token: saved by 1.3.1, whose check let a web address through. Said as that, and never
                // handed to Claude Code, rather than as "cannot be read", which would send the user after the wrong cause.
                if (CheckClaudeToken(Encoding.UTF8.GetString(clear), out unsealed) != null) return ClaudeTokenState.NotAToken;
                token = unsealed;
                return ClaudeTokenState.Saved;
            }
            catch
            {
                return ClaudeTokenState.Unreadable;
            }
        }

        /// <summary>The saved token for a Claude Code child, or null: for Codex, with none saved, or with one this account
        /// cannot read.</summary>
        private string ReadableClaudeToken(CodingAgentKind agent)
        {
            if (agent != CodingAgentKind.Claude) return null;
            string token;
            return ReadClaudeToken(out token) == ClaudeTokenState.Saved ? token : null;
        }

        /// <summary>Remove Token: delete the saved token, so Claude Code calls use the CLI's own sign-in again. The user's
        /// data, so the delete is logged either way (owner rule, 2026-10-07), and a failure is said, not swallowed.</summary>
        internal string RemoveClaudeToken()
        {
            string path = ClaudeTokenPath;
            if (path == null || !File.Exists(path))
                return "No sign-in token is saved here, so Claude Code uses its own sign-in.";
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log("cli: claude sign-in token not removed: " + ex.GetType().Name);
                return "✗ The saved sign-in token could not be removed (" + ex.GetType().Name + "). Press Remove token again in a moment.";
            }
            ForgetClaudeReadings();
            Log("cli: claude sign-in token removed");
            return "✓ Removed. Claude Code calls use its own sign-in again.";
        }

        /// <summary>A new or removed token makes the card's account row, its last Validate and the model that last answered
        /// describe the old sign-in (another sign-in can be another organisation, with another catalog), so all three are
        /// dropped and the next pane open reads afresh; and the sign-in generation moves on, so a reading still in flight on
        /// the old sign-in cannot put them back when it ends (review finding F6). Returns the new generation.</summary>
        private int ForgetClaudeReadings()
        {
            lock (_detailsSync)
            {
                _details.Remove(CodingAgentKind.Claude);
                _lastValidation.Remove(CodingAgentKind.Claude);
                _lastAnswered.Remove(CodingAgentKind.Claude);
                return ++_claudeSignInGeneration;
            }
        }

        // Which saved sign-in a reading of Claude Code belongs to (review finding F6): moved on under _detailsSync by every
        // token save and removal. A call, a Validate or a details read takes it before it reads the token and records what
        // it learnt only if it has not moved since, checked under the same lock as the write. Before this a summary still
        // running on organisation A's token when the user pressed Remove token, or applied B's, finished and wrote A's model
        // back as the one that last answered, under B. Taken BEFORE the token is read, so a change after the read is always
        // seen; one in between is counted too, which can only drop a reading that was right, never keep a stale one.
        private int _claudeSignInGeneration;

        private int ClaudeSignInGeneration()
        {
            lock (_detailsSync) return _claudeSignInGeneration;
        }

        /// <summary>Whether a reading of <paramref name="agent"/> taken at <paramref name="signIn"/> still describes the saved
        /// sign-in. Codex has none of the module's, so its readings always do. Call under _detailsSync.</summary>
        private bool SameSignIn(CodingAgentKind agent, int signIn)
        {
            return agent != CodingAgentKind.Claude || signIn == _claudeSignInGeneration;
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
                ClaudeStreamReading read = ReadClaudeStream(result.StandardOutput);
                answer.InputTokens = read.InputTokens;
                answer.Model = read.Model;
                answer.FallbackFrom = read.FallbackFrom;
                answer.FallbackTo = read.FallbackTo;
                if (read.Found && !read.IsError && !string.IsNullOrWhiteSpace(read.Text))
                {
                    answer.Outcome = CliOutcome.Ok;
                    answer.Text = read.Text.Trim();
                    return;
                }
                said = read.Found ? (read.Text ?? "") + " " + (read.IsError ? "" : read.Subtype ?? "") : "";
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
        /// again"; Claude Code: "OAuth token has expired", "Your session has expired. Please run /login", and where the
        /// organisation requires remote managed settings, "... could not be loaded. Run `claude auth login` to
        /// re-authenticate", which is what a refused sign-in token meets there first), then a missing
        /// sign-in ("Not logged in · Please run /login", "Invalid API key", an answered 401), then a refused model: the
        /// server's words for one, and Claude Code's own (lane feature/cli-model-effort, measured 2026-10-09 on 2.1.293
        /// with an id that does not exist: the result line "There's an issue with the selected model (X). It may not exist
        /// or you may not have access to it." with stderr "[claude-code:unrecognized_model]"; and "is restricted by your
        /// organization's settings" for a model the organisation does not allow), which read as a plain failure before.
        /// </summary>
        internal static CliOutcome Classify(string said)
        {
            string s = (said ?? "").ToLowerInvariant();
            if (s.Length == 0) return CliOutcome.Failed;
            if (Has(s, "requires a newer version") || Has(s, "newer version of codex") || Has(s, "newer version of claude"))
                return CliOutcome.CliTooOld;
            if (Has(s, "refresh token") || Has(s, "could not be refreshed") || Has(s, "sign in again") ||
                Has(s, "sign-in again") || Has(s, "session has expired") || Has(s, "token has expired") ||
                Has(s, "token has been revoked") || Has(s, "was revoked") || Has(s, "authentication required") ||
                Has(s, "re-authenticate"))
                return CliOutcome.SignInExpired;
            if (Has(s, "not logged in") || Has(s, "not signed in") || Has(s, "please run /login") ||
                Has(s, "invalid api key") || Has(s, "codex login") || Has(s, "please log in") ||
                Has(s, "login required") || Has(s, "unauthorized") || HasStandalone(s, "401"))
                return CliOutcome.NotSignedIn;
            if (Has(s, "model") && (Has(s, "not supported") || Has(s, "not available") || Has(s, "does not exist") ||
                Has(s, "not found") || Has(s, "unsupported") || Has(s, "no access") || Has(s, "not allowed") ||
                Has(s, "invalid model")))
                return CliOutcome.ModelRefused;
            // "issue with the selected model" without the apostrophe before it, which a typographic one would miss.
            if (Has(s, "issue with the selected model") || Has(s, "unrecognized_model") ||
                Has(s, "restricted by your organization"))
                return CliOutcome.ModelRefused;
            return CliOutcome.Failed;
        }

        private static bool Has(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Whether a refusal's words are about the EFFORT rather than the model (review finding F17): "reasoning" or
        /// "effort" in them, with the effort the call ran at standing alone, so "low" is not found in "allowed" or "slow".
        /// Not measured against a real Codex refusal (no lane call reaches a model): it assumes the server names the
        /// parameter (reasoning.effort, or the word effort) and the value it refused, as an API refusing a parameter's value
        /// usually does, and it reads the bounded excerpt the pane shows (Said), whose first words are the CLI's error. Used
        /// to keep the automatic pick cached and to point the user at the effort row.
        /// </summary>
        internal static bool RefusalNamesEffort(string said, string effort)
        {
            string s = (said ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(effort) || !(Has(s, "reasoning") || Has(s, "effort"))) return false;
            return HasStandalone(s, effort);
        }

        /// <summary>A number or a word standing alone, so "401" is not found inside "4012" or a request id, nor "low"
        /// inside "allowed".</summary>
        private static bool HasStandalone(string haystack, string number)
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

        /// <summary>What one Claude Code stream said: the result line's answer and usage, and the model that answered.</summary>
        internal sealed class ClaudeStreamReading
        {
            internal bool Found;
            internal string Text = "";
            internal bool IsError;
            internal string Subtype = "";
            internal long InputTokens = -1;
            internal string Model = "";
            internal string FallbackFrom = "";
            internal string FallbackTo = "";
        }

        /// <summary>
        /// Claude Code's stream: the answer and the usage are on the LAST line whose type is "result"; on a failure that
        /// line carries is_error and the error text in "result". The input count is the uncached input plus both cache
        /// counts, which is what the brief's token rows add up.
        ///
        /// The model that ANSWERED is the assistant message's message.model (lane feature/cli-model-effort, measured
        /// 2026-10-09 on 2.1.293), the last one the stream carries. Not system/init's model, which echoes the request, an id
        /// that does not exist included; not the result line's modelUsage, whose first key can be Claude Code's own Haiku
        /// side request. An errored call's assistant message says "&lt;synthetic&gt;", which IsUsableSlug refuses with any id
        /// that is not one, so the Model stays empty and nothing claims a model answered. A system event of subtype
        /// model_fallback names the model asked of the server (original_model) and the one that answered instead
        /// (fallback_model).
        /// </summary>
        internal static ClaudeStreamReading ReadClaudeStream(string stdout)
        {
            var read = new ClaudeStreamReading();
            foreach (JsonObject line in JsonLines(stdout))
            {
                string type = StringOf(line, "type");
                if (string.Equals(type, "assistant", StringComparison.Ordinal))
                {
                    JsonObject message = line["message"] as JsonObject;
                    string model = message == null ? "" : StringOf(message, "model").Trim();
                    if (IsUsableSlug(model)) read.Model = model;
                    continue;
                }
                if (string.Equals(type, "system", StringComparison.Ordinal))
                {
                    if (string.Equals(StringOf(line, "subtype"), "model_fallback", StringComparison.Ordinal))
                    {
                        string from = StringOf(line, "original_model").Trim();
                        string to = StringOf(line, "fallback_model").Trim();
                        if (IsUsableSlug(from)) read.FallbackFrom = from;
                        if (IsUsableSlug(to)) read.FallbackTo = to;
                    }
                    continue;
                }
                if (!string.Equals(type, "result", StringComparison.Ordinal)) continue;
                read.Found = true;
                read.Text = StringOf(line, "result");
                read.Subtype = StringOf(line, "subtype");
                read.IsError = BoolOf(line, "is_error");
                JsonObject usage = line["usage"] as JsonObject;
                read.InputTokens = usage == null
                    ? -1
                    : Math.Max(0, LongOf(usage, "input_tokens")) + Math.Max(0, LongOf(usage, "cache_creation_input_tokens")) +
                      Math.Max(0, LongOf(usage, "cache_read_input_tokens"));
            }
            return read;
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

        /// <summary>
        /// Text from outside as a pane or the log may show it (review finding F3): what a CLI said, a catalog's display
        /// name, a settings value quoted back in a refusal, the model ids RanOn names. Every C0 and C1 control character is
        /// dropped, and so are the bidirectional controls (U+061C, U+200E, U+200F, U+202A to U+202E, U+2066 to U+2069),
        /// which reorder what follows them on screen; every line break (CR, LF, NEL U+0085, the line and paragraph
        /// separators U+2028 and U+2029) and the tab become a space, so nothing can start a line of its own. Nothing else
        /// changes. Written with numeric code points, not escapes. The model ids are clean by construction (IsUsableSlug,
        /// IsCodexModelName and the alias list hold ASCII letters, digits and . - _ : / alone), so for them it changes
        /// nothing and is there so the rule has no exception to remember.
        /// </summary>
        internal static string Displayable(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var text = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                int code = c;
                if (code == 0x0D || code == 0x0A || code == 0x09 || code == 0x85 || code == 0x2028 || code == 0x2029)
                {
                    text.Append(' ');
                    continue;
                }
                if (char.IsControl(c) || IsBidiControl(code)) continue;
                text.Append(c);
            }
            return text.ToString();
        }

        private static bool IsBidiControl(int code)
        {
            return code == 0x061C || code == 0x200E || code == 0x200F || (code >= 0x202A && code <= 0x202E) ||
                   (code >= 0x2066 && code <= 0x2069);
        }

        /// <summary>A pane-sized, single-line excerpt, made displayable first (Displayable).</summary>
        internal static string OneLine(string value)
        {
            string one = Displayable(value).Trim();
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
                case CliOutcome.TokenUnreadable: return "token-unreadable";
                case CliOutcome.TokenNotAToken: return "token-not-a-token";
                case CliOutcome.ChoiceRefused: return "choice-refused";
                case CliOutcome.ModelCannotSee: return "model-cannot-see";
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
        // The model a call runs on is the one the module's settings chose (CliRequest.Model; lane feature/cli-model-effort
        // reversed 1.3.0's "no model chooser" on 2026-10-09, the file header says why), and with none chosen, Claude Code
        // runs on its default and Codex on the AUTOMATIC PICK below. Codex is asked which models THIS installed CLI may
        // use: `codex debug models` renders the catalog its own server hands it (no model call), and the pick is the entry
        // with the LOWEST priority among those whose visibility is "list", image-capable for a vision turn. The same output
        // gives the list a pane offers the user (CodexModelEntry): the listed models with their names, their efforts and
        // whether they take images; and, for the checks before a call (the screenshot, the effort), the same facts for
        // EVERY entry a user could name, hidden ones and any past the list's cap included (CodexPick.Catalog, review
        // finding F2). Never ~/.codex/models_cache.json: every Codex app on the machine rewrites that shared
        // file with ITS version's list, and a newer desktop app's list once offered models the older CLI was refused. The
        // pick and the list are cached per installed CLI, keyed by the binary's path, size and write time (an update
        // changes them) with its version beside it, in memory and in the module's own folder, so the catalog is fetched
        // once per CLI version, not once per remark; and every reader holds the cache to that key, the pane's included
        // (CurrentCachedPick, F7).

        internal sealed class CodexPick
        {
            internal string Fingerprint = "";
            internal string Version = "";
            internal string Text;
            internal string Vision;
            /// <summary>The catalog's listed models, for a pane's dropdown; empty when the catalog listed none this runner
            /// would pass on.</summary>
            internal List<CodexModelEntry> Models = new List<CodexModelEntry>();
            /// <summary>What the checks read, by slug (review finding F2): EVERY catalog entry whose slug a user could choose
            /// (IsCodexModelName), whatever its visibility and with no display cap, with the efforts it lists and whether it
            /// takes images. The dropdown's list alone was not enough: a model the user chose while it was listed, and which a
            /// later catalog keeps but hides, or one past the list's cap, read as unknown, so a text-only one was sent the
            /// screenshot. A listed model's entry here is the list's own, so the dropdown and the checks cannot disagree.</summary>
            internal Dictionary<string, CodexModelEntry> Catalog = new Dictionary<string, CodexModelEntry>(StringComparer.Ordinal);
        }

        /// <summary>One model of Codex's own catalog as a pane can offer it: a slug the runner passes on
        /// (IsCodexModelName), the catalog's display name, the efforts it lists (supported_reasoning_levels) and whether
        /// its input_modalities name "image".</summary>
        internal sealed class CodexModelEntry
        {
            internal string Slug = "";
            internal string DisplayName = "";
            internal string[] Efforts = new string[0];
            internal bool TakesImages;
        }

        /// <summary>The most models a list keeps: a catalog lists about ten; the bound keeps a hostile one off a pane.</summary>
        internal const int MaximumListedModels = 64;

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
            pick.Models = ListModels(json);
            pick.Catalog = CatalogFacts(json, pick.Models, pick.Text, pick.Vision);
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
                " listed=" + pick.Models.Count.ToString(CultureInfo.InvariantCulture) +
                " version=" + (pick.Version.Length > 0 ? pick.Version : "(unknown)"));
            return pick;
        }

        /// <summary>Whether a Codex model takes images, by the catalog's own word: true or false for a slug the cached
        /// catalog holds (any visibility, CodexPick.Catalog), null for one it does not, with nothing cached, or with a
        /// cache from another Codex binary than the one installed now (CurrentCachedPick), which is not evidence either
        /// way. Reads what is cached and never starts Codex, so a caller can ask before it captures the screen (AI Brain).</summary>
        internal bool? CodexModelTakesImages(string slug)
        {
            return CodexModelTakesImages(CurrentCachedPick(), slug);
        }

        /// <summary>The listed models of the cached pick (lowest priority first), or an empty list when nothing is cached
        /// yet, or the cache is another binary's (CurrentCachedPick). Reads what is cached and never starts Codex (aibrain
        /// 1.5.0): a pane builds its Codex model dropdown from this on EVERY open, a Claude Code user's included, and
        /// CachedDetails(Codex) would start a Codex probe for a pane that never asked about Codex. A copy, so the caller
        /// cannot change the cache.</summary>
        internal List<CodexModelEntry> CachedCodexModels()
        {
            CodexPick cached = CurrentCachedPick();
            return cached == null || cached.Models == null ? new List<CodexModelEntry>() : new List<CodexModelEntry>(cached.Models);
        }

        /// <summary>
        /// The cached pick, IF it describes the Codex installed now (review finding F7): read from memory or this module's
        /// file as CodexPickAsync reads it, then held to CodexPickAsync's fingerprint rule, by file probes alone (Locate,
        /// Fingerprint), never a process start. Null with nothing cached, with no Codex found, or when the binary changed
        /// since the catalog was read. Before this the two readers above took the cache as it was: after a `winget upgrade
        /// OpenAI.Codex` whose catalog dropped a model or made it text-only, the pane still offered it with the old image
        /// note, and AI Brain's check before a capture said it took images, so the screen was captured and the call, whose
        /// own pick read the new catalog, ended as ModelCannotSee instead of taking the text turn. Unknown is the answer
        /// for a catalog not read yet: the pane offers Automatic and the saved slug (both modules union the saved value
        /// in), the capture goes ahead as for an unlisted slug, and the next call or card read fetches the new catalog.
        /// </summary>
        private CodexPick CurrentCachedPick()
        {
            CodexPick cached;
            lock (_pickSync) cached = _pick;
            if (cached == null) cached = ReadPickCache();
            if (cached == null) return null;
            CliInstall install = Locate(CodingAgentKind.Codex);
            if (install == null) return null;
            return string.Equals(cached.Fingerprint, Fingerprint(install.Executable), StringComparison.Ordinal) ? cached : null;
        }

        private static bool? CodexModelTakesImages(CodexPick pick, string slug)
        {
            CodexModelEntry entry = CatalogEntry(pick, slug);
            return entry == null ? (bool?)null : entry.TakesImages;
        }

        /// <summary>What the pick's catalog says about one slug, or null when it holds no such entry.</summary>
        private static CodexModelEntry CatalogEntry(CodexPick pick, string slug)
        {
            if (pick == null || pick.Catalog == null || string.IsNullOrEmpty(slug)) return null;
            CodexModelEntry entry;
            return pick.Catalog.TryGetValue(slug, out entry) ? entry : null;
        }

        /// <summary>
        /// Why a call must not start on <paramref name="slug"/> at <paramref name="effort"/>, in the pane's words, or null
        /// (review findings F8, F17): when the pick's catalog entry for the slug lists the efforts it takes and this one is
        /// not among them. Null for a slug the catalog does not hold or an entry that lists no efforts, since not knowing is
        /// not a no (F102). <paramref name="automatic"/>: the slug is the automatic pick, not the user's, which the
        /// sentence says, because the fix is then the effort row alone. What Codex itself does with such a pair was not
        /// measured (no lane call reaches a model; N-cli-model-effort-02): refusing before anything starts is chosen over
        /// sending it, because the catalog is Codex's own word for this binary, and a refusal after the start would be
        /// labelled a refused MODEL and, on the automatic pick, forget the cached pick on every call.
        /// </summary>
        internal static string CodexEffortProblem(CodexPick pick, string slug, bool automatic, string effort)
        {
            CodexModelEntry entry = CatalogEntry(pick, slug);
            if (entry == null || entry.Efforts == null || entry.Efforts.Length == 0 || Array.IndexOf(entry.Efforts, effort) >= 0)
                return null;
            var offered = new List<string>();
            foreach (string level in Efforts) if (Array.IndexOf(entry.Efforts, level) >= 0) offered.Add(level);
            string which = automatic ? "Codex's automatic pick, " + slug + "," : "the model " + slug;
            if (offered.Count == 0) return which + " takes none of low, medium or high in Codex's own catalog";
            string listed = offered.Count == 1
                ? offered[0]
                : string.Join(", ", offered.GetRange(0, offered.Count - 1)) + " or " + offered[offered.Count - 1];
            return which + " takes " + listed + " effort in Codex's own catalog, not " + effort;
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
                // A pick saved before the list was kept (aibrain 1.3.0 to 1.4.0) has no "models": it is asked for again
                // once, rather than leave a pane's dropdown without the list until the next Codex update.
                JsonArray listed = o["models"] as JsonArray;
                if (listed == null) return null;
                // The same for one saved before every entry's facts were kept beside the list (this lane's own earlier
                // builds, F2): no "catalog", so it is asked for again once and the checks see hidden models too. A
                // catalog whose cache outgrows the bound above is fetched again once per session, the memory copy serving
                // the rest of it; a real catalog lists about ten models, far below it.
                JsonArray facts = o["catalog"] as JsonArray;
                if (facts == null) return null;
                var pick = new CodexPick
                {
                    Fingerprint = StringOf(o, "fingerprint"),
                    Version = StringOf(o, "version"),
                    Text = NullIfBlank(StringOf(o, "text")),
                    Vision = NullIfBlank(StringOf(o, "vision")),
                    Models = ReadModelEntries(listed),
                };
                if (!IsUsableSlug(pick.Text)) pick.Text = null;
                if (!IsUsableSlug(pick.Vision)) pick.Vision = null;
                pick.Catalog = MergeCatalog(pick.Models, facts, pick.Text, pick.Vision);
                return pick.Text == null && pick.Vision == null ? null : pick;
            }
            catch { return null; }
        }

        private void WritePickCache(CodexPick pick)
        {
            string path = PickCachePath;
            if (path == null) return;
            var models = new JsonArray();
            foreach (CodexModelEntry entry in pick.Models) models.Add(EntryJson(entry, true));
            var catalog = new JsonArray();
            foreach (CodexModelEntry entry in pick.Catalog.Values) catalog.Add(EntryJson(entry, false));
            var o = new JsonObject
            {
                ["fingerprint"] = pick.Fingerprint,
                ["version"] = pick.Version,
                ["text"] = pick.Text ?? "",
                ["vision"] = pick.Vision ?? "",
                ["models"] = models,
                ["catalog"] = catalog,
            };
            AtomicFile.TryWriteAllText(path, o.ToJsonString(), null);
        }

        /// <summary>One entry in the shape the catalog writes it, so the cache is read back by the catalog's own readers.</summary>
        private static JsonObject EntryJson(CodexModelEntry entry, bool withName)
        {
            var efforts = new JsonArray();
            foreach (string level in entry.Efforts) efforts.Add(level);
            var o = new JsonObject { ["slug"] = entry.Slug };
            if (withName) o["display_name"] = entry.DisplayName;
            o["supported_reasoning_levels"] = efforts;
            o["input_modalities"] = entry.TakesImages ? new JsonArray("text", "image") : new JsonArray("text");
            return o;
        }

        /// <summary>The checks' facts (CodexPick.Catalog) from a `codex debug models` catalog: every entry whose slug a user
        /// could choose, any visibility, no cap, the listed models first as the list holds them (F2), and the automatic
        /// pick's own entries (<paramref name="pickSlugs"/>, the pick's text and vision slugs) whatever their slug.</summary>
        internal static Dictionary<string, CodexModelEntry> CatalogFacts(string catalogJson, List<CodexModelEntry> listed,
            params string[] pickSlugs)
        {
            JsonArray models = null;
            try
            {
                int start = string.IsNullOrWhiteSpace(catalogJson) ? -1 : catalogJson.IndexOf('{');
                JsonObject root = start < 0 ? null : JsonNode.Parse(catalogJson.Substring(start),
                    null, new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject;
                models = root == null ? null : root["models"] as JsonArray;
            }
            catch { models = null; }
            return MergeCatalog(listed, models, pickSlugs);
        }

        /// <summary>The listed entries by slug, then every other entry of <paramref name="entries"/> whose slug a user could
        /// choose, or that is one of <paramref name="pickSlugs"/>, and is not there yet, in the order given: so a listed
        /// model's facts are the list's own, and any other slug's are its first entry's.
        ///
        /// The pick's own slugs (round 2, the fix pass's skeptic): the automatic pick takes any slug IsUsableSlug passes
        /// (an "Org/Upper_Case" one too), where a user's choice must pass IsCodexModelName, so a pick no user could have
        /// typed had no entry here and CodexEffortProblem never refused an effort its entry does not list, though its
        /// sentence says it holds for the automatic pick as for a chosen slug. Admitted, rather than the claim narrowed,
        /// because the refusal before the start is the better outcome for the pick too (CodexEffortProblem says why), and
        /// such a slug goes on the command line as the pick already, so it reaches nothing it did not reach before. Only a
        /// pick IsUsableSlug passes: the readers clear one it does not before they get here.</summary>
        private static Dictionary<string, CodexModelEntry> MergeCatalog(List<CodexModelEntry> listed, IEnumerable<JsonNode> entries,
            params string[] pickSlugs)
        {
            var facts = new Dictionary<string, CodexModelEntry>(StringComparer.Ordinal);
            if (listed != null)
                foreach (CodexModelEntry known in listed)
                    if (known != null && !facts.ContainsKey(known.Slug)) facts[known.Slug] = known;
            if (entries == null) return facts;
            foreach (JsonNode node in entries)
            {
                JsonObject entry = node as JsonObject;
                if (entry == null) continue;
                string slug = StringOf(entry, "slug").Trim();
                bool picked = pickSlugs != null && Array.IndexOf(pickSlugs, slug) >= 0 && IsUsableSlug(slug);
                if (!(IsCodexModelName(slug) || picked) || facts.ContainsKey(slug)) continue;
                facts[slug] = new CodexModelEntry
                {
                    Slug = slug,
                    DisplayName = slug,
                    Efforts = EffortsOf(entry),
                    TakesImages = Lists(entry, "input_modalities", "image"),
                };
            }
            return facts;
        }

        /// <summary>The list a pane offers from a `codex debug models` catalog: the entries whose visibility is "list" and
        /// whose slug the runner would pass on as a user's choice (IsCodexModelName), lowest priority first (a tie keeps
        /// the catalog's order, an entry with no priority goes last), each slug once, at most MaximumListedModels.</summary>
        internal static List<CodexModelEntry> ListModels(string catalogJson)
        {
            var list = new List<CodexModelEntry>();
            if (string.IsNullOrWhiteSpace(catalogJson)) return list;
            try
            {
                int start = catalogJson.IndexOf('{');
                if (start < 0) return list;
                JsonObject root = JsonNode.Parse(catalogJson.Substring(start),
                    null, new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject;
                JsonArray models = root == null ? null : root["models"] as JsonArray;
                if (models == null) return list;
                var shown = new List<JsonNode>();
                var priorities = new List<long>();
                foreach (JsonNode node in models)
                {
                    JsonObject entry = node as JsonObject;
                    if (entry == null || StringOf(entry, "visibility") != "list") continue;
                    long priority = LongOf(entry, "priority");
                    if (priority < 0) priority = long.MaxValue;
                    // Inserted in priority order, after every entry of the same priority: a stable sort by hand.
                    int at = priorities.Count;
                    while (at > 0 && priorities[at - 1] > priority) at--;
                    priorities.Insert(at, priority);
                    shown.Insert(at, entry);
                }
                return ReadModelEntries(shown);
            }
            catch { return new List<CodexModelEntry>(); }
        }

        /// <summary>Entries as the list keeps them, from the catalog or from the cache: a slug the runner passes on, a
        /// display name of one plain line (the slug when the catalog gives none), the efforts as plain lowercase words
        /// (from objects with an "effort", or plain strings), and whether "image" is an input modality.</summary>
        private static List<CodexModelEntry> ReadModelEntries(IEnumerable<JsonNode> entries)
        {
            var list = new List<CodexModelEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode node in entries)
            {
                if (list.Count >= MaximumListedModels) break;
                JsonObject entry = node as JsonObject;
                if (entry == null) continue;
                string slug = StringOf(entry, "slug").Trim();
                if (!IsCodexModelName(slug) || !seen.Add(slug)) continue;
                string name = OneLine(StringOf(entry, "display_name"));
                if (name.Length > 64) name = UnicodeTextProgress.TruncateAtCodePointBoundary(name, 64);
                list.Add(new CodexModelEntry
                {
                    Slug = slug,
                    DisplayName = name.Length > 0 ? name : slug,
                    Efforts = EffortsOf(entry),
                    TakesImages = Lists(entry, "input_modalities", "image"),
                });
            }
            return list;
        }

        /// <summary>The efforts an entry lists, as plain lowercase words (from objects with an "effort", or plain strings),
        /// each once.</summary>
        private static string[] EffortsOf(JsonObject entry)
        {
            var efforts = new List<string>();
            JsonArray levels = entry["supported_reasoning_levels"] as JsonArray;
            if (levels != null)
            {
                foreach (JsonNode level in levels)
                {
                    JsonObject described = level as JsonObject;
                    string word = described != null ? StringOf(described, "effort") : PlainString(level);
                    if (IsEffortWord(word) && !efforts.Contains(word)) efforts.Add(word);
                }
            }
            return efforts.ToArray();
        }

        /// <summary>An effort as a catalog names one ("low", "xhigh", "minimal"): lowercase letters, sixteen at most.</summary>
        private static bool IsEffortWord(string word)
        {
            if (string.IsNullOrEmpty(word) || word.Length > 16) return false;
            foreach (char c in word) if (c < 'a' || c > 'z') return false;
            return true;
        }

        private static string PlainString(JsonNode node)
        {
            JsonValue value = node as JsonValue;
            string s;
            return value != null && value.TryGetValue(out s) ? s ?? "" : "";
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
        /// <param name="carryClaudeToken">True for `claude auth status` alone, so the card describes the sign-in the calls
        /// use. `--version` needs no sign-in, and a token Claude Code refuses stops it before it does anything (aibrain
        /// 1.3.2, after Update CLI failed on a bad token), so nothing else carries one.</param>
        private async Task<CliProcessResult> RunToolAsync(CliInstall install, string[] arguments, TimeSpan timeout,
            CancellationToken cancellationToken, bool carryClaudeToken = false)
        {
            string working = WorkingDirectory();
            if (working == null) return null;
            // An unreadable or not-a-token saved value goes nowhere: its card row says so, and no call starts on it.
            ProcessStartInfo startInfo = NewStartInfo(install, working, carryClaudeToken ? ReadableClaudeToken(install.Agent) : null);
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
        /// What the pane's CLI card shows about one CLI: whether it is installed, its version, Codex's automatic pick and
        /// the models its catalog lists, and which account it is signed into. Read off the CLI itself, never off a file
        /// another app writes, and never logged: the account is on screen only.
        /// </summary>
        internal sealed class CliDetails
        {
            internal CodingAgentKind Agent;
            internal bool Installed;
            internal string Version = "";
            internal string TextModel;
            internal string VisionModel;
            /// <summary>Codex's listed models, for the card's model dropdown (lane feature/cli-model-effort); empty for
            /// Claude Code, which has no command that lists its models, and for a Codex whose catalog listed none.</summary>
            internal List<CodexModelEntry> CodexModels = new List<CodexModelEntry>();
            /// <summary>The "Signed in as" row: an account, a sign-in method, or a ✗/⚠ sentence.</summary>
            internal string SignedIn = "";
            /// <summary>Where the binary came from when that decides who updates it ("installed with WinGet"), else "".</summary>
            internal string Where = "";
            internal DateTime ReadAtUtc;
        }

        /// <summary>
        /// Read the details now. Claude Code: `claude auth status` prints JSON (loggedIn, email, subscriptionType). Codex:
        /// `codex login status` prints the METHOD only ("Logged in using ChatGPT"); no Codex command names the account,
        /// and decoding ~/.codex/auth.json for it was refused: that is Codex's credential store, not a file this module
        /// reads. Codex's model is the pick a call would use (CodexPickAsync), so the card names what will run.
        ///
        /// A read the caller's token stops throws OperationCanceledException and returns no reading (round 3, the review
        /// of round 2): a probe stopped that way answers empty (RunBoundedAsync's null) rather than throwing, which read
        /// as "did not say whether it is signed in", and RefreshDetailsAsync kept that reading for a minute; the catch-all
        /// below caught a thrown one the same way.
        /// </summary>
        internal async Task<CliDetails> ReadDetailsAsync(CodingAgentKind agent, CancellationToken cancellationToken)
        {
            var details = new CliDetails { Agent = agent, ReadAtUtc = DateTime.UtcNow };
            CliInstall install = Locate(agent);
            if (install == null) return details;
            details.Installed = true;
            details.Where = CodingAgentCliText.WherePhrase(install.Source);
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
                    details.CodexModels = new List<CodexModelEntry>(pick.Models);
                }
                string ignored;
                ClaudeTokenState tokenState = agent == CodingAgentKind.Claude ? ReadClaudeToken(out ignored) : ClaudeTokenState.None;
                // One way out of the try, so the token check below sees every reading (round 3: these two returned from
                // inside it, with whatever version a stopped probe had left).
                if (tokenState == ClaudeTokenState.Unreadable)
                    details.SignedIn = CodingAgentCliText.TokenUnreadableSentence;
                else if (tokenState == ClaudeTokenState.NotAToken)
                    details.SignedIn = CodingAgentCliText.TokenNotATokenSentence;
                else
                {
                    CliProcessResult status = await RunToolAsync(install,
                        agent == CodingAgentKind.Claude ? new[] { "auth", "status" } : new[] { "login", "status" },
                        StatusTimeout, cancellationToken, carryClaudeToken: true).ConfigureAwait(false);
                    details.SignedIn = status == null
                        ? "⚠ " + CodingAgents.ProductName(agent) + " did not say whether it is signed in."
                        : agent == CodingAgentKind.Claude
                            ? DescribeClaudeAuthStatus(status.StandardOutput, tokenState == ClaudeTokenState.Saved)
                            : DescribeCodexLoginStatus(status.StandardOutput + "\n" + status.StandardError);
                }
                // A probe the caller's token stopped answered empty instead of throwing, so what was read is no reading.
                cancellationToken.ThrowIfCancellationRequested();
            }
            // Before the catch-all, which would otherwise turn the caller's cancel into "did not say whether it is signed in".
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                details.SignedIn = "⚠ " + CodingAgents.ProductName(agent) + " did not say whether it is signed in.";
            }
            finally { Leave(CallKind.Probe); }
            return details;
        }

        /// <param name="savedToken">The status was read with this module's saved token on the child, so an
        /// "oauth_token" sign-in is that token's.</param>
        internal static string DescribeClaudeAuthStatus(string output, bool savedToken)
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
            // A token is not checked by `auth status` and names no account (measured 2026-10-07): say which token, and
            // that Validate is what proves it.
            if (string.Equals(StringOf(o, "authMethod"), "oauth_token", StringComparison.Ordinal))
                return savedToken
                    ? "The sign-in token saved in this card (Claude Code does not say which account it belongs to; Validate checks that it works)"
                    : "A sign-in token from CLAUDE_CODE_OAUTH_TOKEN in your environment (Claude Code does not say which account)";
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
                int signIn = ClaudeSignInGeneration();
                try { await RefreshDetailsAsync(agent, CancellationToken.None).ConfigureAwait(false); }
                catch { }
                finally
                {
                    if (agent == CodingAgentKind.Claude) Interlocked.Exchange(ref _claudeProbing, 0);
                    else Interlocked.Exchange(ref _codexProbing, 0);
                }
                // A token saved or removed while this read ran made it the old sign-in's, so RefreshDetailsAsync dropped it
                // (F6), and a pane open meanwhile, the rebuild Remove token asks for included, found this read in flight and
                // started none. One more, now that the flag is down, so the new sign-in's details come without the user
                // reopening the pane (round 2, review of the fix pass: the card said "Checking…" until then). Only when the
                // generation moved, so an ordinary read is never followed by a second.
                if (agent == CodingAgentKind.Claude && ClaudeSignInGeneration() != signIn) BeginDetailsRefresh(agent);
            });
        }

        /// <summary>The details kept for this CLI, read without starting a refresh, or null when none were read this
        /// session or the sign-in changed since. The card's row for a CLI chosen on screen and not saved reads this: a pane
        /// open starts no CLI the user did not choose (round 2).</summary>
        internal CliDetails KeptDetails(CodingAgentKind agent)
        {
            if (agent == CodingAgentKind.None) return null;
            CliDetails kept;
            lock (_detailsSync) _details.TryGetValue(agent, out kept);
            return kept;
        }

        /// <summary>The kept details when they are fresh, else read now and kept (Validate's read, awaited); null when the
        /// caller's token stopped the read, which then keeps nothing (ReadDetailsAsync throws, so RefreshDetailsAsync never
        /// reaches its write).</summary>
        private async Task<CliDetails> FreshDetailsAsync(CodingAgentKind agent, CancellationToken cancellationToken)
        {
            CliDetails kept = KeptDetails(agent);
            if (kept != null && DateTime.UtcNow - kept.ReadAtUtc <= SignInFreshness) return kept;
            try { return await RefreshDetailsAsync(agent, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }

        /// <summary>Read now and keep it for the card. Awaited directly by the self-test. A read the token stopped throws
        /// OperationCanceledException and keeps nothing.</summary>
        internal async Task<CliDetails> RefreshDetailsAsync(CodingAgentKind agent, CancellationToken cancellationToken)
        {
            // Kept only for the sign-in it was read on (F6): a token saved or removed meanwhile makes it the old account's.
            int signIn = ClaudeSignInGeneration();
            CliDetails read = await ReadDetailsAsync(agent, cancellationToken).ConfigureAwait(false);
            lock (_detailsSync)
                if (SameSignIn(agent, signIn)) _details[agent] = read;
            return read;
        }

        /// <summary>Whether the card's single-flight read of this CLI is running: the self-check's wait for it to end.</summary>
        internal bool DetailsReadInFlightForDiagnostics(CodingAgentKind agent)
        {
            return (agent == CodingAgentKind.Claude ? Volatile.Read(ref _claudeProbing) : Volatile.Read(ref _codexProbing)) != 0;
        }

        /// <summary>The details kept for the card, read without starting a refresh: the self-check's view of the cache.</summary>
        internal CliDetails KeptDetailsForDiagnostics(CodingAgentKind agent)
        {
            CliDetails kept;
            lock (_detailsSync) _details.TryGetValue(agent, out kept);
            return kept;
        }

        // The card's "Status" row: the last Validate, with when it ran and how long it took, and which CLI it tested. Kept
        // per CLI, for this session, each with the order it was recorded in, so the row can show the most recent of either
        // (LatestValidation, round 2): the first on-screen walk showed a pending Codex Validate's tick beside the button
        // and the saved Claude Code's older tick in the row under it, two green ticks naming different CLIs. And with the
        // model and effort it tested, so an Apply that saves another choice for that CLI can forget it
        // (ForgetValidationUnlessTested, round 3).
        private sealed class ValidationRecord
        {
            internal long Order;
            internal string Line = "";
            /// <summary>The model and the effort the Validate asked for, as TestedChoice reads them.</summary>
            internal string Model = "";
            internal string Effort = "";
        }

        private readonly Dictionary<CodingAgentKind, ValidationRecord> _lastValidation = new Dictionary<CodingAgentKind, ValidationRecord>();
        private long _validationsRecorded;

        /// <summary>A model and an effort as CheckChoice would send them, without refusing either: trimmed, and a blank effort
        /// the runner's floor. What a Validate tested and what an Apply saved are compared in this form.</summary>
        private static void TestedChoice(string requestedModel, string requestedEffort, out string model, out string effort)
        {
            model = (requestedModel ?? "").Trim();
            effort = (requestedEffort ?? "").Trim();
            if (effort.Length == 0) effort = DefaultEffort;
        }

        /// <summary>The last Validate's line for this CLI, or null when none ran this session.</summary>
        internal string LastValidation(CodingAgentKind agent)
        {
            lock (_detailsSync)
            {
                ValidationRecord kept;
                return _lastValidation.TryGetValue(agent, out kept) ? kept.Line : null;
            }
        }

        /// <summary>The most recent Validate's line of either CLI, which names the CLI it tested, or null when none ran
        /// this session (or the only one ran on a sign-in since changed). The card's Status row: whichever CLI the radio
        /// shows, the last press of Validate is what it describes.</summary>
        internal string LatestValidation()
        {
            lock (_detailsSync)
            {
                string latest = null;
                long order = long.MinValue;
                foreach (ValidationRecord kept in _lastValidation.Values)
                    if (kept.Order > order)
                    {
                        order = kept.Order;
                        latest = kept.Line;
                    }
                return latest;
            }
        }

        /// <summary>
        /// Forget <paramref name="agent"/>'s last Validate when <paramref name="savedModel"/> and <paramref name="savedEffort"/>,
        /// the choice an Apply has just saved for that CLI, are not the model and effort it tested; true when one was
        /// forgotten. Both modules' Apply call it for each CLI (round 3, the second on-screen walk: after GPT-6-Astra at
        /// medium was applied the card's Status row still showed the green tick for gpt-6.1-sol at low, through a close and a
        /// reopen, as if the new choice had been tested). A choice validated on screen and then applied unchanged keeps its
        /// tick, and the other CLI's Validate is not touched. Compared as TestedChoice reads both, so a blank saved effort is
        /// the floor a Validate of it ran at.
        /// </summary>
        internal bool ForgetValidationUnlessTested(CodingAgentKind agent, string savedModel, string savedEffort)
        {
            string model, effort;
            TestedChoice(savedModel, savedEffort, out model, out effort);
            lock (_detailsSync)
            {
                ValidationRecord kept;
                if (!_lastValidation.TryGetValue(agent, out kept)) return false;
                if (string.Equals(kept.Model, model, StringComparison.Ordinal) && string.Equals(kept.Effort, effort, StringComparison.Ordinal))
                    return false;
                return _lastValidation.Remove(agent);
            }
        }

        private void RecordValidation(CodingAgentKind agent, CliAnswer answer, int signIn, string testedModel, string testedEffort)
        {
            string marker = answer.Ok ? "✓" : (answer.Outcome == CliOutcome.Busy || answer.Outcome == CliOutcome.Cancelled ? "⚠" : "✗");
            string ran = answer.Ok ? CodingAgentCliText.RanOn(answer) : "";
            // "✓ Codex: answered at 18:41:01, in 5.1 s, on gpt-6.1-sol at low effort": the CLI first, since the row shows
            // either CLI's (round 2).
            string line = marker + " " + CodingAgents.ProductName(agent) + ": " +
                          (answer.Ok ? "answered" : CodingAgentCliText.Brief(answer.Outcome)) + " at " +
                          DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ", in " +
                          (answer.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s" +
                          (ran.Length > 0 ? ", " + ran : "");
            var record = new ValidationRecord { Line = line };
            TestedChoice(testedModel, testedEffort, out record.Model, out record.Effort);
            lock (_detailsSync)
                if (SameSignIn(agent, signIn))
                {
                    record.Order = ++_validationsRecorded;
                    _lastValidation[agent] = record;
                }
        }

        // The model that last ANSWERED, per CLI, for this session (lane feature/cli-model-effort): a real call's, a remark's,
        // an audition sample's or a summary's, so both modules' Status rows can name what ran, which is not always what was
        // asked for. Never Validate's (round 2, CliRequest.Validation): its test of a choice on screen is the card's
        // Validate row's to say. Kept beside the Validate line, under the same lock, and dropped with it when the sign-in
        // changes.
        private readonly Dictionary<CodingAgentKind, CliAnswer> _lastAnswered = new Dictionary<CodingAgentKind, CliAnswer>();

        /// <summary>The model, the alias or slug asked for and the effort of this CLI's last real call that answered (never
        /// a Validate), or null when none has this session. A copy: its Text is never kept.</summary>
        internal CliAnswer LastAnswered(CodingAgentKind agent)
        {
            lock (_detailsSync)
            {
                CliAnswer kept;
                return _lastAnswered.TryGetValue(agent, out kept) ? kept : null;
            }
        }

        private void RecordAnswered(CodingAgentKind agent, CliAnswer answer, int signIn)
        {
            var kept = new CliAnswer
            {
                Outcome = CliOutcome.Ok,
                Model = answer.Model,
                RequestedModel = answer.RequestedModel,
                Effort = answer.Effort,
                FallbackFrom = answer.FallbackFrom,
                FallbackTo = answer.FallbackTo,
                Version = answer.Version,
                ElapsedMilliseconds = answer.ElapsedMilliseconds,
            };
            lock (_detailsSync)
                if (SameSignIn(agent, signIn)) _lastAnswered[agent] = kept;
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
                // An install another program updates is not updated from here: on WinGet the CLI's own update answers
                // "up to date" whatever the version (the setup docs), and VS Code replaces its copy with the extension.
                string updatedElsewhere = CodingAgentCliText.UpdatedElsewhere(agent, install.Source);
                if (updatedElsewhere != null)
                {
                    Log("cli: " + CodingAgents.IdOf(agent) + " update not run, source=" + CodingAgentCliText.SourceWord(install.Source));
                    return updatedElsewhere;
                }
                string before = await VersionCoreAsync(install, cancellationToken).ConfigureAwait(false);
                var bounded = new CliAnswer();
                string working = WorkingDirectory();
                if (working == null)
                    return CodingAgentCliText.Describe(agent, new CliAnswer { Outcome = CliOutcome.NoPrivateFolder }, null);
                // No sign-in token on the update (1.3.2): it needs none, and on 2026-10-07 a token Claude Code refused made
                // the owner's Update CLI fail before the update began. The update then runs on the CLI's own setup, as it
                // would from a terminal.
                ProcessStartInfo startInfo = NewStartInfo(install, working, null);
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
                // The card's details of this CLI name the version from before the update, so they go too, once the update
                // has exited: the rebuild after the press reads afresh, and so does the next Validate, which takes its
                // version from details up to a minute old (round 3, the review of round 2: within that minute Validate named
                // the old version right under this answer's "A -> B").
                lock (_detailsSync) _details.Remove(agent);
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
        /// <summary>A few words for an outcome, for the card's Status row ("✗ Claude Code: not signed in at 14:32:05, in
        /// 0.8 s").</summary>
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
                case CliOutcome.TokenUnreadable: return "saved token unreadable";
                case CliOutcome.TokenNotAToken: return "saved token is not a sign-in token";
                case CliOutcome.ChoiceRefused: return "model or effort setting refused";
                case CliOutcome.ModelCannotSee: return "the chosen model takes no images";
                default: return "failed";
            }
        }

        /// <summary>
        /// What a call that answered ran on, for Validate's sentence, the card's CLI row and, without its leading "on ",
        /// Remembrance's summary header (lane feature/cli-model-effort): <see cref="RanOnShort"/> and then the effort.
        /// "on claude-haiku-5-5 at low effort"; "on claude-sonnet-5-5 (asked for haiku) at low effort" when Claude Code
        /// answered on another family than the alias asked for, a fact and not a failure; "on a model it did not name
        /// (asked for haiku) at low effort" when the stream named no model a call can be said to have run on; "at low
        /// effort" when nothing was asked for and nothing named; "" for nothing known.
        /// </summary>
        internal static string RanOn(CliAnswer answer)
        {
            if (answer == null) return "";
            string on = RanOnShort(answer);
            string effort = CodingAgentCli.Displayable(answer.Effort);
            string at = effort.Length == 0 ? "" : "at " + effort + " effort";
            return on.Length > 0 && at.Length > 0 ? on + " " + at : on + at;
        }

        /// <summary>
        /// The model a call that answered ran on, with its notes and without the effort: "on claude-haiku-5-5", "on
        /// claude-sonnet-5-5 (asked for haiku; Claude Code fell back to it from claude-opus-5-5)", or "" when no model is
        /// known and none was asked for. RanOn is this and the effort, and both modules' Status rows say this one after
        /// "last answered " (review finding F10, through <see cref="LastAnsweredOn"/>), so the fallback and the asked-for
        /// notes are worded once, here.
        ///
        /// The model is the one the stream named (Model), else the one Claude Code said it fell back to (FallbackTo); it is
        /// never the alias asked for (review findings F5, F12: when the stream named neither, haiku was said as if haiku had
        /// answered, and a fallback note then named the alias as the model fallen back to). With neither named and an alias
        /// asked for, the sentence says "a model it did not name" and "asked for" the alias. The fallback note says "fell
        /// back to it" only of the model it names as the one fallen back to, and otherwise both ends.
        /// </summary>
        internal static string RanOnShort(CliAnswer answer)
        {
            return RanOnShort(answer, null);
        }

        /// <param name="request">What the call asked for, said in place of RanOnShort's "asked for" note when the caller
        /// names it (<see cref="LastAnsweredOn"/>), so one parenthesis carries every note; null for the plain short form.</param>
        private static string RanOnShort(CliAnswer answer, string request)
        {
            if (answer == null) return "";
            string model = CodingAgentCli.Displayable(answer.AnsweredModel);
            string asked = CodingAgentCli.Displayable(answer.RequestedModel);
            string from = CodingAgentCli.Displayable(answer.FallbackFrom);
            string to = CodingAgentCli.Displayable(answer.FallbackTo);
            var notes = new List<string>();
            if (request != null) notes.Add("that call asked for " + request);
            else if (answer.AnsweredOtherModel || (model.Length == 0 && asked.Length > 0)) notes.Add("asked for " + asked);
            if (from.Length > 0)
                notes.Add(to.Length == 0 || string.Equals(to, model, StringComparison.Ordinal)
                    ? "Claude Code fell back to it from " + from
                    : "Claude Code fell back from " + from + " to " + to);
            string subject = model.Length > 0 ? model : (notes.Count > 0 ? "a model it did not name" : "");
            if (subject.Length == 0) return "";
            return "on " + subject + (notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "");
        }

        /// <summary>
        /// What answered last, for a module's CLI row (<paramref name="withEffort"/> true: "on claude-opus-5-5 at medium
        /// effort", RanOn) and its Status row (false: "on claude-opus-5-5", RanOnShort), both said after "last answered "
        /// beside the SAVED choice; "" when nothing is known. Both modules word it here, so the fallback and the asked-for
        /// notes are never said two ways (review finding F10).
        ///
        /// When the call that answered asked for another model or effort than <paramref name="savedModel"/> and
        /// <paramref name="savedEffort"/> (an Apply since, or a Validate of a choice on screen that was never applied), the
        /// note says what that call asked for, in the parenthesis the other notes share, and carries its effort, so neither
        /// form adds the effort after it: "on claude-opus-5-5 (that call asked for opus at medium effort)" beside "haiku
        /// at low effort" (review finding F13: the pair read as a model swap). "Claude Code's default" or "the automatic
        /// pick" when that call asked for none.
        /// </summary>
        internal static string LastAnsweredOn(CodingAgentKind agent, CliAnswer last, string savedModel, string savedEffort, bool withEffort)
        {
            if (last == null) return "";
            // The saved choice as CheckChoice would send it: trimmed, a blank effort the runner's floor.
            string choiceModel = (savedModel ?? "").Trim();
            string choiceEffort = (savedEffort ?? "").Trim();
            if (choiceEffort.Length == 0) choiceEffort = CodingAgentCli.DefaultEffort;
            bool sameRequest = string.Equals(last.RequestedModel, choiceModel, StringComparison.Ordinal) &&
                               string.Equals(last.Effort, choiceEffort, StringComparison.Ordinal);
            if (sameRequest) return withEffort ? RanOn(last) : RanOnShort(last);
            string asked = CodingAgentCli.Displayable(last.RequestedModel);
            string effort = CodingAgentCli.Displayable(last.Effort);
            string request = (asked.Length > 0 ? asked : agent == CodingAgentKind.Codex ? "the automatic pick" : "Claude Code's default") +
                             (effort.Length > 0 ? " at " + effort + " effort" : "");
            // Never "": with a note to say, a model the stream did not name is "a model it did not name".
            return RanOnShort(last, request);
        }

        /// <summary>The card's row, and a call's answer, when what is saved as the token is not one (aibrain 1.3.2).</summary>
        internal const string TokenNotATokenSentence =
            "✗ What is saved as the sign-in token in this card is not a sign-in token (a 1.3.1 install could save a web address there), so nothing is sent with it. Press Remove token, then paste the token claude setup-token prints and press Validate, which saves it once it answers.";

        /// <summary>The card's row, and a call's answer, when the saved token cannot be unsealed by this account.</summary>
        internal const string TokenUnreadableSentence =
            "✗ The sign-in token saved in this card cannot be read by this Windows account (it was saved by another user, or on another machine). Paste it again, or press Remove token to use Claude Code's own sign-in.";

        /// <summary>The places the locator looks, as the not-installed sentences name them.</summary>
        private static string LookedIn(CodingAgentKind agent)
        {
            return agent == CodingAgentKind.Codex
                ? "on PATH, in %APPDATA%\\npm or in WinGet's packages"
                : "on PATH, in %APPDATA%\\npm, in %USERPROFILE%\\.local\\bin, in WinGet's packages or in VS Code's Claude Code extension";
        }

        /// <summary>The card's CLI row when the CLI is not found.</summary>
        internal static string NotInstalledRow(CodingAgentKind agent)
        {
            return "✗ " + CodingAgents.ProductName(agent) + " is not installed (no " + CodingAgents.ExecutableName(agent) + " " +
                   LookedIn(agent) + ").";
        }

        /// <summary>Where a binary came from, for the card's CLI row, when that is not the CLI's own installer.</summary>
        internal static string WherePhrase(CliSource source)
        {
            if (source == CliSource.WinGet) return "installed with WinGet";
            if (source == CliSource.VsCodeExtension) return "the copy inside VS Code's Claude Code extension";
            return "";
        }

        /// <summary>The log's word for a source.</summary>
        internal static string SourceWord(CliSource source)
        {
            switch (source)
            {
                case CliSource.Npm: return "npm";
                case CliSource.NativeInstaller: return "native";
                case CliSource.WinGet: return "winget";
                case CliSource.VsCodeExtension: return "vscode";
                default: return "path";
            }
        }

        /// <summary>Update CLI's answer for an install another program updates, or null when the CLI's own update is the
        /// right one.</summary>
        internal static string UpdatedElsewhere(CodingAgentKind agent, CliSource source)
        {
            string product = CodingAgents.ProductName(agent);
            if (source == CliSource.WinGet)
                return "⚠ This " + product + " was installed with WinGet, which updates it: run winget upgrade " +
                       (agent == CodingAgentKind.Codex ? CodingAgentLocator.CodexWinGetId : CodingAgentLocator.ClaudeWinGetId) +
                       " in a terminal while no " + product + " is running (Windows locks a running program).";
            if (source == CliSource.VsCodeExtension)
                return "⚠ This Claude Code is the copy inside VS Code's Claude Code extension, which VS Code updates with the extension. Nothing was run.";
            return null;
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
                    // A tick on a typed token says which token it tested and that Validate kept it (1.3.3; ValidateAsync
                    // says why it saves): on 2026-10-07 the owner's 1.3.1 answered two Validates with a tick on a typed
                    // token while a web address was what it had saved, and 1.3.2's "press Apply" could not be followed.
                    // The model is the one that ANSWERED and the effort the one it ran at (RanOn).
                    string ranOn = RanOn(a);
                    return "✓ " + product + (a.Version.Length > 0 ? " " + a.Version : "") + " answered in " +
                           (a.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s" +
                           (ranOn.Length > 0 ? " " + ranOn : "") + "." +
                           (!a.UsedUnsavedToken ? ""
                               : a.TypedTokenSaved ? " That was the token typed in this card, and it is now saved: no Apply needed."
                               : " That was the token typed in this card, but it could not be saved" +
                                 (a.TypedTokenSaveError.Length > 0 ? ": " + a.TypedTokenSaveError : ".") + " Press Validate again.");
                case CliOutcome.NotInstalled:
                    return "✗ " + product + " is not installed: there is no " + CodingAgents.ExecutableName(agent) + " " +
                           LookedIn(agent) + " (a linked or network folder is not trusted). Install it, then press Validate again.";
                case CliOutcome.TokenUnreadable:
                    return TokenUnreadableSentence;
                case CliOutcome.TokenNotAToken:
                    return TokenNotATokenSentence;
                case CliOutcome.NotSignedIn:
                case CliOutcome.SignInExpired when a.UsedSavedToken:
                    // The CLI's own words go last, in brackets: on a machine whose organisation requires remote managed
                    // settings they are about those settings, and the fix is still the token.
                    if (a.UsedSavedToken)
                        return "✗ Claude Code refused the sign-in token in this card. Make a new one with claude setup-token and paste it in, or press Remove token to use Claude Code's own sign-in." +
                               (string.IsNullOrEmpty(a.Said) ? "" : " (It said: " + a.Said + ")");
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
                    // Named by the model asked for when none answered (Claude Code's refusal carries no model of its own),
                    // and a model the user chose is theirs to change, so the sentence says where (lane feature/cli-model-effort).
                    string refusedModel = a.Model.Length > 0 ? a.Model : a.RequestedModel;
                    string refused = "✗ " + product + " refused " + (refusedModel.Length > 0 ? "the model " + refusedModel : "its default model") + said;
                    // Words about the effort send the user to the effort row first, the automatic pick's refusal included
                    // (F17): "choose another model" alone pointed at the wrong setting.
                    if (CodingAgentCli.RefusalNamesEffort(a.Said, a.Effort))
                        return refused.TrimEnd('.', ' ') + ". Its words are about the effort (" + a.Effort +
                               "): choose another effort, or another model, in the CLI card, then press Validate again.";
                    return a.RequestedModel.Length == 0
                        ? refused
                        : refused.TrimEnd('.', ' ') + ". Choose another model in the CLI card, then press Validate again.";
                case CliOutcome.ChoiceRefused:
                    return "✗ " + product + " was not started: " + (a.ChoiceProblem.Length > 0 ? a.ChoiceProblem : "its model or effort setting is not one this module runs it with") +
                           ". Choose again in the CLI card and press Apply.";
                case CliOutcome.ModelCannotSee:
                    return "✗ " + product + "'s model " + a.Model + " takes no images (its own catalog says so), so the screenshot was not sent. " +
                           "Choose a model that takes images, or Automatic, in the CLI card, or turn Use vision off.";
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

        /// <summary>
        /// <see cref="Describe(CodingAgentKind, CliAnswer, TimeSpan?)"/> for a module whose default model for this CLI is
        /// <paramref name="moduleDefaultModel"/> ("" for none: Claude Code's default, Codex's automatic pick). When the CLI
        /// refused that very model, the answer ends with a sentence saying it is the module's default and what else the
        /// card offers (review finding F14): an install that never chose moves to the default ("existing installs move to
        /// the new defaults", the owner, 2026-10-09), and an organisation or a plan that does not serve it would otherwise
        /// stop every call with nothing saying the model was not one the user picked. Judged by the value, because once an
        /// Apply has saved the default it cannot be told from a choice; a user who chose it hears a true sentence. Not
        /// said when the refusal's words are about the effort (RefusalNamesEffort), which Describe already points at.
        /// </summary>
        internal static string Describe(CodingAgentKind agent, CliAnswer answer, TimeSpan? timeout, string moduleDefaultModel)
        {
            string said = Describe(agent, answer, timeout);
            string fallback = (moduleDefaultModel ?? "").Trim();
            if (answer == null || answer.Outcome != CliOutcome.ModelRefused || fallback.Length == 0 ||
                !string.Equals(answer.RequestedModel, fallback, StringComparison.Ordinal) ||
                CodingAgentCli.RefusalNamesEffort(answer.Said, answer.Effort))
                return said;
            return said + " The model " + CodingAgentCli.Displayable(fallback) + " is this module's default, and an organisation or a plan " +
                   "can withhold a model: choose another one, or " +
                   (agent == CodingAgentKind.Codex ? CodexAutomaticChoice : "Claude Code's default") + ", in the CLI card.";
        }

        /// <summary>The Codex model row's word for the runner's automatic pick, as both modules label it.</summary>
        private const string CodexAutomaticChoice = "Automatic";
    }
}
