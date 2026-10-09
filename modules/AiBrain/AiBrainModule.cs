using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.Modules;
using DesktopAICompanion.ModuleKit;   // EmbeddedResources
using DesktopAICompanion.CodingAgent;   // the coding-agent CLI runner (shared/CodingAgentCli, lane feature/cli-backend)

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// The AI-brain module: the optional, off-by-default screen-commentary LLM. It owns the "ask about my screen"
    /// flow through host services: a drop responder at priority 10 that outranks Fortunes on the host's global
    /// drop schedule (the module's own idle loop went in 1.2.3), a poke responder (the text path, the one exception
    /// to vision-for-every-remark), a global hotkey, two tray rows and the "AI Brain" options pane, plus the
    /// emotion->animation reaction. The brain lifecycle (generation/supersede, prepare/retire) is
    /// <see cref="AiSessionManager"/>; settings and the DPAPI-scoped keys are the module's own store
    /// (engine/AiSettings). OFF by default (its own AiBrainEnabled), so a fresh install does nothing until enabled
    /// from the tray or the pane. (This summary described an idle-commentary loop and "no tray/Options UI yet" long
    /// after both had changed, RA-054.)
    /// </summary>
    public sealed class AiBrainModule : IModule
    {
        private IHost _host;
        private SynchronizationContext _ui;                 // captured on the UI thread in Init
        private readonly AiSessionManager _session = new AiSessionManager();
        /// <summary>The session, for the module self-test only: it asserts what a pane action does to the LIVE
        /// brain (R-015).</summary>
        internal AiSessionManager SessionForDiagnostics { get { return _session; } }
        private AiSettings _settings;
        private CancellationTokenSource _lifetime = new CancellationTokenSource();
        private IDisposable _dropResponder;
        private IDisposable _pokeResponder;
        private IDisposable _hotkey;
        private Action<bool> _fullscreenChanged;
        // Remembrance's busy flag is READ AT USE (RemembrancePhase) and never subscribed to, so there is no handler field
        // here and nothing for Shutdown to detach. These two are what a reading leaves behind. The raw value last
        // logged as malformed or stale, so the same value read again at the next decision is not logged again (UI
        // thread only); and whether the last reading was busy, for the cloud+local composite's fallover, which runs on a
        // pool thread where the flag itself is not read (volatile for that reason).
        private string _remembranceLoggedValue;
        private volatile bool _remembranceBusyAtLastRead;
        private ICompanion _lastPet;                              // most-recently-seen pet (screen-context anchor)
        // Still load-bearing without the old idle timer: it keeps a hotkey ask and a drop that land within
        // 30s of each other from becoming two answers in a row.
        private DateTime _lastInteractionUtc = DateTime.MinValue;

        // Model-picker dropdowns: explicit-refresh-only caches (no TTL - populated by the "Refresh ...
        // models" actions, empty until the user clicks one) and the retained SettingField objects the pane's
        // Schema holds, so a refresh can mutate .Options IN PLACE on those same objects (the only way a
        // PaneAction.ReloadPaneAfter rebuild picks up a fresh list - see RefreshModelFieldOptions).
        private readonly List<ModelListing> _localModels = new List<ModelListing>();
        private readonly List<ModelListing> _cloudModels = new List<ModelListing>();
        // Maps a displayed dropdown LABEL back to its underlying model id for the current pane session.
        // A label can carry a size prefix and/or an uncensored suffix (see FormatModelLabel), so recovering
        // the id needs this lookup rather than a fixed string pattern; every label FormatModelLabel produces
        // (both for Load's current-value and for each listed model) registers itself here first, and Load
        // always runs before Save can be called, so a lookup here always succeeds for anything the user
        // could have actually picked from a dropdown.
        private readonly Dictionary<string, string> _modelIdByLabel = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Guards <see cref="_localModels"/>, <see cref="_cloudModels"/> and <see cref="_modelIdByLabel"/>.
        ///
        /// The two "Refresh ... models" pane actions can be in flight together -- the host disables only
        /// the button that was clicked -- and both resume on a POOL thread after their
        /// ConfigureAwait(false), then Clear/AddRange one list and call RefreshModelFieldOptions, which
        /// reads BOTH lists and writes the label dictionary through FormatModelLabel. Concurrently that is
        /// a List being rebuilt while another thread enumerates it and two threads writing one
        /// Dictionary: torn dropdowns at best, a corrupted dictionary or InvalidOperationException at
        /// worst. LoadPaneValues reads the same three from the UI thread, so the race does not even need
        /// two refreshes -- one refresh plus a pane open is enough.
        ///
        /// Monitor is reentrant, so the coarse lock in each refresh action and the fine ones in
        /// FormatModelLabel / ResolveModelId nest safely on the same thread. The coarse one is what makes
        /// "replace the list, then rebuild the options from it" atomic rather than merely thread-safe in
        /// pieces.
        /// </summary>
        private readonly object _modelsLock = new object();

        /// <summary>
        /// The VRAM residency sentence, refreshed OFF the UI thread.
        ///
        /// MEASURED 2026-09-27 with the same mechanism OllamaClient uses (HttpClient, 2s deadline,
        /// literal 127.0.0.1 so no DNS): server running 5-56 ms, server REFUSED 2005-2008 ms, host
        /// unreachable 2010 ms. A refused localhost connection does not fail fast -- it burns the whole
        /// deadline and comes back as TaskCanceledException. So the old synchronous call did not cost
        /// 2 seconds in a rare hung-server case; it cost 2 seconds for every user who does not have
        /// Ollama running, on every options-pane open, and three pane actions rebuild the pane.
        ///
        /// Hence: never block the pane on it. Serve the last answer, refresh behind, and say "checking"
        /// the first time rather than freezing while we find out.
        /// </summary>
        private volatile string _vramResident;
        private long _vramStampTicks;
        private int _vramProbing;
        private const int VramFreshnessSeconds = 5;
        private SettingField _textModelField;
        private SettingField _visionModelField;
        private SettingField _cloudTextModelField;
        private SettingField _cloudVisionModelField;

        private static readonly string[] NoAnimation = new string[0];

        // The coding-agent CLI runner (lane feature/cli-backend, 1.3.0): ONE per module instance, built in Init over this
        // module's own storage folder and shared by every brain the session builds, both auditions and the card's Validate
        // and Update CLI, so one CLI call at a time holds for the whole module (the runner's gate).
        private CodingAgentCli _cli;

        /// <summary>Test seam: the runner Init uses instead of building its own, set before Init (one over a fake CLI).
        /// Null in the shipped module. Same shape as BrainFactoryForDiagnostics.</summary>
        internal CodingAgentCli CliForDiagnostics;

        /// <summary>The runner Init built or was handed, for the self-test.</summary>
        internal CodingAgentCli CliRunnerForDiagnostics { get { return _cli; } }

        // "Brain runs on", the owner's approved four (mockup AB2, 2026-10-06), and the conditions the cards that only one
        // engine reads carry, as CardEnabledWhen on each card's first field since lane feature/layout-aibrain (host 1.4.0;
        // EnabledWhen's syntax, several values with '|'). The host compares the option TEXT on screen, so these are labels;
        // the two CLI labels are CodingAgents.ChoiceLabel's, and the self-test pins that they agree. Local and cloud are
        // two options, so the cloud dropdown no longer carries "(none)".
        internal const string BrainRunsOnLocal = "Local model";
        internal const string BrainRunsOnCloud = "Cloud provider";
        internal const string CliCardGroup = "Coding-agent CLI";
        /// <summary>Live for the local model and for a cloud provider, whose fallback is the local slot.</summary>
        internal const string OnLocalOrCloud = "brainRunsOn=" + BrainRunsOnLocal + "|" + BrainRunsOnCloud;
        internal const string OnCloud = "brainRunsOn=" + BrainRunsOnCloud;
        internal const string OnCliOnly = "brainRunsOn=Claude Code CLI|Codex CLI";
        /// <summary>The sign-in token row: Claude Code's alone, so live only while Claude Code is the CLI on screen.</summary>
        internal const string OnClaudeCliOnly = "brainRunsOn=Claude Code CLI";

        /// <summary>What Use vision leaves to OCR, on every engine (AiBrain.AskAboutScreenAsync: the vision path sends the
        /// screenshot and runs no OCR; the poke passes allowVision=false; ChooseModel falls back to text for a model that
        /// cannot see), and what a blank OCR engine resolves to (ResolveTesseract, then RunOcrAsync's Windows fallback).</summary>
        internal const string OcrWhenLine =
            "Only for text: with Use vision on, the poke reaction and any remark whose model cannot see images; with it off, every remark. " +
            "Tesseract is optional: left blank, an installed one is used if found, and otherwise Windows' own OCR.";

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "aibrain",
            Name = "AI Brain",
            Version = "1.5.0",   // 1.5.0: a model and an effort for each coding-agent CLI (the owner, 2026-10-09: cheap
                                 //        calls on a small model, heavy ones on a large one). Until now every Claude Code
                                 //        call ran on whatever the user's own setup resolved, which on the owner's machine
                                 //        was Opus at xhigh: one identical one-word call cost $0.0407 there against
                                 //        $0.0011 on Haiku (Claude Code's own list-price estimate, measured that day). The
                                 //        shared runner (shared/CodingAgentCli) now gives Claude Code --model with an alias
                                 //        (haiku, sonnet or opus, never a full id: the user's two organisations serve
                                 //        different catalogs) and --effort on every call, and Codex the chosen slug and
                                 //        effort in place of its automatic pick and its fixed low; turns the advisor tool
                                 //        and the title request off on each Claude Code call and takes off
                                 //        CLAUDE_CODE_EFFORT_LEVEL, which outranks --effort; refuses any other value before
                                 //        anything starts; reads the model that ANSWERED from the stream (asked for haiku
                                 //        and answered on another is said, and is not an error); calls Claude Code's
                                 //        wrong-model and organisation-restriction words a refused model; leaves the
                                 //        automatic pick alone when a model the user chose is refused; and keeps Codex's
                                 //        own list of models beside the pick, for the card. Remembrance 2.3.0 carries the
                                 //        same runner.
                                 // 1.4.0: a fullscreen app stands AI Brain down only where it matters (the owner,
                                 //        2026-10-07: Ctrl+Alt+P did nothing on Claude Code while a game ran on ANOTHER
                                 //        monitor, and AgentFlow kept talking; "if it's on a monitor that does NOT have a
                                 //        fullscreen app it is not suppressed, if it's on a fullscreen in-use monitor it
                                 //        auto-suppresses"). Two questions where there was one. Can the user see the
                                 //        companion? Asked of the host per companion (ICompanionStandDown, host 1.5.0):
                                 //        a companion that moved to a free monitor answers, on every engine, and one that
                                 //        hides with nowhere to go does not, since the host would hold its line until the
                                 //        game ends. Does the local model need the graphics card a game is using? Only on
                                 //        the local slot, which is what the stand-down switch now governs, beside
                                 //        Remembrance's in the Local server card: there it still releases the model and
                                 //        declines, the hotkey now SAYS why to a companion in view, and a cloud slot's
                                 //        fallback to the local model is held back while the game runs. Claude Code,
                                 //        Codex and a cloud provider load nothing on this machine and go ahead.
                                 // 1.3.3: Validate keeps a typed sign-in token that answers (the owner, 2026-10-07: "apply
                                 //        did not become clickable after validate was pressed"). Validate rebuilds the pane,
                                 //        the host's rebuild empties a secret's box, and an Apply then had nothing to save,
                                 //        so 1.3.2's "press Apply to keep it" could not be followed. A token that does not
                                 //        answer is not saved. In shared/CodingAgentCli; Remembrance 2.1.3 carries it too.
                                 // 1.3.2: "What it sees" says Tesseract is optional (owner, 2026-10-07): the OCR engine's
                                 //        blank box says "(optional)" where it said "(auto-detect)", and a new row says when
                                 //        OCR reads the screen at all (with Use vision on, only the poke reaction and a remark
                                 //        whose model cannot see) and that Windows' own OCR stands in without Tesseract.
                                 //        That part is wording: nothing reads the screen differently. And the shared CLI runner's
                                 //        token fixes (the owner's Update CLI failed on 2026-10-07 with a web address
                                 //        saved as the token): Update CLI and the version check no longer carry the
                                 //        token, the token box refuses a web address and any character no token holds,
                                 //        a saved value that is not a token is said as that and never sent, and
                                 //        Validate's tick on a typed token says it is not saved yet.
                                 // 1.3.1: finds Claude Code and Codex where 1.3.0 said "not installed" (the owner's other
                                 //        workstation, 2026-10-07): a WinGet install (its package folder, which WinGet
                                 //        reaches through a link the trust rules refuse, or puts on a PATH a host started
                                 //        earlier never saw), the PATH saved since the app started, and the Claude Code
                                 //        inside VS Code's extension; Update CLI leaves WinGet's and VS Code's copies to
                                 //        them and says how. The CLI card gains an optional Claude sign-in token (owner
                                 //        request, same day): a `claude setup-token` token this module's Claude Code calls
                                 //        run on instead of Claude Code's own sign-in, handed to those children alone,
                                 //        sealed with DPAPI in the runner's folder, tested by Validate before Apply, and
                                 //        deleted by Remove token. All of it in shared/CodingAgentCli, so Remembrance
                                 //        2.1.1 carries the same.
                                 // 1.3.0: can run on a coding-agent CLI instead of a model server (owner decision,
                                 //        2026-10-06). "Brain runs on" chooses "Local model", "Cloud provider", "Claude
                                 //        Code CLI" or "Codex CLI"; a settings file written before this has no CLI key
                                 //        and reads its old slot, and the cloud dropdown's "(none)" left the list (the
                                 //        radio says whether the cloud is used; the provider is remembered while it is
                                 //        not). With a CLI chosen EVERY call goes through it: the drop, the poke, the
                                 //        hotkey, the tray row and both auditions, text and vision alike, with the persona
                                 //        as the CLI's short system prompt (Claude Code) or instructions file (Codex), and
                                 //        choosing it is the consent. Claude Code runs on its default model; Codex on the
                                 //        model its own `codex debug models` lists first (the lowest priority among the
                                 //        listed ones, image-capable for a vision turn), picked once per installed CLI
                                 //        version. Nothing loads into Ollama on that path, so Remembrance's busy flag stands
                                 //        nothing down there. The pane is the owner's approved mockup (AB2), built on host
                                 //        1.4.0's settings primitives, so MinHostVersion rises to 1.4.0: a full-width Status
                                 //        card first (on or off, what it runs on, vision, the last remark), the AI brain and
                                 //        Coding-agent CLI cards pinned, then Persona, Triggers (the Ask hotkey and the
                                 //        fullscreen stand-down, which a CLI still reads), "What it sees" (Use vision beside
                                 //        the OCR engine, now a path field whose own Browse replaces "Choose OCR engine…"),
                                 //        and the three engine cards, Fallback folded into Cloud provider. A card whose
                                 //        engine is not chosen greys WHOLE, its buttons with it, and says why under its
                                 //        title (CardEnabledWhen), and so does the CLI card off a CLI; Local provider has
                                 //        a Test connection of its own, the local slot's, since the one in Cloud provider
                                 //        greys on the local model. The CLI card names the CLI, its version and
                                 //        model, the account it is signed into, the last Validate and what goes through it;
                                 //        Validate makes one tiny call and names what is wrong in plain words; Update CLI
                                 //        runs the CLI's own update, refuses while a call runs, and then removes the npm
                                 //        staging folder an update leaves beside a package it could not delete. The buttons
                                 //        that serve one engine also refuse in words on another. No call leaves a session
                                 //        behind (--no-session-persistence, --ephemeral). The runner is shared with
                                 //        Remembrance (shared/CodingAgentCli). Lane feature/cli-backend, and the layout lane
                                 //        feature/layout-aibrain; their decisions are under those headings in
                                 //        docs/DESIGN-REGISTER.md.
                                 // 1.2.0: stands down while Remembrance runs a local model, so a remark cannot
                                 //        evict the model of a transcription or a summary in progress (owner
                                 //        request, 2026-10-02). Remembrance publishes `remembrance.busy` on the
                                 //        host's shared context; AI Brain reads it at each decision with
                                 //        ReadContext, never through a subscription, and makes it a second,
                                 //        release-free reason beside the fullscreen guard, which is unchanged.
                                 //        While the flag is set and fresh (`at` within 8 hours) and the new
                                 //        switch "Stand down while Remembrance is transcribing or summarizing" is
                                 //        on, the drop and the poke decline on the local slot so Fortunes
                                 //        speaks, the hotkey and the tray row are declined with one short
                                 //        spoken line (the companion stays on screen, so silence would read as
                                 //        broken) and a log line under a category of their own, the Status row
                                 //        names the phase, the auditions
                                 //        and a local Test connection answer that Remembrance is using the local
                                 //        model, and an Apply warms nothing and evicts nothing. Nothing is
                                 //        released for this reason, and while it holds a fullscreen app's
                                 //        release waits too, because AI Brain's model can be the very model
                                 //        Remembrance is using. A cloud slot's requests go ahead; only its
                                 //        fallback to the local slot waits. A malformed or stale value fails
                                 //        open and is logged once. Lane feature/aibrain-standdown; its decisions
                                 //        are under that heading in docs/DESIGN-REGISTER.md.
                                 // 1.1.14: the 2026-09-29 audit campaign, lane fix/aibrain. Vision, when on,
                                 //         applies to every remark including the unprompted drop (owner
                                 //         decision, BUG-010): the code stood, the label and comments changed,
                                 //         and the module self-test pins the drop's routing. The hotkey and
                                 //         the tray row honour the fullscreen stand-down. LaunchProcess is
                                 //         declared. The settings store reads a BOM'd or UTF-16 file, keeps a
                                 //         rejected primary as ai-settings.corrupt-*.json, says in the log
                                 //         when it could not load, restores an empty endpoint or hotkey, and
                                 //         has no %TEMP% fallback root. A cloud slot needs a model before the
                                 //         brain builds; the cloud+local composite can be enumerated, so a bad
                                 //         cloud id is re-validated (BUG-002); a vision model the marker list
                                 //         does not know is used, not refused; an answered 401 is reachable
                                 //         and described as a rejected key, and the log files answered
                                 //         statuses as http-<n>; model listings read under their own 8 MiB
                                 //         cap; the failure line names the model sent and the host built
                                 //         against; a substitution is announced before any capture. The
                                 //         reachability probes are bounded by their own 10 s deadline and
                                 //         the composite runs both legs at once; the warm-up carries the
                                 //         residency's keep_alive; an Apply that leaves the backend as it
                                 //         was keeps the model resident; the timed-out dispose releases the
                                 //         model before disposing the backend; a reply that completed is
                                 //         never discarded to a deadline that fired during the read; the
                                 //         audition holds its model between samples and warms nothing; a
                                 //         fallover picks the local model by whether the request carries an
                                 //         image. The brain factory reads a private copy of the settings
                                 //         taken on the UI thread, and a factory that throws is logged; the
                                 //         inventory is taken when the backend is first seen up and again
                                 //         when it comes back, and the pane's Refresh reaches the live
                                 //         brain; the OCR engine resolves once per brain and the PATH walk
                                 //         throws nothing; Windows OCR is fed the capture's pixels, not a
                                 //         PNG round trip; the audition guard is interlocked. Lane
                                 //         fix/deadcode (same version, zip not yet republished): the seven
                                 //         Fortunes-era settings fields, the local provider presets and the
                                 //         "ollama" branches behind them, the legacy-root read that could
                                 //         never run, the second downscale and the OCR display name nothing
                                 //         showed are gone; an unknown provider id changes nothing; the
                                 //         audition pane shows each sample's latency; a live-vision audition
                                 //         carries one instruction; the probe names a throwing group and
                                 //         survives a renamed reflected field. Every item is
                                 //         dispositioned in BACKLOG.md; decisions under `#### fix/aibrain`
                                 //         and `#### fix/deadcode` in docs/DESIGN-REGISTER.md.
                                 //         Round 2 (2026-09-30, the early regression review): UseVision joined
                                 //         the backend fingerprint; model listings run under their own 30 s
                                 //         bound and an empty re-list keeps the previous inventory; "Test
                                 //         OCR" and "Choose OCR engine" reset the LIVE brain's Tesseract
                                 //         cache; the substitution loop excludes a reported-blind model as
                                 //         the gate does; a cloud primary never substitutes a missing model
                                 //         (R-011, R-014, R-015, R-020, R-022).
                                 //         Phase 8 burn-down (2026-09-30, lane burn/aibrain, same version): the
                                 //         audition, Test OCR and Test connection read the pane's PENDING values
                                 //         through PaneAction.InvokeWithPendingAsync, a member host 1.2.5 added,
                                 //         so MinHostVersion is RAISED from 1.1.0 to 1.2.5 in this version (the
                                 //         publish round must carry it into the catalog entry); a typed
                                 //         key that cannot be stored refuses the save and says why; the pane shows
                                 //         whether the brain started and the tray row hides while it has not; a save refused for its key
                                 //         leaves the live settings untouched (N-burn-aibrain-02, 2026-10-01);
                                 //         every explicit-ask refusal is logged; a model refresh names an answered
                                 //         401; the audition applies the cloud no-substitution rule, evicts within
                                 //         2 s and cuts samples at code points; the release names the substitute a
                                 //         turn sent and the local model the composite warmed by PATH; the composite
                                 //         cannot enumerate while its primary is down and an unknown inventory is
                                 //         re-listed; the session owns the backend fingerprint; a chosen OCR path
                                 //         survives a walk in flight; a held settings file is not corruption, the
                                 //         load warning names the backup's fate and a failed default write, a save
                                 //         over a missing file keeps unknown keys, the residency token is clamped
                                 //         and the active-slot snapshot cannot be saved; the probe has no SKIP line
                                 //         and guards every check (RA-054 to RA-087, R-012, R-013, R-016 to R-019,
                                 //         R-021, R-023).
                                 // 1.1.13: the emotion reaction reached 18/54, 35/54, 8/54, 8/54 and
                                 //         8/54 companions. "thinking" fires on EVERY ask, so on 46 of
                                 //         54 it silently did nothing -- the eSheep-era names it used
                                 //         are absent from the 32 converted shimeji. Original names
                                 //         kept FIRST, so nothing that worked changes; the tail is a
                                 //         fallback. Now 42/42/39/40/43. Also dropped a write-only
                                 //         _generation field.  // 1.1.12: exposes SelfTest on the module class, so --module-selftest runs
                                 //         AiEngineProbe through the convention the gate and CI use.
                                 // 1.1.11: the options pane no longer freezes for 2s when Ollama is not
                                 //         running. The VRAM line was a synchronous network call on the UI
                                 //         thread; a REFUSED localhost connection burns the full 2s deadline
                                 //         rather than failing fast, so that was the common case, not the
                                 //         worst one. It is served from cache and refreshed behind now.
                                 // 1.1.10: settings saves from the UI are bounded at 1.5s instead of the 10s
                                 //         cross-session budget, so a hung second instance can no longer freeze
                                 //         the settings window; and the tray AI toggle now says when a save
                                 //         failed instead of silently reverting on the next launch.
                                 // 1.1.9: the local and cloud "Refresh models" actions raced. Both resume on a
                                 //        pool thread and rebuilt the shared model lists and label map with no
                                 //        synchronisation, and a pane open read the same three from the UI
                                 //        thread. All three are guarded now.   // 1.1.8: the fullscreen release now unloads the model it actually loaded. The local
                                 //        leg of the fallback ignored its argument and always passed the TEXT model,
                                 //        so after a cloud-primary fallback had loaded local llava:13b (~8 GB) the
                                 //        vision model kept its VRAM for the whole game.
                                 // 1.1.7: dead code out, no behaviour change. DisposeWithin was byte-for-byte
                                 //        DisposeForDiagnostics with no callers, and CredentialIdentity had none
                                 //        either while decrypting the API key on every call.
                                 // 1.1.6: "Test connection" reports whether it got an ANSWER; a saved vision
                                 //        model the filter rejects is still offered; and the drop and poke
                                 //        responders no longer claim a turn they declined.
                                 // 1.1.4: the persona audition can run against the REAL screen ("5 about
                                 //        my screen"), which is the more honest test. Needed a real
                                 //        anti-repetition mechanism first: the prompt's "do not repeat
                                 //        anything you have said recently" was INERT, because each
                                 //        sample is an independent single-turn request and nothing told
                                 //        the model what it had already said. Five asks about one
                                 //        unchanging screen came back near-identical; now each prompt
                                 //        carries the previous remarks, bounded to the last four.
                                 // 1.1.3: a failed AI turn now says WHICH failure it was (backend
                                 //        availability transitions, request latency/attempts/parse
                                 //        outcome, and the OCR engine plus every silent `return ""` in
                                 //        the tesseract path), and the Persona card gained "Show me 5
                                 //        examples" -- five remarks across five canned scenes, so a
                                 //        disposition is judged by its voice. Reports the model that
                                 //        answered, because ChooseModel substitutes silently.
                                 // 1.1.2: republished so the bundled ModuleKit.dll no longer carries the
                                 //        maintainer's absolute build path (Contracts + ModuleKit moved to
                                 //        DebugType=embedded). NO functional change here; the bump exists
                                 //        because the catalog offers an update by VERSION, so without it the
                                 //        cleaned payload would only ever reach new installs.
                                 // 1.1.1: the shipped TextModel default was llama3.1:8b, a model that was
                                 //        never in the measured comparison and that the Readme tables do
                                 //        not mention, so a fresh install pointed its text model at
                                 //        something the user very likely did not have. Now gemma3:4b,
                                 //        matching VisionModel so one download serves both jobs and the
                                 //        backend can keep a single model resident. The value was
                                 //        duplicated across five sites; all are now in step.
                                 // 1.1.0: NEW: screen reactions capture the FOREGROUND WINDOW rather than the
                                 //        whole monitor, and the prompt names what else is open. Needs host
                                 //        1.1.0 for ScreenContext.ForegroundWindowBounds / .Windows.
                                 // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
                                 //        rollback -- the previous line below is the higher number, and
                                 //        every module restarts its numbering here alongside the app.
                                 // 1.4.0: NEW: "Stand down while a fullscreen app is running" -- releases the
                                 //        model and lets free fortunes speak instead, so a local model cannot
                                 //        claim VRAM beside a game that already owns it. Releases on the
                                 //        TRANSITION (host FullscreenChanged), because a model loaded before
                                 //        the game started is not helped by merely declining to load. Needs
                                 //        host 1.9.9 for the fullscreen predicate + event.
                                 // 1.3.0: NEW: "Model residency" -- one choice for how long a local model may
                                 //        hold VRAM, defaulting to unloading after each remark. Replaces
                                 //        "Preload model on launch", which could contradict a short eject
                                 //        window (it pinned keep_alive to 10m) and needed a paragraph of
                                 //        explanation; one setting cannot disagree with itself. The pane now
                                 //        reads GET /api/ps and reports what is ACTUALLY resident -- model,
                                 //        GB, seconds to eviction -- instead of printing a documented default
                                 //        that OLLAMA_KEEP_ALIVE can override on the user's own machine.
                                 // 1.2.3: unprompted commentary now rides the HOST's global "Randomly drop a
                                 //        fortune / insight" schedule. The module's own idle timer and its
                                 //        three settings (Idle commentary / min / max) are gone: two
                                 //        schedules were driving the same model into the same bubble with no
                                 //        shared cooldown. The Ask hotkey is unchanged.
                                 // 1.2.2: payload refresh only, no behaviour change -- the bundled ModuleKit
                                 //        was 4 commits stale. See the note on Fortunes 1.2.4.
                                 // 1.2.1: the tray icon is the blue brain glyph, not the retired red-X
                                 //        disable-ai.png. 0f3def7 changed the source but never republished the
                                 //        payload, so every download still carried the old icon -- and because
                                 //        the version did not move, no update was ever offered. This bump is
                                 //        what actually ships that change.
                                 // 1.2.0: the question, the thinking cue and the answer all belong to ONE pet
                                 //        (and an answer whose pet has gone is dropped, not handed to another)
                                 // 1.1.2: helpers come from DesktopAICompanion.ModuleKit instead of local copies
                                 // 1.1.1: OCR output is decoded as UTF-8 (was the ANSI codepage -> "asÂ®")
                                 // 1.1.0: reads the screen with Windows' built-in OCR when Tesseract is absent
            // 1.9.9 is the host that added IHost.IsFullscreenActive + FullscreenChanged, which the
            // stand-down-for-a-game guard needs. Declaring it means an older host refuses this module with a
            // legible reason instead of loading it and failing at a missing member. (1.5.0 added the pet-aware
            // responders and IsCompanionAlive, which this also uses.)
            // Raised to 1.1.0 because the screen-reaction path now reads
            // ScreenContext.ForegroundWindowBounds and .Windows, both introduced by that host. This is the
            // first module to raise its floor since the 1.0.0 rebase flattened every one of them, so it is
            // also the first to exercise the sequencing rule: do NOT publish this to the catalog until host
            // 1.1.0 has shipped, or the catalog offers users a module their host correctly refuses.
            // Raised to 1.2.5 on 2026-09-30 (RA-055): the three pane actions set PaneAction.InvokeWithPendingAsync,
            // which host 1.2.5 introduced, and an older host would fail at the missing member while running Init.
            // The shipping host is 1.2.6, so the sequencing rule above is already satisfied.
            // Raised to 1.4.0 on 2026-10-07 (lane feature/layout-aibrain): the pane sets SettingField.CardEnabledWhen
            // on the four cards one engine reads, and declares the OCR engine as SettingKind.FilePath with
            // SettingField.FileExtensions and SettingField.EmptyHint, all members host 1.4.0 introduced; an older host
            // would fail at the missing setters while running Init. The sequencing rule applies again: the catalog
            // entry's minHostVersion moves to 1.4.0 with this publish, and not before host 1.4.0 ships.
            // Raised to 1.5.0 on 2026-10-07 (lane feature/fullscreen-per-monitor): the stand-down asks the host whether
            // the companion is in view through ICompanionStandDown, which host 1.5.0 introduced. Contracts is the host's
            // shared assembly, so on an older host the interface type itself is missing and the method naming it fails
            // to compile at its first call; the floor makes that host refuse the module with a legible reason instead.
            // Publish only after app 1.5.0 has shipped.
            MinHostVersion = "1.5.0",
            // LaunchProcess: this module starts `ollama serve` (engine\OllamaClient.TryStartServer) and runs
            // tesseract.exe as a child for OCR (engine\AiBrain.RunOcrAsync), and no flag had ever said so on
            // the consent screen (F226: the flag shipped in host 1.2.5 naming this module as a holder, and no
            // module declared it). A disclosure, not a gate, like every flag in the enum. Declaring an existing
            // flag needs no MinHostVersion raise.
            Permissions = ModulePermissions.Speech | ModulePermissions.Animation |
                          ModulePermissions.ScreenContext | ModulePermissions.Network |
                          ModulePermissions.Hotkey | ModulePermissions.Storage |
                          ModulePermissions.LaunchProcess,
        };

        public void Init(IHost host)
        {
            _host = host;
            _ui = SynchronizationContext.Current;   // the WinForms UI-thread context (host loads modules there)

            // Give the engine a way into the diagnostic log. Until this existed the whole of
            // modules/AiBrain had ONE IHost.Log call, in a module that fails by returning null on
            // purpose -- so every failure looked like "nothing to say" (BUG-002). Set before anything
            // else here so a fault during the rest of Init is itself recorded.
            AiBrain.LogSink = delegate(string line)
            {
                IHost h = _host;
                if (h == null) return;
                try { h.Log(Info.Id, line); } catch { }
            };

            try
            {
                IModuleStorage storage = host.GetStorage("aibrain");
                if (storage != null && !string.IsNullOrEmpty(storage.DataDirectory))
                {
                    AiPaths.SetRoot(storage.DataDirectory);
                    MigrateFromBaseIfNeeded(storage.DataDirectory);   // one-time, non-destructive
                }
                _settings = AiSettings.Load();
                // Said out loud. Load never throws, and until 2026-09-29 it also could not SAY anything: a
                // corrupt primary recovered from the backup, a lock that timed out and a host that gave no
                // storage all produced a settings object and nothing in the file SUPPORT.md asks for (F096,
                // F098, N-gates-02). One line, once, at the start of the module's life.
                if (!string.IsNullOrEmpty(_settings.LoadWarning))
                    try { host.Log(Info.Id, "settings: " + _settings.LoadWarning); } catch { }
            }
            catch { _settings = new AiSettings(); }

            // The coding-agent CLI runner over this module's own folder (lane feature/cli-backend). With no storage it
            // answers every call "no data folder" rather than running a CLI from anywhere else. Its log lines are outcome
            // words, exit codes, durations and model ids, never a prompt, an answer or an account.
            string cliScratch = null;
            try
            {
                IModuleStorage cliStorage = host.GetStorage("aibrain");
                if (cliStorage != null && !string.IsNullOrEmpty(cliStorage.DataDirectory))
                    cliScratch = Path.Combine(cliStorage.DataDirectory, "cli");
            }
            catch { cliScratch = null; }
            _cli = CliForDiagnostics ?? new CodingAgentCli(cliScratch, delegate(string line)
            {
                IHost h = _host;
                if (h == null) return;
                try { h.Log(Info.Id, line); } catch { }
            });

            // Track the current pet for the screen-context anchor; harmless while the brain is off.
            host.CompanionSpawned += OnPetSeen;
            host.CompanionLanded += OnPetSeen;
            host.CompanionPoked += OnPetPoked;
            // Outrank Fortunes (priority 0) on the shared drop: when the brain is on, the drop is an AI
            // insight and this responder handles it; when off, it declines and Fortunes speaks instead.
            _dropResponder = host.RegisterCompanionDropResponder(10, OnDrop);
            // Same for the first poke of a session, except the user's "Trigger Speech" preference can
            // override this ordering entirely (or randomize it).
            _pokeResponder = host.RegisterCompanionPokeResponder(Info.Id, 10, OnPokeReaction);

            // A game STARTING is the only moment we can hand VRAM back before it is needed rather than after,
            // so this is an event rather than something checked at our own next tick (which could be 15
            // minutes away, long after the game has failed to get the memory).
            _fullscreenChanged = OnFullscreenChanged;
            host.FullscreenChanged += _fullscreenChanged;

            // Contribute the AI tray items (S5a): the host merges these into the tray, re-evaluating
            // DynamicText/Visible on each open. This is the module's own enable/ask entry point now that the
            // base's AI tray items are gone (closes the S4 accept-the-gap).
            host.AddTrayItems(new[]
            {
                new TrayItem
                {
                    Label = "Enable AI", Group = 50, Order = 0,
                    DynamicText = delegate { return (_settings != null && _settings.AiBrainEnabled) ? "Disable AI" : "Enable AI"; },
                    Click = ToggleEnabled,
                    IconPng = LoadIconResource("ai-brain.png"),
                },
                new TrayItem
                {
                    Label = "Ask about my screen", Group = 50, Order = 1,
                    // Offered only while the brain is actually STARTED. On the stored switch alone the row stayed
                    // visible, and inert, after CanUse had refused the configuration (a cloud slot with no model,
                    // consent missing, a bad endpoint): the one signal was a log line (R-013). The toggle above keeps
                    // reading the switch, because the switch is what it flips; the pane's Status row says why.
                    Visible = delegate { return _session.Enabled; },
                    Click = delegate { Ask(null, true); },
                    IconPng = LoadIconResource("monitor.png"),
                },
            });

            // Model-picker dropdowns: build the retained SettingField objects first (so a later refresh can
            // mutate .Options on these SAME objects) and seed their Options from whatever's already saved
            // (the caches are empty pre-refresh, so this is just the safety-net current-value entry - see
            // RefreshModelFieldOptions/BuildModelOptions). No EnabledWhen of their own: each greys with its CARD (host
            // 1.4.0 CardEnabledWhen on the card's first field, below), the local pair unless the brain runs on the local
            // model or a cloud provider (whose fallback is the local slot), the cloud pair unless it runs on the cloud.
            _textModelField = new SettingField { Id = "textModel", Label = "Local text model", Kind = SettingKind.Enum, Group = "Local provider" };
            _visionModelField = new SettingField { Id = "visionModel", Label = "Local vision model", Kind = SettingKind.Enum, Group = "Local provider" };
            _cloudTextModelField = new SettingField { Id = "cloudTextModel", Label = "Cloud text model", Kind = SettingKind.Enum, Group = "Cloud provider" };
            _cloudVisionModelField = new SettingField { Id = "cloudVisionModel", Label = "Cloud vision model", Kind = SettingKind.Enum, Group = "Cloud provider" };
            RefreshModelFieldOptions();

            // Contribute the AI config as a schema-driven OptionsPane (S5b): the host renders it in the WPF
            // settings window and round-trips values through this Load/Save, which persist to the module's
            // own AiSettings store. Exercises every field kind (bool/int/text/enum/secret).
            //
            // CARDS IN THE ORDER OF THE OWNER'S APPROVED MOCKUP (AB2, 2026-10-06; lanes feature/cli-backend and
            // feature/layout-aibrain): a full-width Status card pinned first; the AI brain card (the switch and "Brain runs
            // on") and the Coding-agent CLI card pinned beside it; then the cards that apply to every engine (Persona,
            // Triggers, and "What it sees", which holds Use vision beside the OCR engine because a screenshot goes to
            // whichever engine runs); then the three engine cards. Fallback folds into Cloud provider.
            //
            // WHOLE-CARD GREYING (host 1.4.0, SettingField.CardEnabledWhen, read from a card's FIRST field). The three
            // engine cards and the CLI card each carry ONE condition on their first field, and no row in them carries an
            // EnabledWhen of its own: the host greys every row and every button of the card together and puts "Not used
            // while “Brain runs on” is <engine>." under the title, and one string per card cannot drift out of step the
            // way a copy on each row could. The values are still collected and saved unchanged, so a greyed card keeps
            // what the user set. The cards nothing gates (Status, AI brain, Persona, Triggers, What it sees) set none, and
            // a field the module still reads on every engine must sit in one of them: inside a greyed card the host
            // disables the whole body, so no row there can stay live (OptionsWindow.DressCard).
            host.AddOptionsPane(new OptionsPane
            {
                Title = "AI Brain",
                Schema = new[]
                {
                    // Whether the brain STARTED, and why not when it did not: CanUse's refusal used to live in the
                    // diagnostic log alone while Save reported success (R-013). See BrainStatusLine. Since 1.3.0 its own
                    // card, full width and first (owner, 2026-10-06), and one line that says what the brain runs on, whether
                    // it sees the screen, and how the last remark went (StatusRowLine).
                    new SettingField { Id = "brainStatus", Label = "Status", Kind = SettingKind.Info, Group = "Status", FullWidth = true, PinTop = true },
                    new SettingField { Id = "enabled", Label = "Enable AI brain", Kind = SettingKind.Bool, Group = "AI brain", PinTop = true },
                    // The engine, one choice of four (feature/cli-backend). The radio never greys; everything else that only
                    // one engine reads does, so a field is never live while nothing reads it.
                    new SettingField { Id = "brainRunsOn", Label = "Brain runs on", Kind = SettingKind.Radio, Options = BrainRunsOnLabels(), Group = "AI brain" },
                    // ---- the coding-agent CLI card: the rows in the mockup's order, then Validate and Update CLI ----
                    // Greyed whole, its two buttons included, unless a CLI is chosen on screen (the mockup's cw).
                    new SettingField { Id = "cliName", Label = "CLI", Kind = SettingKind.Info, Group = CliCardGroup, PinTop = true, CardEnabledWhen = OnCliOnly },
                    new SettingField { Id = "cliAccount", Label = "Signed in as", Kind = SettingKind.Info, Group = CliCardGroup },
                    // The optional sign-in token (owner request, 2026-10-07; aibrain 1.3.1): a `claude setup-token` token that
                    // this module's Claude Code calls run on instead of Claude Code's own sign-in. Sealed in the CLI runner's
                    // folder, never in this module's settings, and Load hands back only "set"; blank keeps the saved one, and
                    // Remove token below deletes it. Its own EnabledWhen inside the card, because Codex never reads it.
                    new SettingField { Id = "cliToken", Label = "Claude sign-in token (optional)", Kind = SettingKind.Secret, Group = CliCardGroup, EnabledWhen = OnClaudeCliOnly },
                    new SettingField { Id = "cliStatus", Label = "Status", Kind = SettingKind.Info, Group = CliCardGroup },
                    new SettingField { Id = "cliSends", Label = "Goes through it", Kind = SettingKind.Info, Group = CliCardGroup },
                    new SettingField { Id = "companionName", Label = "Companion name", Kind = SettingKind.Text, Group = "Persona" },
                    new SettingField { Id = "userName", Label = "Your name (optional)", Kind = SettingKind.Text, Group = "Persona" },
                    new SettingField { Id = "disposition", Label = "Disposition", Kind = SettingKind.Enum, Options = DispositionNames(), Group = "Persona" },
                    // Unprompted commentary has no controls here on purpose: it rides the host's global
                    // "Randomly drop a fortune / insight" schedule in Preferences via OnDrop. The hotkey is
                    // the only trigger this module still owns, because it is the only one that is its own.
                    new SettingField { Id = "hotkey", Label = "Ask hotkey", Kind = SettingKind.Text, Group = "Triggers" },
                    // Vision, when on, applies to EVERY remark about the screen: the hotkey, the tray row and
                    // the unprompted drop alike, so the companion reacts to what is actually on screen rather
                    // than to OCR text. Owner decision 2026-09-29 (BUG-010): the label used to say "on explicit
                    // asks" while OnDrop had always allowed vision; the code stood and the words changed. The
                    // poke reaction is the one exception and stays on the text path (see OnPokeReaction). It sits
                    // in "What it sees", not in Local provider, since 1.3.0: every engine takes the screenshot.
                    new SettingField { Id = "useVision", Label = "Use vision (send a screenshot, not OCR text, with each remark)", Kind = SettingKind.Bool, Group = "What it sees" },
                    // Screen reading uses this OCR engine whenever vision is off, the chosen model cannot see, or the
                    // remark is the poke reaction (always text). Blank = search the usual install locations, then PATH,
                    // then Windows' own OCR. A PATH FIELD since lane feature/layout-aibrain (host 1.4.0, the FilePath
                    // kind): the 177 DIP editor column showed only the tail of a real path ("…ract-OCR\tesseract.exe"),
                    // the field shows the file's name with its folder under it and the whole path on hover, and its own
                    // Browse (the host's Open dialog on .exe, the filter "Choose OCR engine…" used) replaces that button.
                    // It stores exactly what the Text kind stored, the full path, so no settings file changes. The label
                    // names the file to pick, because the host titles the Browse dialog with it; AB2's "(used when vision
                    // is off)" is not true of the poke. The EmptyHint said "(auto-detect)" until 1.3.2, which beside a
                    // "Get Tesseract…" button read as a requirement (the owner, 2026-10-07: "dont i NOT need tesseract if
                    // using CLI?"). It is not one on any engine, so the box says so in the few characters it shows (about
                    // 125 px of a 13-character hint), and the row below says when OCR runs at all.
                    new SettingField
                    {
                        Id = "tesseractPath",
                        Label = "OCR engine (tesseract.exe)",
                        Kind = SettingKind.FilePath,
                        FileExtensions = new[] { "exe" },
                        EmptyHint = "(optional)",
                        Group = "What it sees",
                    },
                    // When OCR reads the screen at all (1.3.2), true on every engine and whatever is saved, so it is one
                    // fixed sentence (OcrWhenLine) rather than a line that would describe the saved Use vision while the
                    // box above shows an unapplied one.
                    new SettingField { Id = "ocrWhen", Label = "When OCR is used", Kind = SettingKind.Info, Group = "What it sees" },
                    // Local provider (always available; defaults to Ollama but can instead speak the
                    // generic OpenAI-compatible /v1 protocol for llama.cpp/LM Studio/other local servers). The card is
                    // live on the local model and on the cloud, whose fallback is this slot.
                    new SettingField { Id = "localBackendKind", Label = "Local backend", Kind = SettingKind.Enum, Options = LocalBackendKindLabels(), Group = "Local provider", CardEnabledWhen = OnLocalOrCloud },
                    new SettingField { Id = "endpoint", Label = "Local endpoint (base URL)", Kind = SettingKind.Text, Group = "Local provider" },
                    _textModelField,
                    _visionModelField,
                    // The fullscreen stand-down, in Local provider since 1.4.0 (lane feature/fullscreen-per-monitor). It is
                    // the graphics-card guard and nothing else now: whether the user can SEE the companion is the host's
                    // per-companion stand-down, asked on every engine with no switch (ICompanionStandDown), so this governs
                    // only what loads a model on this machine: the local slot, and the cloud slot's fallback to it. That is
                    // exactly where this card is live (OnLocalOrCloud), so on a CLI, where 1.3.x still read it, it greys
                    // with the card and nothing reads it. Not Local server: that card is Ollama's, and llama.cpp or LM
                    // Studio behind the OpenAI-compatible kind fill the graphics card just the same. It sat in Triggers
                    // from lane feature/layout-aibrain until here, because a CLI read it then.
                    new SettingField
                    {
                        Id = "standDownFullscreen",
                        Label = "Stand down while a fullscreen app is running (releases VRAM; the Ask hotkey says why, fortunes speak instead)",
                        Kind = SettingKind.Bool,
                        Group = "Local provider",
                    },
                    // Local server (Ollama only): live where the Local provider card is, for the same reason.
                    new SettingField { Id = "autoStart", Label = "Start Ollama automatically", Kind = SettingKind.Bool, Group = "Local server (Ollama only)", CardEnabledWhen = OnLocalOrCloud },
                    // The stand-down's second reason, worded as the owner asked for it (2026-10-02). The Status row says
                    // when it applies. A CLI loads nothing into Ollama, so nothing stands down for Remembrance there, and
                    // this one greys with its card where its sibling above does not.
                    new SettingField
                    {
                        Id = "standDownRemembrance",
                        Label = "Stand down while Remembrance is transcribing or summarizing",
                        Kind = SettingKind.Bool,
                        Group = "Local server (Ollama only)",
                    },
                    // ONE choice, not a "preload" switch plus an eject window that could contradict it.
                    // Defaults to unloading: the module holds VRAM only for a remark it has already made.
                    new SettingField
                    {
                        Id = "residency",
                        Label = "Model residency (how long it may hold VRAM)",
                        Kind = SettingKind.Enum,
                        Options = ResidencyLabels(),
                        Group = "Local server (Ollama only)",
                    },
                    // Deliberately NOT a sentence claiming "the default is 5 minutes". It is 5 minutes in
                    // Ollama's docs, but OLLAMA_KEEP_ALIVE overrides it server-wide, so the claim would be
                    // wrong on exactly the machines whose owner had tuned it. This reads /api/ps and reports
                    // what is actually resident, which is the honest version of "say whatever the default is".
                    new SettingField { Id = "vramStatus", Label = "In VRAM right now", Kind = SettingKind.Info, Group = "Local server (Ollama only)" },
                    // Cloud provider (primary when "Brain runs on" says so), live on the cloud alone. Its dropdown no longer
                    // offers "(none)": the radio says whether the cloud is used, and the dropdown which provider
                    // (feature/cli-backend, AB1/AB2).
                    new SettingField { Id = "cloudProvider", Label = "Cloud provider", Kind = SettingKind.Enum, Options = CloudProviderLabels(), Group = "Cloud provider", CardEnabledWhen = OnCloud },
                    new SettingField { Id = "cloudEndpoint", Label = "Cloud base URL", Kind = SettingKind.Text, Group = "Cloud provider" },
                    new SettingField { Id = "apiKey", Label = "API key (cloud providers)", Kind = SettingKind.Secret, Group = "Cloud provider" },
                    _cloudTextModelField,
                    _cloudVisionModelField,
                    new SettingField { Id = "cloudConsent", Label = "Allow cloud data sharing", Kind = SettingKind.Bool, Group = "Cloud provider" },
                    // Fallback: the runtime half is FallbackBackend, built by CreateBrain whenever a cloud provider is
                    // primary and this is on (F094; this line said "a later change" long after it shipped, RA-054). Folded
                    // into the Cloud provider card in 1.3.0 (AB2): it is read only when the cloud is primary.
                    new SettingField { Id = "useLocalFallback", Label = "Use local provider as fallback", Kind = SettingKind.Bool, Group = "Cloud provider" },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    // Two actions rather than one action plus a mode switch, and deliberately: pressing one of two
                    // buttons is inherently exclusive and needs no Apply, and the "which screen" choice is not a
                    // setting worth persisting. (The pending-values reason this used to give went with RA-055: the
                    // actions below now DO see what is on screen.)
                    //
                    // Both delegates on the three actions that read settings. InvokeWithPendingAsync (host 1.2.5)
                    // is handed the pane as it stands and wins when set, so the audition, Test OCR and Test
                    // connection answer about what the user is looking at rather than about the last Apply; the
                    // saved-values delegate stays as the documented fallback shape (RA-055, adopting the member
                    // F227 recorded as having no adopter). MinHostVersion is 1.2.5 for this.
                    new PaneAction { Label = "Show me 5 examples", InvokeAsync = PreviewDispositionAsync, InvokeWithPendingAsync = PreviewDispositionPendingAsync, Group = "Persona" },
                    new PaneAction { Label = "5 about my screen", InvokeAsync = PreviewDispositionLiveAsync, InvokeWithPendingAsync = PreviewDispositionLivePendingAsync, Group = "Persona" },
                    // The CLI card's two buttons act on the CLI chosen ON SCREEN. Both rebuild the pane after, so the CLI,
                    // Signed in as and Status rows show what the press just learnt (feature/cli-backend). Off a CLI they grey
                    // with their card, and each still answers PickACliFirst if a press reaches it anyway.
                    new PaneAction { Label = "Validate", InvokeWithPendingAsync = ValidateCliPendingAsync, Group = CliCardGroup, ReloadPaneAfter = true },
                    new PaneAction { Label = "Update CLI", InvokeWithPendingAsync = UpdateCliPendingAsync, Group = CliCardGroup, ReloadPaneAfter = true },
                    // Deletes the saved sign-in token at once (there is nothing to Apply), so Claude Code runs on its own
                    // sign-in again; the pane rebuilds so Signed in as says so.
                    new PaneAction { Label = "Remove token", InvokeAsync = RemoveCliTokenAsync, Group = CliCardGroup, ReloadPaneAfter = true },
                    // No "Choose OCR engine…" since lane feature/layout-aibrain: the OCR engine field's own Browse opens the
                    // same host dialog on .exe. That button also SAVED the pick behind Apply's back; the field makes it an
                    // unsaved edit like any other, Test OCR (pending-aware) tests it before Apply, and Apply rebuilds the
                    // live brain, which resolves the engine afresh from the path it is built with.
                    new PaneAction { Label = "Get Tesseract…", InvokeAsync = GetTesseractAsync, Group = "What it sees" },
                    new PaneAction { Label = "Test OCR", InvokeAsync = TestOcrAsync, InvokeWithPendingAsync = TestOcrPendingAsync, Group = "What it sees" },
                    // The engine cards' buttons grey with their card (CardEnabledWhen, host 1.4.0), so none can be pressed
                    // for an engine it does not serve. Each still refuses in plain words if a press reaches it anyway, the
                    // refusals feature/cli-backend wrote when a PaneAction could not grey at all, kept as the second line of
                    // defence: the card's gate and the button's refusal are two readings of the same rule.
                    new PaneAction { Label = "Refresh local models", InvokeAsync = RefreshLocalModelsAsync, InvokeWithPendingAsync = RefreshLocalModelsPendingAsync, Group = "Local provider", ReloadPaneAfter = true },
                    // The LOCAL slot's own Test connection (lane feature/layout-aibrain). The one Test connection used to sit
                    // in Cloud provider and test whichever slot was active, so on the local model it was the local slot's
                    // only test; AB2 greys that card whole off the cloud, which would have taken the test from every local
                    // install. So each card now tests its own slot: this one the local model (on the cloud, its fallback),
                    // the Cloud provider card's the cloud. The host keys a button's result by card and label, so two buttons
                    // may share the label.
                    new PaneAction { Label = "Test connection", InvokeAsync = TestLocalConnectionAsync, InvokeWithPendingAsync = TestLocalConnectionPendingAsync, Group = "Local provider" },
                    new PaneAction { Label = "Test connection", InvokeAsync = TestConnectionAsync, InvokeWithPendingAsync = TestConnectionPendingAsync, Group = "Cloud provider" },
                    new PaneAction { Label = "Refresh cloud models", InvokeAsync = RefreshCloudModelsAsync, InvokeWithPendingAsync = RefreshCloudModelsPendingAsync, Group = "Cloud provider", ReloadPaneAfter = true },
                },
            });

            ApplyState();
        }

        /// <summary>
        /// Guard against a second audition starting while one is running. Five sequential generations is
        /// the one action in this pane long enough for an impatient user to press twice, and the backlog
        /// called that out: pressing it repeatedly while comparing characters would otherwise queue 25
        /// generations against one local model. The press arrives on the UI thread, but the CLEAR does not:
        /// the audition awaits with ConfigureAwait(false), so its finally runs on a pool thread. An earlier
        /// comment here claimed a single-thread model the code had stopped following (F062); the flag is an
        /// int taken and released with interlocked operations, so the check and the set are one step and a
        /// second press gets an explanation instead of silently doubling the work.
        /// </summary>
        private int _auditionRunning;

        /// <summary>Claim the audition slot; false when one is already running.</summary>
        internal bool TryBeginAudition()
        {
            return Interlocked.CompareExchange(ref _auditionRunning, 1, 0) == 0;
        }

        /// <summary>Release the audition slot, from whichever thread the audition ended on.</summary>
        internal void EndAudition()
        {
            Interlocked.Exchange(ref _auditionRunning, 0);
        }

        /// <summary>
        /// "Show me 5 examples" for the Persona card: generate one remark per canned scene so a
        /// disposition can be judged by how it SOUNDS rather than by its name.
        ///
        /// Two entry points per button. <c>PaneAction.InvokeAsync</c> reads the SAVED settings and says so in
        /// the header; <c>PaneAction.InvokeWithPendingAsync</c> (host 1.2.5) is handed the pane as it stands and
        /// auditions THAT, so the user hears the disposition they just picked in the dropdown before committing
        /// to it. The rule this comment used to record, "a module cannot see a pending field value", was true
        /// until host 1.2.5 added the member; F227 noted it had no adopter, and this is the adoption (RA-055). The
        /// host prefers the pending-aware delegate when both are set. The pending copy is detached (CloneForBrain),
        /// so nothing an audition does with it can reach the settings file.
        /// </summary>
        private Task<string> PreviewDispositionAsync()
        {
            return PreviewDispositionAsync(false);
        }

        private Task<string> PreviewDispositionPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            return PreviewDispositionAsync(false, pending);
        }

        /// <summary>
        /// "5 about my screen": the same audition, but against the REAL screen instead of canned scenes.
        ///
        /// The more honest of the two, and the maintainer's point when asking for it: it is literally
        /// what the companion would say about what you are doing, carrying every quirk a real turn has
        /// (the capture clamped to the COMPANION's monitor, the OCR engine that is actually installed,
        /// the vision downscale). Needs a companion on screen, because ScreenContext is anchored to one
        /// and MonitorBounds means "the monitor the companion is on".
        /// </summary>
        private Task<string> PreviewDispositionLiveAsync()
        {
            return PreviewDispositionAsync(true);
        }

        private Task<string> PreviewDispositionLivePendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            return PreviewDispositionAsync(true, pending);
        }

        // internal, not private: the module self-test presses it with the slot held (F062).
        internal Task<string> PreviewDispositionAsync(bool live)
        {
            return PreviewDispositionAsync(live, null);
        }

        /// <param name="pending">The pane's on-screen values, or null to audition the saved settings (RA-055).</param>
        internal async Task<string> PreviewDispositionAsync(bool live, IReadOnlyDictionary<string, string> pending)
        {
            if (!TryBeginAudition()) return "⏳ Already generating examples — give it a moment.";
            try { return await RunAuditionAsync(live, pending).ConfigureAwait(false); }
            finally { EndAudition(); }
        }

        /// <summary>
        /// The settings as the pane shows them right now: a detached copy of the live instance with the on-screen
        /// values applied through the same mapping Save uses, so an action asked about "what is on screen" answers
        /// about exactly what Apply would store. It cannot be saved (CloneForBrain marks it), so nothing an action
        /// does with it reaches the file. Null, with the reason, when a typed key could not be stored on the copy
        /// (no provider or endpoint to scope it to, or DPAPI refused): the action would otherwise run on a key
        /// other than the one typed (RA-055).
        /// </summary>
        private AiSettings PendingSettings(AiSettings live, IReadOnlyDictionary<string, string> values, out string error)
        {
            AiSettings pending = live.CloneForBrain();
            if (!ApplyPaneValues(pending, values, out error)) return null;
            return pending;
        }

        private async Task<string> RunAuditionAsync(bool live, IReadOnlyDictionary<string, string> pending)
        {
            AiSettings saved = _settings;
            if (saved == null) return "✗ No settings.";
            string pendingError = null;
            AiSettings s = pending == null ? saved : PendingSettings(saved, pending, out pendingError);
            if (s == null) return "✗ " + pendingError;
            // Remembrance running a local model (lane feature/aibrain-standdown, Addendum 1): an audition on the LOCAL slot
            // is five chats to a model Remembrance may be using on the same server, so nothing is sent. A cloud audition
            // goes ahead with its local fallback held back (the hold CreateBrain wires in below). Read here, on the UI
            // thread, before the first await, and read FIRST, whatever the slot: the reading is what that hold consults.
            if (RemembrancePhase() != null && IsLocalSlot(s)) return RemembranceBusyAnswer;

            string dispositionName = DispositionNameForId(s.Disposition);

            // A live audition reads the screen through a companion handle, so one has to be out. Said
            // plainly rather than falling back to canned scenes, which would silently answer a different
            // question than the one the button asks.
            ScreenContext liveContext = null;
            string petZone = null;
            if (live)
            {
                IHost host = _host;
                ICompanion pet = _lastPet;
                if (host == null) return "✗ No host.";
                if (pet == null || !host.IsCompanionAlive(pet))
                    return "✗ No companion on screen to look through — add one from the tray, then try again.";
                try { liveContext = host.CaptureScreenContext(pet); }
                catch (Exception ex) { return "✗ Couldn't read the screen: " + ex.Message; }
                if (liveContext == null)
                    return "✗ Couldn't read the screen (the companion went away).";
                petZone = liveContext.WindowUnderCompanion;
            }

            // A cloud provider means five billable requests per press, and the persona is exactly the
            // thing a user presses repeatedly while comparing. CreateBrain already refuses a non-local
            // endpoint without CloudDataConsent, so the consent gate is inherited rather than
            // reimplemented; what is added here is telling the user the COST before they spend it.
            // A CLI audition has no endpoint to check and no consent switch to read: choosing the CLI is the user's
            // statement that their remarks go through it (owner decision, 2026-10-06), and the header counts the calls
            // (feature/cli-backend).
            CodingAgentKind auditionCli = IsCliSlot(s) ? CodingAgents.FromId(s.CliBackend) : CodingAgentKind.None;
            string endpoint = SelectedEndpoint(s);
            string normalized = "";
            string endpointError;
            if (auditionCli == CodingAgentKind.None && !AiEndpointPolicy.TryNormalize(endpoint, out normalized, out endpointError))
                return "✗ " + endpointError;
            bool cloud = auditionCli == CodingAgentKind.None && !AiEndpointPolicy.IsLoopbackEndpoint(normalized);
            if (cloud && !s.CloudDataConsent)
                return "✗ Approve cloud data sharing first — an audition sends " +
                       DispositionScenes.All.Length + " requests to your provider.";

            // Per sample rather than for the run, so one stuck generation costs one example instead of
            // all five. Floor of 20s because a local 4B model answering a text prompt is ~400ms but a
            // cold first load is seconds, and a ceiling so a misconfigured timeout cannot pin the pane.
            TimeSpan perSample = TimeSpan.FromSeconds(Math.Max(20, Math.Min(90, s.TimeoutSeconds)));
            // A whole-run bound as well: PaneAction has no cancel affordance, so "cancellable" here means
            // "cannot run forever". The engine also honours this token between samples.
            TimeSpan whole = TimeSpan.FromSeconds(perSample.TotalSeconds * DispositionScenes.All.Length + 15);

            try
            {
                AiBrain brain;
                // A short positive keep_alive for the audition's Ollama client under "unload" residency: five
                // back-to-back samples with keep_alive:0 raced Ollama's eviction and could pay up to four extra
                // cold loads (F070). Evicted explicitly when the run ends, below. Built through the seam when the
                // self-test set one, so the three decisions this method makes (the keep_alive window, no warm-up,
                // the eviction at the end) are pinned where they are MADE and not only on the helpers they call
                // (RA-073).
                int? keepAlive = AuditionKeepAliveSeconds(s);
                Func<AiSettings, int?, AiBrain> factory = AuditionBrainFactoryForDiagnostics;
                try { brain = factory != null ? factory(s, keepAlive) : CreateBrain(s, keepAlive, LocalFallbackHold, _cli); }
                catch (Exception ex) { return "✗ " + ex.Message; }

                using (brain)
                using (var run = new CancellationTokenSource(whole))
                {
                    // Fills the model inventory, so ChooseModel can tell "configured model is missing"
                    // from "backend is down" instead of producing five identical silences (BUG-002). No warm-up:
                    // the canned samples run on the text model and the live ones load whatever they use on their
                    // first sample; PrepareAsync's warm-up belongs to the launch routine (F063).
                    if (!await brain.PrepareAsync(run.Token, false).ConfigureAwait(false))
                        return auditionCli != CodingAgentKind.None
                            ? CodingAgentCliText.Describe(auditionCli, new CliAnswer { Outcome = CliOutcome.NotInstalled }, null)
                            : "✗ Not reachable at " + normalized + " — start the provider and try again.";

                    DispositionAudition audition =
                        await brain.SampleDispositionAsync(
                            s.Disposition, liveContext, petZone, perSample, run.Token)
                            .ConfigureAwait(false);
                    // The VRAM back now, under "unload": the samples held the model for a minute between them, and
                    // the residency's promise is that it is gone after the remark. Bounded like a retirement's
                    // eviction (RA-056): on a cloud composite this round-trips to local Ollama for a model the canned
                    // samples never loaded, about 4 s per request against nothing listening on localhost, and it
                    // used to run under the chat deadline (120 s by default) with the pane waiting on it.
                    if (string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase))
                        await UnloadWithinAsync(brain, AuditionUnloadBudget, run.Token).ConfigureAwait(false);
                    return FormatAudition(dispositionName, audition, cloud, live, pending != null,
                        auditionCli == CodingAgentKind.None ? null : CodingAgents.ChoiceLabel(auditionCli));
                }
            }
            catch (OperationCanceledException)
            {
                return "✗ Gave up after " + (int)whole.TotalSeconds + "s — the provider is too slow for an audition.";
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>
        /// Render an audition. Names the persona (so previewing the saved one is obvious), labels each
        /// sample with the scene that produced it (so a character that only works on code is visible), and
        /// reports failures per sample rather than collapsing the run into one error.
        /// </summary>
        private static string FormatAudition(
            string dispositionName, DispositionAudition audition, bool cloud, bool live, bool pendingValues, string cliLabel = null)
        {
            IReadOnlyList<DispositionSample> samples = audition == null ? null : audition.Samples;
            if (samples == null || samples.Count == 0)
                return "✗ No examples were produced.";

            var sb = new StringBuilder();
            // Which settings this is an audition OF. The pending-aware press names the dropdown's choice, unsaved;
            // the saved-values press says so, as it always has (RA-055).
            sb.Append(dispositionName).Append(pendingValues ? " — as shown in the pane, not yet applied" : " — as currently saved");
            if (!string.IsNullOrEmpty(audition.ModelUsed)) sb.Append(" · ").Append(audition.ModelUsed);
            sb.Append(live ? " · your real screen" : " · made-up scenes");
            if (cloud) sb.Append(" · ").Append(samples.Count).Append(" cloud requests");
            else if (cliLabel != null) sb.Append(" · ").Append(samples.Count).Append(" ").Append(cliLabel).Append(" calls");
            sb.Append(pendingValues
                ? "\nHit Apply to keep it, or pick another and press again."
                : "\nChange the dropdown and hit Apply to audition a different one.");
            // A substituted model has to be said out loud here. ChooseModel swaps in whatever is
            // available when the configured id is missing, and a user auditioning personas on a model
            // they never picked would blame the character for the model's output.
            if (!string.IsNullOrEmpty(audition.Advisory))
                sb.Append("\n⚠ ").Append(audition.Advisory);

            int ok = 0;
            int index = 0;
            foreach (DispositionSample sample in samples)
            {
                index++;
                sb.Append("\n\n");
                // Canned mode labels each remark with the scene that produced it, because the scene is
                // what varies. Live mode asks about ONE screen, so the label would be the same five
                // times; numbering is what carries information there.
                sb.Append("• [").Append(live ? index.ToString(CultureInfo.InvariantCulture) : sample.SceneLabel).Append("] ");
                if (sample.Ok)
                {
                    ok++;
                    sb.Append(Ellipsize(sample.Text, 220));
                }
                else
                {
                    sb.Append("✗ ").Append(sample.Error);
                }
                // The sample's own latency, so "the provider is too slow" (the timeout message's guess) can be
                // read off the pane: one cold-load count followed by four warm sub-second ones is the residency
                // story in a line. The diagnostic log carried this per request; the pane threw it away (F076).
                sb.Append(" · ")
                  .Append((sample.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" s");
            }
            if (ok == 0)
                sb.Append("\n\nNothing came back. The diagnostic log records why, under Modules.");
            return sb.ToString();
        }

        // internal, not private: the module self-test cuts a sample that ends in an emoji through it (RA-057).
        internal static string Ellipsize(string value, int maximum)
        {
            string one = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            // At a code-point boundary, the helper the OCR text goes through two lines from the other cut: a sample
            // is model text and can end in an emoji, and a plain Substring at a UTF-16 index split the pair and put a
            // lone surrogate (U+FFFD on screen) in the pane (RA-057).
            return one.Length > maximum ? UnicodeTextProgress.TruncateAtCodePointBoundary(one, maximum) + "…" : one;
        }

        /// <summary>Test-connection actions for the WPF pane, one per slot card since lane feature/layout-aibrain: build the
        /// card's slot's backend from the settings, probe availability + a tiny chat, and report a status line. Async so
        /// the pane stays responsive. Saved and pending entry points, as for the audition (RA-055): the pending one tests
        /// the provider, endpoint, key and model on screen. The Cloud provider card's tests the cloud slot and refuses off
        /// the cloud, as Refresh cloud models does; the Local provider card's tests the local slot, which on the cloud is
        /// the fallback. There used to be one button, in Cloud provider, testing whichever slot was active.</summary>
        private Task<string> TestConnectionAsync()
        {
            return TestConnectionAsync(_settings, false);
        }

        private Task<string> TestConnectionPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            return TestConnectionPendingAsync(pending, false);
        }

        private Task<string> TestLocalConnectionAsync()
        {
            return TestConnectionAsync(_settings, true);
        }

        private Task<string> TestLocalConnectionPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            return TestConnectionPendingAsync(pending, true);
        }

        private Task<string> TestConnectionPendingAsync(IReadOnlyDictionary<string, string> pending, bool localSlot)
        {
            AiSettings saved = _settings;
            if (saved == null) return Task.FromResult("No settings.");
            string error;
            AiSettings s = PendingSettings(saved, pending, out error);
            return s == null ? Task.FromResult("✗ " + error) : TestConnectionAsync(s, localSlot);
        }

        /// <param name="localSlot">True for the Local provider card's button, which tests the local slot; false for the
        /// Cloud provider card's, which tests the cloud slot.</param>
        private async Task<string> TestConnectionAsync(AiSettings s, bool localSlot)
        {
            if (s == null) return "No settings.";
            // On a CLI there is no endpoint here to test; the CLI card's Validate tests the CLI (feature/cli-backend).
            if (IsCliSlot(s)) return NotUsedWhileRunningOn(CodingAgents.ChoiceLabel(CodingAgents.FromId(s.CliBackend)));
            // The Cloud provider card's button serves the cloud slot alone: on the local model its card is greyed, and a
            // press that reaches it anyway is refused the way Refresh cloud models refuses (lane feature/layout-aibrain).
            if (!localSlot && IsLocalSlot(s)) return NotUsedWhileRunningOn(BrainRunsOnLocal);
            // A local Test connection is a chat to the local model (lane feature/aibrain-standdown, Addendum 1), so nothing
            // is sent while Remembrance may be using it, the cloud's fallback test included. A cloud test goes to the cloud
            // alone and is unaffected.
            if (localSlot && RemembrancePhase() != null) return RemembranceBusyAnswer;
            string endpoint = localSlot ? s.Endpoint : s.OpenAiBaseUrl;
            string normalized, err;
            if (!AiEndpointPolicy.TryNormalize(endpoint, out normalized, out err)) return "✗ " + err;
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
                return "✗ Approve cloud data sharing first.";
            try
            {
                TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(120, s.TimeoutSeconds)));
                bool local = localSlot;
                ICompanionBrainBackend backend = local
                    ? BuildLocalBackend(s, normalized, timeout)
                    : (ICompanionBrainBackend)new OpenAiCompatBackend(normalized, s.ApiKey, timeout);
                using (backend)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (!await backend.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false))
                        return "✗ Not reachable at " + normalized;
                    // Test the card's own slot: the cloud model on the Cloud provider card, the local one on Local provider.
                    // A blank cloud model is refused, not defaulted: this used to substitute the LOCAL default
                    // "gemma3:4b" and report the provider's 400 (F101).
                    string activeModel = local ? s.TextModel : s.CloudTextModel;
                    if (string.IsNullOrWhiteSpace(activeModel))
                        return local
                            ? "✗ No local text model is set."
                            : "✗ Pick a cloud text model first (Refresh cloud models, then choose one).";
                    string model = activeModel.Trim();
                    var msgs = new List<ChatMessage> { ChatMessage.System("Reply with OK."), ChatMessage.User("OK?", null) };
                    string reply = await backend.ChatAsync(model, msgs, false, CancellationToken.None).ConfigureAwait(false);
                    sw.Stop();
                    return TestConnectionVerdict(reply, model, sw.ElapsedMilliseconds);
                }
            }
            catch (AiBackendHttpException ex) { return DescribeHttpFailure(ex, normalized); }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>
        /// The pane's line for an answered HTTP failure: the status, the host it came from, the likely cause
        /// for the ones a user can fix themselves, and the provider's own words when it sent any. "AI backend
        /// returned HTTP 401." named neither the host nor the key (F107), and the provider's error.message
        /// ("Insufficient credits", "not a valid model ID") was never read at all (F080). Pane text only; the
        /// diagnostic log carries the status category.
        /// </summary>
        internal static string DescribeHttpFailure(AiBackendHttpException ex, string endpoint)
        {
            string host = AiBrain.DescribeEndpoint(endpoint);
            string cause;
            switch (ex.StatusCode)
            {
                case 401:
                case 403: cause = "the provider rejected the API key"; break;
                case 402: cause = "the provider wants payment or credits on this key"; break;
                case 404: cause = "check the base URL (it usually ends in /v1) and the model id"; break;
                case 429: cause = "the provider is rate-limiting this key"; break;
                default: cause = ex.StatusCode >= 500 ? "the provider is having trouble; try again later" : ""; break;
            }
            string line = "✗ HTTP " + ex.StatusCode.ToString(CultureInfo.InvariantCulture) + " from " + host +
                          (cause.Length > 0 ? ": " + cause : "");
            if (!string.IsNullOrEmpty(ex.ProviderMessage)) line += " (" + ex.ProviderMessage + ")";
            return line;
        }

        // "Choose OCR engine…" (browse to a tesseract.exe the auto-detect missed, save it, test it) left in lane
        // feature/layout-aibrain: the OCR engine is a host 1.4.0 path field now, whose Browse opens the same dialog on
        // .exe and puts the pick in the field as an unsaved edit. Test OCR answers for the pick before Apply, and Apply
        // rebuilds the live brain with it. The button saved behind Apply's back, the one action in this pane that did.

        /// <summary>
        /// "Get Tesseract…": open the official download page. Screen reading works without it (Windows'
        /// built-in engine is the fallback), but Tesseract generally reads dense text better, so this is
        /// how a user finds the upgrade instead of having to know it exists. The standard installer lands
        /// in %ProgramFiles%\Tesseract-OCR, which auto-detect already checks — so after installing, "Test
        /// OCR" just goes green with no path to configure.
        /// </summary>
        private Task<string> GetTesseractAsync()
        {
            IHost host = _host;
            if (host == null) return Task.FromResult("No host.");
            const string url = "https://tesseract-ocr.github.io/tessdoc/Installation.html";
            bool opened = host.OpenLink(Info.Id, url);
            return Task.FromResult(opened
                ? "Opened the Tesseract install guide. Install it, then click Test OCR."
                : ("Couldn't open a browser — see " + url));
        }

        /// <summary>"Test OCR" action: run the OCR self-test (resolve tesseract + read a known image) so a
        /// missing/broken engine surfaces as a red status instead of silently making remarks screen-blind. Saved and
        /// pending entry points (RA-055): the pending one tests the path in the pane's OCR engine field, applied or not.</summary>
        // internal, not private: the module self-test presses it and asserts what it does to the LIVE brain (R-015).
        internal Task<string> TestOcrAsync()
        {
            return TestOcrAsync(_settings ?? new AiSettings());
        }

        private Task<string> TestOcrPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            AiSettings saved = _settings ?? new AiSettings();
            string error;
            AiSettings s = PendingSettings(saved, pending, out error);
            return s == null ? Task.FromResult("✗ " + error) : TestOcrAsync(s);
        }

        private async Task<string> TestOcrAsync(AiSettings s)
        {
            try
            {
                string verdict;
                using (var probe = new AiBrain(null, s))
                    verdict = await probe.SelfTestOcrAsync(CancellationToken.None).ConfigureAwait(false);
                // The probe above is a throwaway. The brain that reads screens lives in the session and cached its
                // own resolution when it was built, so until 2026-09-30 this button went green after an install while
                // every remark kept the engine the live brain had resolved at build time (R-015). Forget that cache,
                // with the path the SAVED settings name: a path browsed to but not applied is tested on the throwaway
                // above and must not re-point the live brain at an engine the settings do not name yet (Apply rebuilds
                // the brain anyway, RA-055), while an engine installed since the brain was built is found at once.
                AiSettings live = _settings;
                _session.ForgetOcrResolution(live != null ? live.TesseractPath : s.TesseractPath);
                return verdict;
            }
            catch (Exception ex) { return "✗ OCR test failed: " + ex.Message; }
        }

        // ---- "Brain runs on" and the coding-agent CLI card (lane feature/cli-backend, 1.3.0) ------------------------

        private const string RunsOnLocalId = "local";
        private const string RunsOnCloudId = "cloud";

        private static readonly string[] BrainRunsOnOptions =
        {
            BrainRunsOnLocal,
            BrainRunsOnCloud,
            "Claude Code CLI",
            "Codex CLI",
        };

        internal static string[] BrainRunsOnLabels()
        {
            return (string[])BrainRunsOnOptions.Clone();
        }

        /// <summary>The radio's option for these settings: a CLI when one is chosen, else the cloud when a provider is
        /// primary, else the local model. A file written before 1.3.0 has no CLI, so it reads its old slot.</summary>
        internal static string BrainRunsOnLabel(AiSettings s)
        {
            if (s == null) return BrainRunsOnLocal;
            CodingAgentKind agent = CodingAgents.FromId(s.CliBackend);
            if (agent != CodingAgentKind.None) return CodingAgents.ChoiceLabel(agent);
            return string.IsNullOrEmpty(s.Provider) ? BrainRunsOnLocal : BrainRunsOnCloud;
        }

        /// <summary>The radio's id for an option's text ("local", "cloud", "claude", "codex"), or null for text that is no
        /// option (the "" an unmatched radio row collects), which then changes nothing: Remembrance's folder-layout rule.</summary>
        internal static string BrainRunsOnIdForLabel(string label)
        {
            string value = (label ?? "").Trim();
            if (string.Equals(value, BrainRunsOnLocal, StringComparison.Ordinal)) return RunsOnLocalId;
            if (string.Equals(value, BrainRunsOnCloud, StringComparison.Ordinal)) return RunsOnCloudId;
            if (string.Equals(value, CodingAgents.ChoiceLabel(CodingAgentKind.Claude), StringComparison.Ordinal)) return CodingAgents.ClaudeId;
            if (string.Equals(value, CodingAgents.ChoiceLabel(CodingAgentKind.Codex), StringComparison.Ordinal)) return CodingAgents.CodexId;
            return null;
        }

        /// <summary>The cloud provider the dropdown names when the cloud is not primary: the one last chosen, kept while
        /// the brain runs elsewhere, so choosing Cloud provider again restores it; OpenAI for an install that never chose.</summary>
        private static string RememberedCloudProvider(AiSettings s)
        {
            string id = CloudProviderIdForLabel(s != null ? s.LastCloudProvider : "");
            return id.Length > 0 ? id : "openai";
        }

        /// <summary>The engine the pane SHOWS (BUG-013's lesson: an action answers about the screen), else the saved one.</summary>
        private string RunsOnOnScreen(IReadOnlyDictionary<string, string> pending)
        {
            string label;
            if (pending != null && pending.TryGetValue("brainRunsOn", out label))
            {
                string id = BrainRunsOnIdForLabel(label);
                if (id != null) return id;
            }
            AiSettings s = _settings;
            return BrainRunsOnIdForLabel(BrainRunsOnLabel(s));
        }

        private CodingAgentKind CliOnScreen(IReadOnlyDictionary<string, string> pending)
        {
            return CodingAgents.FromId(RunsOnOnScreen(pending));
        }

        internal const string PickACliFirst = "⚠ Pick Claude Code CLI or Codex CLI under \"Brain runs on\" first.";

        /// <summary>The plain refusal a button gives while the engine on screen is one it does not serve. Written when a
        /// PaneAction could not grey at all; since host 1.4.0 the button greys with its card (CardEnabledWhen), and this
        /// stays as the second line of defence for a press that reaches the module anyway (lane feature/layout-aibrain).
        /// The card's own line says the same thing in the host's words: Not used while “Brain runs on” is ….</summary>
        internal static string NotUsedWhileRunningOn(string runsOnLabel)
        {
            return "✗ Not used while the brain runs on " + runsOnLabel + ".";
        }

        /// <summary>That refusal for a local- or cloud-only button while a CLI is on screen, or null.</summary>
        internal string CliRefusal(IReadOnlyDictionary<string, string> pending)
        {
            CodingAgentKind agent = CliOnScreen(pending);
            return agent == CodingAgentKind.None ? null : NotUsedWhileRunningOn(CodingAgents.ChoiceLabel(agent));
        }

        /// <summary>Refresh local models serves the local slot, which the cloud's fallback uses too: refused on a CLI only.</summary>
        private Task<string> RefreshLocalModelsPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            string refusal = CliRefusal(pending);
            return refusal != null ? Task.FromResult(refusal) : RefreshLocalModelsAsync();
        }

        /// <summary>Refresh cloud models serves the cloud slot alone: refused unless the cloud is the engine on screen.</summary>
        private Task<string> RefreshCloudModelsPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            string runsOn = RunsOnOnScreen(pending);
            if (!string.Equals(runsOn, RunsOnCloudId, StringComparison.Ordinal))
                return Task.FromResult(NotUsedWhileRunningOn(
                    runsOn == RunsOnLocalId ? BrainRunsOnLocal : CodingAgents.ChoiceLabel(CodingAgents.FromId(runsOn))));
            return RefreshCloudModelsAsync();
        }

        /// <summary>Validate: one tiny call through the CLI on screen, off the UI thread, answered in plain words: not
        /// installed, not signed in, sign-in expired, model refused (and "press Update CLI" when the CLI is too old for
        /// it), timed out. Cancelled by Shutdown.</summary>
        private Task<string> ValidateCliPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            CodingAgentKind agent = CliOnScreen(pending);
            if (agent == CodingAgentKind.None) return Task.FromResult(PickACliFirst);
            CodingAgentCli cli = _cli;
            if (cli == null) return Task.FromResult("✗ This module has no CLI runner (it was not initialised).");
            // A sign-in token typed and not applied yet is the one tested, like the CLI on screen; one that cannot be a
            // token is refused in words before anything runs.
            string typedToken = null;
            string onScreen;
            if (agent == CodingAgentKind.Claude && pending != null && pending.TryGetValue("cliToken", out onScreen) &&
                !string.IsNullOrEmpty(onScreen))
            {
                string refusal = CodingAgentCli.CheckClaudeToken(onScreen, out typedToken);
                if (refusal != null) return Task.FromResult("✗ " + refusal);
            }
            CancellationToken token;
            try { token = _lifetime.Token; } catch (ObjectDisposedException) { return Task.FromResult("✗ AI Brain is shutting down."); }
            return Task.Run(async delegate
            {
                CliAnswer answer = await cli.ValidateAsync(agent, token, typedToken).ConfigureAwait(false);
                return CodingAgentCliText.Describe(agent, answer, CodingAgentCli.ValidateTimeout);
            });
        }

        /// <summary>Remove token: delete the saved Claude sign-in token now; the runner logs the delete and says what
        /// happened.</summary>
        private Task<string> RemoveCliTokenAsync()
        {
            CodingAgentCli cli = _cli;
            if (cli == null) return Task.FromResult("✗ This module has no CLI runner (it was not initialised).");
            return Task.Run(delegate { return cli.RemoveClaudeToken(); });
        }

        /// <summary>Update CLI: the CLI's own update, off the UI thread; the runner refuses while any call of this module's
        /// is running. Not cancelled by Shutdown on purpose: stopping an npm install halfway is worse than letting it end.</summary>
        private Task<string> UpdateCliPendingAsync(IReadOnlyDictionary<string, string> pending)
        {
            CodingAgentKind agent = CliOnScreen(pending);
            if (agent == CodingAgentKind.None) return Task.FromResult(PickACliFirst);
            CodingAgentCli cli = _cli;
            if (cli == null) return Task.FromResult("✗ This module has no CLI runner (it was not initialised).");
            return Task.Run(delegate { return cli.UpdateAsync(agent, CancellationToken.None); });
        }

        /// <summary>The card's "CLI" row: which CLI, its version, and the model a call runs on.</summary>
        private string CliNameLine(CodingAgentKind agent, AiSettings s)
        {
            // About the SAVED choice, which the radio may already differ from on screen (the owner's other workstation,
            // 2026-10-07: "None chosen" beside a radio showing Claude Code CLI read as a contradiction); Validate tests the
            // one on screen before Apply.
            if (agent == CodingAgentKind.None)
                return "No CLI in use yet. Choose Claude Code CLI or Codex CLI under \"Brain runs on\" and press Apply (Validate tests the one on screen before that).";
            string product = CodingAgents.ProductName(agent);
            CodingAgentCli.CliDetails details = _cli == null ? null : _cli.CachedDetails(agent);
            if (details == null) return product + ": checking… reopen this pane in a moment.";
            if (!details.Installed) return CodingAgentCliText.NotInstalledRow(agent);
            string named = product + (details.Version.Length > 0 ? " " + details.Version : "") +
                           (details.Where.Length > 0 ? " (" + details.Where + ")" : "");
            if (agent == CodingAgentKind.Claude) return named + ", its default model";
            return named + ", " + CodexModelPhrase(details, s != null && s.UseVision);
        }

        /// <summary>Codex's pick as the card says it: one model, or the text one and the screenshot one when they differ
        /// and vision is on.</summary>
        internal static string CodexModelPhrase(CodingAgentCli.CliDetails details, bool vision)
        {
            if (details == null || (details.TextModel == null && details.VisionModel == null))
                return "Codex's own default model (its catalog listed none for this module)";
            string text = details.TextModel ?? details.VisionModel;
            string picture = details.VisionModel ?? details.TextModel;
            if (!vision || string.Equals(text, picture, StringComparison.Ordinal))
                return (vision ? picture : text) + ", the first model this Codex lists";
            return text + ", and " + picture + " for a screenshot: the first models this Codex lists";
        }

        /// <summary>The card's "Signed in as" row.</summary>
        private string CliAccountLine(CodingAgentKind agent)
        {
            if (agent == CodingAgentKind.None) return "";
            CodingAgentCli.CliDetails details = _cli == null ? null : _cli.CachedDetails(agent);
            if (details == null) return "Checking…";
            return details.Installed ? details.SignedIn : "";
        }

        /// <summary>The card's "Status" row: the last Validate this session, with when and how long.</summary>
        private string CliStatusLine(CodingAgentKind agent)
        {
            if (agent == CodingAgentKind.None) return "";
            string last = _cli == null ? null : _cli.LastValidation(agent);
            return last ?? "Not validated yet. Press Validate.";
        }

        /// <summary>The card's "Goes through it" row: one plain account of what leaves the machine on this path.</summary>
        internal static string CliSendsLine(AiSettings s, CodingAgentKind agent)
        {
            string to = agent == CodingAgentKind.None ? "Anthropic (Claude Code) or OpenAI (Codex)" : CodingAgents.Vendor(agent);
            bool vision = s != null && s.UseVision;
            return "Every remark goes to " + to + " through it, with your persona and the front window's title: " +
                   (vision
                       ? "Ask, the hotkey, the tray row and the random drops each send a screenshot of the window (Use vision is on), and the poke reaction the text read off the screen."
                       : "Ask, the hotkey, the tray row, the random drops and the poke reaction each send the text read off the screen (OCR); with Use vision on, a screenshot instead.") +
                   " The persona auditions go the same way.";
        }

        // ---- the Status card (owner, 2026-10-06): one line, true when read ----------------------------------------
        //
        // "On.  |  runs on: Claude Code CLI 2.1.292  |  vision: on  |  last remark 14:02 (5.2 s)". The first part is
        // BrainStatusLine, unchanged (off, not started and why, standing down for Remembrance); then the engine, whether
        // the screen is sent as a picture, and how this session's last remark went. Never an account: the CLI card says that.

        private readonly object _remarkSync = new object();
        private DateTime _lastRemarkAt;
        private long _lastRemarkMs = -1;
        private string _lastRemarkFailure;

        /// <summary>Record how a started turn ended: the time, how long, and on failure why (a class, never a message).
        /// Called from AskCoreAsync on the pool thread; read at Load on the UI thread, hence the lock.</summary>
        internal void RecordRemark(DateTime atLocal, long elapsedMilliseconds, string failure)
        {
            lock (_remarkSync)
            {
                _lastRemarkAt = atLocal;
                _lastRemarkMs = elapsedMilliseconds;
                _lastRemarkFailure = failure;
            }
        }

        internal string StatusRowLine(AiSettings s, string remembrancePhase)
        {
            string head = BrainStatusLine(s, remembrancePhase);
            if (s == null) return head;
            var parts = new List<string> { head, "runs on: " + RunsOnPhrase(s), "vision: " + (s.UseVision ? "on" : "off") };
            DateTime at;
            long ms;
            string failure;
            lock (_remarkSync)
            {
                at = _lastRemarkAt;
                ms = _lastRemarkMs;
                failure = _lastRemarkFailure;
            }
            if (ms < 0) parts.Add("no remark yet this session");
            else if (failure == null)
                parts.Add("last remark " + at.ToString("HH:mm", CultureInfo.InvariantCulture) + " (" +
                          (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s)");
            else
                parts.Add("last ask " + at.ToString("HH:mm", CultureInfo.InvariantCulture) + " had no answer (" + failure + ")");
            return string.Join("  |  ", parts);
        }

        /// <summary>What the brain runs on, as the Status card says it: the CLI and its version, the cloud provider and its
        /// model (and whether the local fallback is on), or the local backend and its model.</summary>
        private string RunsOnPhrase(AiSettings s)
        {
            CodingAgentKind agent = CodingAgents.FromId(s.CliBackend);
            if (agent != CodingAgentKind.None)
            {
                CodingAgentCli.CliDetails details = _cli == null ? null : _cli.CachedDetails(agent);
                string version = details != null && details.Version.Length > 0 ? " " + details.Version : "";
                return CodingAgents.ChoiceLabel(agent) + version;
            }
            string model = (s.UseVision ? s.VisionModel : s.TextModel) ?? "";
            if (!IsLocalSlot(s))
            {
                string cloudModel = (s.UseVision ? s.CloudVisionModel : s.CloudTextModel) ?? "";
                return "cloud " + (s.Provider ?? "") + (cloudModel.Length > 0 ? " " + cloudModel : "") +
                       (s.UseLocalFallback ? ", local fallback on" : "");
            }
            bool ollama = !string.Equals(s.LocalBackendKind, "openai-compat", StringComparison.OrdinalIgnoreCase);
            return "local " + (ollama ? "Ollama" : "OpenAI-compatible server") + (model.Length > 0 ? " " + model : "");
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            AiSettings s = _settings;
            if (s != null)
            {
                d["enabled"] = s.AiBrainEnabled ? "true" : "false";
                // The Remembrance reason is read from the shared context HERE, at Load (Addendum 1); the rest of the
                // line still comes from the settings alone.
                // Since 1.3.0 the Status card's whole line (StatusRowLine), which begins with BrainStatusLine's answer.
                d["brainStatus"] = StatusRowLine(s, RemembranceBlockingPhase());
                d["companionName"] = s.CompanionName ?? "";
                d["userName"] = s.UserName ?? "";
                d["disposition"] = DispositionNameForId(s.Disposition);
                // Local provider slot (always available; Ollama-native by default, or a generic
                // OpenAI-compatible /v1 server such as llama.cpp/LM Studio).
                d["localBackendKind"] = LocalBackendKindLabelForId(s.LocalBackendKind);
                d["endpoint"] = s.Endpoint ?? "";
                // SNAPSHOT under the lock, FORMAT outside it. The lock guards the two list copies and
                // nothing else: the label formatting below and VramStatusLine stay outside it. (This used
                // to cite VramStatusLine's "blocking model query -- up to a couple of seconds" as the thing
                // kept out of the critical section; that line has been a cached string plus a pool-thread
                // probe since 1.1.11, F064.) Copying two short lists costs nothing and gives the four label
                // reads below a consistent view even if a refresh replaces a list midway.
                ModelListing[] localSnapshot, cloudSnapshot;
                lock (_modelsLock)
                {
                    localSnapshot = _localModels.ToArray();
                    cloudSnapshot = _cloudModels.ToArray();
                }
                d["textModel"] = FormatModelLabel(s.TextModel, localSnapshot);
                d["visionModel"] = FormatModelLabel(s.VisionModel, localSnapshot);
                d["useVision"] = s.UseVision ? "true" : "false";
                d["tesseractPath"] = s.TesseractPath ?? "";
                d["ocrWhen"] = OcrWhenLine;
                d["autoStart"] = s.AutoStartServer ? "true" : "false";
                d["residency"] = ResidencyLabel(s.ModelResidency);
                d["standDownFullscreen"] = s.StandDownForFullscreen ? "true" : "false";
                d["standDownRemembrance"] = s.StandDownForRemembrance ? "true" : "false";
                d["vramStatus"] = VramStatusLine(s);
                // Cloud provider slot.
                // While the cloud is not primary the dropdown names the provider it would use, the one last chosen
                // (feature/cli-backend: "(none)" left the list, since the radio says whether the cloud is used).
                d["cloudProvider"] = CloudProviderLabelForId(string.IsNullOrEmpty(s.Provider) ? RememberedCloudProvider(s) : s.Provider);
                d["cloudEndpoint"] = s.OpenAiBaseUrl ?? "";
                d["cloudTextModel"] = FormatModelLabel(s.CloudTextModel, cloudSnapshot);
                d["cloudVisionModel"] = FormatModelLabel(s.CloudVisionModel, cloudSnapshot);
                d["cloudConsent"] = s.CloudDataConsent ? "true" : "false";
                d["apiKey"] = string.IsNullOrEmpty(s.ApiKey) ? "" : "set";   // cloud-key presence hint; never the plaintext
                d["useLocalFallback"] = s.UseLocalFallback ? "true" : "false";
                d["hotkey"] = s.Hotkey ?? "";
                // The radio and the CLI card (feature/cli-backend): four rows about the SAVED choice, served from the
                // runner's cache and refreshed behind (a pane open never waits on a child process). After Apply the host
                // rebuilds a pane carrying Info rows, so a new choice shows its own rows at once.
                CodingAgentKind cli = CodingAgents.FromId(s.CliBackend);
                d["brainRunsOn"] = BrainRunsOnLabel(s);
                d["cliName"] = CliNameLine(cli, s);
                d["cliAccount"] = CliAccountLine(cli);
                // The saved sign-in token's presence and never the token: the host's Secret box shows "a value is saved".
                string ignoredToken;
                d["cliToken"] = _cli != null && _cli.ReadClaudeToken(out ignoredToken) != CodingAgentCli.ClaudeTokenState.None ? "set" : "";
                d["cliStatus"] = CliStatusLine(cli);
                d["cliSends"] = CliSendsLine(s, cli);
            }
            return d;
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            AiSettings s = _settings;
            if (s == null || values == null) return false;
            // The Claude sign-in token FIRST, before anything is written onto the live settings: one that is not a token,
            // or cannot be sealed, refuses the whole Apply (nothing saved, the typed values stay on screen, the reason in
            // the log, which never carries the token). Present only when the user typed one; blank keeps the saved one.
            // It is a store of its own, so a cloud key refused below leaves a good token saved; the two cannot meet in
            // one Apply in practice, since the token's row is live only on Claude Code and the cloud card only on the cloud.
            string typedToken;
            if (values.TryGetValue("cliToken", out typedToken) && !string.IsNullOrEmpty(typedToken))
            {
                CodingAgentCli cli = _cli;
                string tokenError = "the module has no CLI runner";
                if (cli == null || !cli.TrySetClaudeToken(typedToken, out tokenError))
                {
                    try { if (_host != null) _host.Log(Info.Id, "sign-in token not stored: " + tokenError); } catch { }
                    return false;
                }
            }
            string keyError;
            // Judged on a detached copy BEFORE anything is written onto the live instance (N-burn-aibrain-02):
            // ApplyPaneValues writes every other field before it reaches the key, so a save refused for its key
            // used to leave the live settings carrying the pane's typed values, unsaved and unapplied, until the
            // next Apply or restart. The copy is PendingSettings' (the mapping the pending-aware actions already
            // use), and the live write runs only once the copy has proved the key storable; its own answer is
            // still read, so a key refused between the two calls cannot be dropped silently. One short-circuit,
            // one decision, so a single mutant can defeat it.
            bool storable = PendingSettings(s, values, out keyError) != null && ApplyPaneValues(s, values, out keyError);
            if (!storable)
            {
                // A key was typed and NOT stored: no cloud provider or endpoint to scope it to, or DPAPI refused.
                // Persisting the rest and answering true made the host report a saved pane with the key silently
                // dropped, and the pane's "set" hint then described a key that did not exist (RA-058). Nothing is
                // saved or applied: the host says these settings could not be saved, the typed values stay on
                // screen for the user to fix and press again, and the running brain keeps the saved configuration.
                // The reason goes to the log, the channel the pane's bool cannot carry.
                try { if (_host != null) _host.Log(Info.Id, "api key not stored: " + keyError); } catch { }
                return false;
            }
            bool ok = s.SaveWithin(AiSettings.UiSaveBudgetMilliseconds);
            ApplyState();   // re-apply triggers/backend to reflect the new config
            return ok;
        }

        /// <summary>
        /// Write the pane's values onto <paramref name="s"/>: every field the schema declares, in the order the
        /// cloud key needs (provider and endpoint before the key that is scoped to them). Shared by Save (onto the
        /// live instance) and by the pending-aware actions (onto a detached copy), so an action asked about "what is
        /// on screen" maps the pane exactly as Apply would (RA-055). False, with the reason, when a typed key could
        /// not be stored; every other field is applied regardless.
        /// </summary>
        private bool ApplyPaneValues(AiSettings s, IReadOnlyDictionary<string, string> values, out string keyError)
        {
            keyError = null;
            bool keyStored = true;
            string v;
            bool b;
            if (values.TryGetValue("enabled", out v) && bool.TryParse(v, out b)) s.AiBrainEnabled = b;
            if (values.TryGetValue("companionName", out v)) s.CompanionName = (v ?? "").Trim();
            if (values.TryGetValue("userName", out v)) s.UserName = (v ?? "").Trim();
            if (values.TryGetValue("disposition", out v)) s.Disposition = DispositionIdForName(v);
            // ---- Local provider slot: always present; Ollama-native by default, or a generic
            // OpenAI-compatible /v1 server (llama.cpp/LM Studio/other) via localBackendKind ----
            if (values.TryGetValue("localBackendKind", out v)) s.LocalBackendKind = LocalBackendKindIdForLabel(v);
            if (values.TryGetValue("endpoint", out v) && !string.IsNullOrWhiteSpace(v)) s.Endpoint = v.Trim();
            if (values.TryGetValue("textModel", out v) && !string.IsNullOrWhiteSpace(v)) s.TextModel = ResolveModelId(v);
            if (values.TryGetValue("visionModel", out v) && !string.IsNullOrWhiteSpace(v)) s.VisionModel = ResolveModelId(v);
            if (values.TryGetValue("useVision", out v) && bool.TryParse(v, out b)) s.UseVision = b;
            // Blank is meaningful here (= auto-detect), so unlike endpoint/model this one accepts an empty
            // value rather than treating it as "leave unchanged".
            if (values.TryGetValue("tesseractPath", out v)) s.TesseractPath = (v ?? "").Trim();
            if (values.TryGetValue("autoStart", out v) && bool.TryParse(v, out b)) s.AutoStartServer = b;
            if (values.TryGetValue("residency", out v)) s.ModelResidency = ResidencyFromLabel(v);
            if (values.TryGetValue("standDownFullscreen", out v) && bool.TryParse(v, out b)) s.StandDownForFullscreen = b;
            if (values.TryGetValue("standDownRemembrance", out v) && bool.TryParse(v, out b)) s.StandDownForRemembrance = b;
            // ---- Cloud provider slot ----
            // Switching the cloud provider prefills its preset endpoint (the stale endpoint field is ignored
            // on a switch); "(none)" clears the cloud selection (local-only); keeping the provider honors an
            // edited cloud endpoint. Reuses the unchanged SelectProviderEndpoint/UpdateSelectedProviderEndpoint.
            // ---- Brain runs on (feature/cli-backend, mockup AB2), decided before the cloud dropdown is read ----
            // A CLI wins over both slots and leaves them as they were, so choosing either again restores it. "Local model"
            // clears the cloud primary and remembers which provider the dropdown names. "Cloud provider", and a pane that
            // hands over no radio at all (an older caller), let the dropdown choose, as it always did; its legacy
            // "(none)" still means local for such a caller, and under "Cloud provider" means the remembered provider.
            string runsOn = null;
            if (values.TryGetValue("brainRunsOn", out v)) runsOn = BrainRunsOnIdForLabel(v);   // null: no option, no change
            if (runsOn != null)
                s.CliBackend = CodingAgents.FromId(runsOn) != CodingAgentKind.None ? runsOn : "";
            bool cloudProviderChanged = false;
            if (string.Equals(runsOn, RunsOnLocalId, StringComparison.Ordinal))
            {
                string onScreen;
                string named = values.TryGetValue("cloudProvider", out onScreen) ? CloudProviderIdForLabel(onScreen) : "";
                if (!string.IsNullOrEmpty(s.Provider)) s.LastCloudProvider = s.Provider;
                else if (named.Length > 0) s.LastCloudProvider = named;
                s.Provider = "";
            }
            else if (CodingAgents.FromId(runsOn) != CodingAgentKind.None)
            {
                // A CLI: the slot underneath stays exactly as it was.
            }
            else if (values.TryGetValue("cloudProvider", out v))
            {
                string newProvider = CloudProviderIdForLabel(v);
                if (newProvider.Length == 0 && string.Equals(runsOn, RunsOnCloudId, StringComparison.Ordinal))
                    newProvider = RememberedCloudProvider(s);
                cloudProviderChanged = !string.Equals(newProvider, s.Provider ?? "", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(newProvider))
                    s.Provider = "";   // "(none)" -> local-only; leaves the remembered cloud endpoint intact
                else
                    s.SelectProviderEndpoint(newProvider, cloudProviderChanged);
            }
            if (string.Equals(runsOn, RunsOnCloudId, StringComparison.Ordinal) && string.IsNullOrEmpty(s.Provider))
            {
                // "Cloud provider" chosen with no dropdown value handed over: the remembered provider, so the radio's
                // choice is never stored as the local slot.
                s.SelectProviderEndpoint(RememberedCloudProvider(s), true);
                cloudProviderChanged = true;
            }
            if (!cloudProviderChanged && !string.IsNullOrEmpty(s.Provider) &&
                values.TryGetValue("cloudEndpoint", out v) && !string.IsNullOrWhiteSpace(v))
                s.UpdateSelectedProviderEndpoint(v.Trim());
            // Cloud models are optional (empty = unset), so unlike the local models they may be cleared.
            if (values.TryGetValue("cloudTextModel", out v)) s.CloudTextModel = ResolveModelId((v ?? "").Trim());
            if (values.TryGetValue("cloudVisionModel", out v)) s.CloudVisionModel = ResolveModelId((v ?? "").Trim());
            if (values.TryGetValue("cloudConsent", out v) && bool.TryParse(v, out b)) s.CloudDataConsent = b;
            // Secret: only present when the user typed a new key; scoped to the CURRENT cloud provider +
            // endpoint (set just above), so it must run after the provider/endpoint fields. Its answer is the
            // method's: a false here and its reason used to be discarded (RA-058).
            if (values.TryGetValue("apiKey", out v) && !string.IsNullOrEmpty(v)) keyStored = s.TrySetApiKey(v, out keyError);
            // ---- Fallback + triggers ----
            if (values.TryGetValue("useLocalFallback", out v) && bool.TryParse(v, out b)) s.UseLocalFallback = b;
            if (values.TryGetValue("hotkey", out v) && !string.IsNullOrWhiteSpace(v)) s.Hotkey = v.Trim();
            return keyStored;
        }

        // Disposition enum: the pane shows the friendly Name, the setting stores the Id. The catalog itself
        // (curated characters, each a complete tone+voice instruction) lives in Dispositions.cs, shared with
        // the runtime prompt builder.
        private static string[] DispositionNames()
        {
            var names = new List<string>(Dispositions.All.Length);
            foreach (Dispositions.Disposition d in Dispositions.All) names.Add(d.Name);
            return names.ToArray();
        }
        private static string DispositionNameForId(string id)
        {
            foreach (Dispositions.Disposition d in Dispositions.All)
                if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d.Name;
            foreach (Dispositions.Disposition d in Dispositions.All)
                if (string.Equals(d.Id, Dispositions.DefaultId, StringComparison.OrdinalIgnoreCase)) return d.Name;
            return "";
        }
        private static string DispositionIdForName(string name)
        {
            foreach (Dispositions.Disposition d in Dispositions.All)
                if (string.Equals(d.Name, name, StringComparison.Ordinal)) return d.Id;
            return Dispositions.DefaultId;
        }

        // Tray-item icons (TrayItem.IconPng): read once at Init from the module's own embedded PNGs, so the
        // base can show them without the ABI depending on System.Drawing. Null on any failure -- a
        // missing/malformed icon must never break the tray item, which is ModuleKit's contract too.
        private static byte[] LoadIconResource(string fileName)
        {
            return EmbeddedResources.LoadBytes(typeof(AiBrainModule).Assembly, fileName);
        }

        // Cloud-provider dropdown (schema v2): only the CLOUD selectors, with a friendly "(none)" for the
        // empty (local-only) value. The dropdown shows the label; the setting stores the id ("" for none).
        // The local presets (ollama/lmstudio/llamacpp) are intentionally NOT offered — the local slot is the
        // fixed Endpoint field now, not a Provider choice.
        private static string[] CloudProviderLabels()
        {
            // "(none)" left the list in 1.3.0 (mockup AB2): "Brain runs on" says whether the cloud is used. The label is
            // still READ (CloudProviderIdForLabel), so an older caller that hands it over still means local.
            return new[] { "openai", "openrouter", "custom" };
        }
        private static string CloudProviderLabelForId(string id)
        {
            switch ((id ?? "").Trim().ToLowerInvariant())
            {
                case "openai": return "openai";
                case "openrouter": return "openrouter";
                case "custom": return "custom";
                default: return "openai";   // "" / legacy local id / unknown: the dropdown has no "(none)" any more
            }
        }
        private static string CloudProviderIdForLabel(string label)
        {
            switch ((label ?? "").Trim().ToLowerInvariant())
            {
                case "openai": return "openai";
                case "openrouter": return "openrouter";
                case "custom": return "custom";
                default: return "";   // "(none)" or anything unrecognized -> local-only
            }
        }

        // Local-backend dropdown: which protocol the LOCAL slot speaks (see AiSettings.LocalBackendKind).
        // Only two choices, so a straight label<->id mapping (no "none" case — local is always available).
        private const string OllamaNativeLabel = "Ollama (native)";
        private const string LocalCompatLabel = "Generic OpenAI-compatible (llama.cpp / LM Studio / other)";
        private static string[] LocalBackendKindLabels()
        {
            return new[] { OllamaNativeLabel, LocalCompatLabel };
        }
        private static string LocalBackendKindLabelForId(string id)
        {
            return string.Equals(id, "openai-compat", StringComparison.OrdinalIgnoreCase)
                ? LocalCompatLabel : OllamaNativeLabel;
        }
        private static string LocalBackendKindIdForLabel(string label)
        {
            return string.Equals((label ?? "").Trim(), LocalCompatLabel, StringComparison.OrdinalIgnoreCase)
                ? "openai-compat" : "ollama";
        }

        // ---- Model-picker dropdowns (local + cloud text/vision) --------------------------------------

        /// <summary>
        /// Formats a model id into its dropdown label — <c>"SIZE · id · uncensored"</c>, with the size
        /// prefix present only when <paramref name="models"/> has a listing for this id with a known
        /// <see cref="ModelListing.SizeBytes"/> (a real value from Ollama's own "size" field — a solid proxy
        /// for VRAM/weight footprint; the generic OpenAI-compatible list has no such metadata, so cloud/
        /// openai-compat entries never get a size prefix) and the uncensored suffix only when
        /// <see cref="AiModelPolicy.LooksUncensored"/> matches. Registers the (label, id) pair into
        /// <see cref="_modelIdByLabel"/> as a side effect so <see cref="ResolveModelId"/> can reverse it —
        /// a variable-length size prefix can't be recovered by a fixed string pattern the way the old
        /// suffix-only scheme could.
        /// </summary>
        private string FormatModelLabel(string id, IReadOnlyList<ModelListing> models)
        {
            if (string.IsNullOrEmpty(id)) return "";
            long? size = null;
            if (models != null)
                foreach (ModelListing m in models)
                    if (m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        size = m.SizeBytes;
                        break;
                    }
            var parts = new List<string>(3);
            if (size.HasValue) parts.Add(FormatSize(size.Value));
            parts.Add(id);
            if (AiModelPolicy.LooksUncensored(id)) parts.Add("uncensored");
            string label = string.Join(" · ", parts);
            lock (_modelsLock) _modelIdByLabel[label] = id;
            return label;
        }

        // Look up a label the pane returned back to id (registered by FormatModelLabel). A label that was
        // never produced by FormatModelLabel this session (shouldn't normally happen — Load always runs
        // before Save) falls back to treating it as a raw id, so a save is never silently lost.
        private string ResolveModelId(string label)
        {
            string id;
            lock (_modelsLock)
                if (!string.IsNullOrEmpty(label) && _modelIdByLabel.TryGetValue(label, out id)) return id;
            return label ?? "";
        }

        // A rough, human-scannable VRAM/weight-footprint size: whole MB under 1GB, one-decimal GB above.
        // Decimal (1000-based) units, matching the common informal convention for a plain "GB"/"MB" label.
        private static string FormatSize(long bytes)
        {
            const double MB = 1_000_000.0;
            const double GB = 1_000_000_000.0;
            if (bytes < 0) return "";
            return bytes >= GB
                ? (bytes / GB).ToString("0.0", CultureInfo.InvariantCulture) + "GB"
                : Math.Round(bytes / MB).ToString(CultureInfo.InvariantCulture) + "MB";
        }

        /// <summary>
        /// Builds one dropdown's Options: every listed model (or only vision-capable ones when
        /// <paramref name="visionOnly"/> — the UNION of what the backend reported and the
        /// <see cref="AiModelPolicy.LooksVisionCapable"/> heuristic, because Ollama's /api/tags omits
        /// "vision" for some models that have it), tagged/sorted so uncensored-leaning
        /// models (<see cref="AiModelPolicy.LooksUncensored"/> — an advisory for personas that need a model
        /// to actually comply, e.g. Samuel/Triumph; never a hard filter) come first, each labeled with its
        /// known size (see <see cref="FormatModelLabel"/>) when available. SAFETY INVARIANT: the
        /// currently-saved value is always unioned in (labeled the same way), even if the fetch hasn't run
        /// yet, came back empty, or doesn't cover it — the pane's Enum dropdown is a closed, non-editable
        /// ComboBox (see PaneView.Build), so a value missing from Options would show nothing selected and a
        /// save would silently blank the field.
        /// </summary>
        internal string[] BuildModelOptions(IReadOnlyList<ModelListing> models, string currentValue, bool visionOnly)
        {
            var uncensoredLabels = new List<string>();
            var otherLabels = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (models != null)
                foreach (ModelListing model in models)
                {
                    // Contains, NOT Add. seenIds is what the union guard at the bottom consults to decide
                    // whether the saved value still needs inserting, so an id must not count as "already
                    // offered" until it has survived the vision filter below. Marking it here meant a saved
                    // vision model the filter rejects was neither listed NOR unioned back in -- and the
                    // dropdown is a closed ComboBox, so it showed nothing selected and the next save wrote
                    // the blank. That is exactly what the SAFETY INVARIANT above says cannot happen.
                    if (model == null || string.IsNullOrEmpty(model.Id) || seenIds.Contains(model.Id)) continue;
                    // UNION, not fallback. `??` only fires when the backend reported nothing, and
                    // Ollama's /api/tags reports a capabilities array that is INCOMPLETE for the Gemma
                    // family. Measured 2026-09-10 on one machine:
                    //
                    //   gemma3:4b              /api/show: completion,vision   /api/tags: completion
                    //   gemma4:26b             /api/show: completion,vision,… /api/tags: completion,…
                    //   mistral-small3.2:24b   /api/show: completion,vision,… /api/tags: vision,…
                    //
                    // So VisionFromCapabilities saw ["completion"], correctly returned false rather than
                    // null, and `??` treated an incomplete report as authoritative -- which hid the
                    // RECOMMENDED vision model from its own dropdown, while the "gemma3" name marker that
                    // exists for precisely this case was never consulted.
                    //
                    // Re-measured 2026-09-30 on Ollama 0.34.4: /api/tags and /api/show AGREE for gemma3:4b,
                    // gemma4:12b and gemma4:26b, so the under-report belongs to older servers. The union stays
                    // here because such a server may still be in use and a hidden model is the worse failure;
                    // the ASK trusts a reported false (AiModelPolicy.ChooseModel, F102 and R-020).
                    //
                    // The asymmetry decides the direction: a false positive is visible and recoverable
                    // (the user picks a model that cannot see, and changes it), while a false negative
                    // hides a working model with no way to discover it exists. /api/show would be
                    // authoritative but costs one request per model on every pane open; revisit if the
                    // name markers ever go stale enough to matter.
                    bool isVision = AiModelPolicy.IsVisionCapable(model.Id, model.Vision);
                    if (visionOnly && !isVision) continue;
                    seenIds.Add(model.Id);
                    (AiModelPolicy.LooksUncensored(model.Id) ? uncensoredLabels : otherLabels).Add(FormatModelLabel(model.Id, models));
                }
            var result = new List<string>(uncensoredLabels.Count + otherLabels.Count + 1);
            result.AddRange(uncensoredLabels);
            result.AddRange(otherLabels);
            if (!string.IsNullOrEmpty(currentValue) && seenIds.Add(currentValue))
                result.Insert(0, FormatModelLabel(currentValue, models));
            return result.ToArray();
        }

        /// <summary>
        /// The verdict half of "Test connection", separate from the I/O so it can be asserted with no
        /// server at all.
        ///
        /// An empty reply is a FAILURE, and that is the whole point. The caller used to discard the
        /// reply and report a green tick, so what it proved was that nothing threw -- and neither backend
        /// throws on a 200 carrying no content: OpenAiCompatBackend.ChatAsync returns "" when the choices
        /// array is missing or empty, and OllamaClient.ChatAsync has the same shape. A model id the
        /// provider does not serve, an exhausted quota and a moderation refusal all land there, and all
        /// three were reported as "connected".
        /// </summary>
        internal static string TestConnectionVerdict(string reply, string model, long elapsedMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(reply))
                return "✗ " + model + " answered with nothing — check the model id, the quota, and whether the provider refused.";
            return "✓ connected · " + model + " OK " +
                   (elapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        /// <summary>
        /// Rebuild the four model dropdowns' Options IN PLACE on the retained SettingField objects (the only
        /// way a refresh becomes visible to the pane — Schema itself is never rebuilt; PaneView re-reads
        /// Options fresh on each rebuild triggered by a PaneAction's ReloadPaneAfter). Called once at Init
        /// (the caches start empty, so this just seeds the current-value safety net) and again at the end of
        /// each "Refresh ... models" action.
        /// </summary>
        private void RefreshModelFieldOptions()
        {
            AiSettings s = _settings;
            lock (_modelsLock)
            {
                _textModelField.Options = BuildModelOptions(_localModels, s != null ? s.TextModel : "", false);
                _visionModelField.Options = BuildModelOptions(_localModels, s != null ? s.VisionModel : "", true);
                _cloudTextModelField.Options = BuildModelOptions(_cloudModels, s != null ? s.CloudTextModel : "", false);
                _cloudVisionModelField.Options = BuildModelOptions(_cloudModels, s != null ? s.CloudVisionModel : "", true);
            }
        }

        private static int CountUncensored(IReadOnlyList<ModelListing> models)
        {
            int count = 0;
            if (models != null)
                foreach (ModelListing m in models)
                    if (m != null && AiModelPolicy.LooksUncensored(m.Id)) count++;
            return count;
        }

        /// <summary>
        /// The status line of a "Refresh ... models" press. An empty list is named for its CAUSE (RA-059): both
        /// ListModelsAsync implementations fold every failure into an empty list by contract, because the brain
        /// reads empty as "unknown, do not complain", so an answered 401 read here as "No models found at <url>"
        /// and sent the user to check the URL rather than the key. An answered status gets F107's wording, the
        /// line "Test connection" gives for the same answer; a transport failure says not reachable; only a
        /// listing that answered and was empty says no models.
        /// </summary>
        internal static string ModelListStatus(IReadOnlyList<ModelListing> models, string target, Exception listingFailure)
        {
            if (models == null || models.Count == 0)
            {
                AiBackendHttpException answered = listingFailure as AiBackendHttpException;
                if (answered != null) return DescribeHttpFailure(answered, target);
                if (listingFailure != null)
                    return "✗ Not reachable at " + target + " (" + AiBrain.DescribeError(listingFailure) + ")";
                return "✗ No models found at " + target;
            }
            int uncensoredCount = CountUncensored(models);
            return "✓ " + models.Count.ToString(CultureInfo.InvariantCulture) + " model(s) found" +
                (uncensoredCount > 0
                    ? " (" + uncensoredCount.ToString(CultureInfo.InvariantCulture) + " tagged uncensored)"
                    : "");
        }

        /// <summary>"Refresh local models" pane action: lists whatever the LOCAL slot's configured backend
        /// reports (Ollama's real capabilities, or the generic /v1 id-only list for openai-compat), caches
        /// it, and rebuilds the local text/vision dropdowns from it.</summary>
        private async Task<string> RefreshLocalModelsAsync()
        {
            AiSettings s = _settings;
            if (s == null) return "✗ No settings.";
            string normalized, error;
            if (!AiEndpointPolicy.TryNormalize(s.Endpoint, out normalized, out error))
                return "✗ " + error;
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(120, s.TimeoutSeconds)));
            try
            {
                IReadOnlyList<ModelListing> models;
                Exception listingFailure = null;
                using (ICompanionBrainBackend backend = BuildLocalBackend(s, normalized, timeout))
                {
                    models = await ListBackendModelsAsync(backend, CancellationToken.None).ConfigureAwait(false)
                             ?? (IReadOnlyList<ModelListing>)new List<ModelListing>();
                    IModelListingStatus status = backend as IModelListingStatus;
                    if (status != null) listingFailure = status.LastListingFailure;   // RA-059
                }
                // One critical section: swapping the list and rebuilding the dropdowns from it is a
                // single logical update, so a concurrent cloud refresh cannot read a half-replaced list.
                lock (_modelsLock)
                {
                    _localModels.Clear();
                    _localModels.AddRange(models);
                    RefreshModelFieldOptions();
                }
                // The live brain re-lists its own backend too, so the pane and the brain agree about what is
                // installed: a model pulled mid-session stayed "missing" to the brain until the next Apply (F071).
                _ = _session.RefreshInventoryAsync(_lifetime.Token);
                return ModelListStatus(models, normalized, listingFailure);
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>"Refresh cloud models" pane action: lists the configured cloud endpoint's models
        /// (generic /v1, id-only), caches it, and rebuilds the cloud text/vision dropdowns from it.</summary>
        private async Task<string> RefreshCloudModelsAsync()
        {
            AiSettings s = _settings;
            if (s == null) return "✗ No settings.";
            if (string.IsNullOrEmpty(s.Provider)) return "✗ Select a cloud provider first.";
            string normalized, error;
            if (!AiEndpointPolicy.TryNormalize(s.OpenAiBaseUrl, out normalized, out error))
                return "✗ " + error;
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(120, s.TimeoutSeconds)));
            try
            {
                IReadOnlyList<ModelListing> models;
                Exception listingFailure;
                using (var backend = new OpenAiCompatBackend(normalized, s.ApiKey, timeout))
                {
                    models = await backend.ListModelsAsync(CancellationToken.None).ConfigureAwait(false);
                    listingFailure = backend.LastListingFailure;   // RA-059
                }
                lock (_modelsLock)
                {
                    _cloudModels.Clear();
                    _cloudModels.AddRange(models);
                    RefreshModelFieldOptions();
                }
                _ = _session.RefreshInventoryAsync(_lifetime.Token);   // as above (F071)
                return ModelListStatus(models, normalized, listingFailure);
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>Tray toggle: flip the module's own AiBrainEnabled, persist, and (re)build the brain.</summary>
        private void ToggleEnabled()
        {
            AiSettings s = _settings;
            if (s == null) return;
            s.AiBrainEnabled = !s.AiBrainEnabled;
            // SAYS SO WHEN IT FAILS. This was `try { s.Save(); } catch { }`, which discarded both the
            // return value and the exception -- so a toggle that did not persist looked exactly like one
            // that did, and came back on the next launch with no explanation. That matters more now the
            // budget is bounded: a contended save can legitimately return false.
            bool persisted;
            try { persisted = s.SaveWithin(AiSettings.UiSaveBudgetMilliseconds); }
            catch (Exception ex)
            {
                persisted = false;
                if (_host != null) _host.Log(Info.Id, "AI toggle not saved: " + ex.GetType().Name);
            }
            if (!persisted && _host != null)
                _host.Log(Info.Id, "AI toggle applied for this session but NOT saved; it will revert on restart");
            ApplyState();
        }

        private void OnPetSeen(ICompanion pet) { if (pet != null) _lastPet = pet; }
        private void OnPetPoked(PokeInfo info) { if (info != null && info.Pet != null) _lastPet = info.Pet; }

        /// <summary>
        /// Drop responder, and since 1.2.3 the ONLY schedule for unprompted commentary: the host's global
        /// "Randomly drop a fortune / insight" setting drives this, and the module no longer runs a timer of
        /// its own. When the brain is enabled this takes the tick as an AI insight (returns handled, so
        /// Fortunes stays quiet); otherwise it declines and a local fortune speaks.
        /// </summary>
        private bool OnDrop(ICompanion pet)
        {
            if (!_session.Enabled) return false;
            // Decline if the AI spoke very recently (a hotkey ask landing just before a drop), so the user
            // gets a fortune rather than two model answers back to back. This is the one piece of the old
            // idle-loop suppression worth keeping, and declining is strictly better than going silent
            // because the responder chain falls through to Fortunes.
            if ((DateTime.UtcNow - _lastInteractionUtc).TotalSeconds < 30) return false;
            // Don't load several GB of VRAM next to a game that already owns it. Declining is exactly right
            // here rather than going silent: the responder chain falls through to Fortunes, so the pet still
            // says something, it just says something free. Only on the local slot since 1.4.0: a CLI or a cloud
            // provider loads nothing here, and whether this companion can be SEEN is Ask's per-companion question.
            if (GpuGuardBlocks()) return false;
            // The stand-down's second reason (lane feature/aibrain-standdown, 1.2.0): Remembrance running a local model.
            // Declined the same silent way, so Fortunes speaks instead, and with nothing released, because AI Brain's
            // model can be the very one Remembrance is using (RemembranceBlockingPhase).
            if (RemembranceBlockingPhase() != null) return false;
            // allowVision: TRUE, deliberately, and pinned by the module self-test. A drop is unprompted
            // commentary, and with UseVision on it is a vision turn exactly like the hotkey and the tray row:
            // the owner's decision of 2026-09-29 (BUG-010, docs/ISSUES-post-1.0.0.md) is that vision, when
            // enabled, applies to every remark, because the screen is what the remark is about. The audit
            // proposed `false` here to match a label written for the module's OLD idle loop; the label was
            // what changed. Only the poke stays text-only, and OnPokeReaction says why.
            return Ask(pet, true);
        }

        // ---- the fullscreen stand-down: two questions since 1.4.0 (lane feature/fullscreen-per-monitor) ----
        //
        // Until 1.3.3 one check, FullscreenBlocked, declined every remark on every engine whenever a fullscreen window
        // existed on ANY monitor, on two premises: a model loading beside a game can take it down, and the companion is
        // hidden during a game anyway. The first is true of the local model only. The second is true of one monitor
        // only: with a game on one screen the companion moves to a free one and is in plain view, and the owner pressed
        // Ctrl+Alt+P at it on 2026-10-07, on Claude Code, and got nothing (the log said "ask declined: fullscreen
        // stand-down"). So the two premises are two questions now. GpuGuardBlocks: is a game running while this would
        // run the LOCAL model (the switch governs it, nothing else does)? CompanionStoodDown: can the user see the
        // companion this turn belongs to (the host's own per-companion answer, every engine, no switch)?

        /// <summary>Whether a fullscreen app is running and the switch says to stand down for one. Also records the reading
        /// for the cloud slot's fallback (LocalFallbackHold), as RemembrancePhase does for its reason. UI thread.</summary>
        private bool FullscreenNow()
        {
            bool active = false;
            if (_settings != null && _settings.StandDownForFullscreen)
            {
                try { active = _host != null && _host.IsFullscreenActive; } catch { active = false; }
            }
            _fullscreenAtLastRead = active;
            return active;
        }

        /// <summary>
        /// The graphics-card guard: true when a fullscreen app is running, the switch is on, and this turn would run on the
        /// LOCAL model. While a game runs it also releases anything already resident, on any slot (a cloud slot's fallback
        /// may have loaded one), which is the half that actually protects a game: a model loaded BEFORE the game started
        /// is not helped by declining to load. A cloud or CLI turn goes ahead; the cloud's fallback to the local model is
        /// held back by the reading this takes (LocalFallbackHold). Cheap: the host answers from the scan the pets run.
        /// </summary>
        private bool GpuGuardBlocks()
        {
            if (!FullscreenNow()) return false;
            ReleaseModelForFullscreen();
            return IsLocalSlot(_settings);
        }

        /// <summary>
        /// Whether the companion is stood down for a fullscreen window, as the host decides it: hidden with no free monitor,
        /// or on a blocked one on its way to a free one (ICompanionStandDown, host 1.5.0). The host holds such a
        /// companion's speech until its screen clears, so a turn for it would be answered after the game, or a paid CLI or
        /// cloud call would be spent on a line nobody sees now. No switch: this is not a preference, it is whether the
        /// answer can be seen. A host without the interface (only a test double, under MinHostVersion 1.5.0) gets the
        /// any-monitor answer, the conservative one, which is what every companion on one monitor sees anyway. A host
        /// that throws counts as in view, as FullscreenBlocked failed open before it.
        /// </summary>
        private static bool CompanionStoodDown(IHost host, ICompanion pet)
        {
            if (host == null || pet == null) return false;
            try
            {
                var standDown = host as ICompanionStandDown;
                return standDown != null ? standDown.IsCompanionStoodDown(pet) : host.IsFullscreenActive;
            }
            catch { return false; }
        }

        /// <summary>The fullscreen reading the UI thread last took (FullscreenNow, OnFullscreenChanged), for the cloud
        /// slot's fallback, which is decided on a pool thread.</summary>
        private volatile bool _fullscreenAtLastRead;

        /// <summary>
        /// Evict the local model so a game gets its VRAM back. Best-effort and fire-and-forget: this runs
        /// while a game is starting, which is the worst possible moment to block on anything.
        ///
        /// WITHHELD while Remembrance is running a local model (lane feature/aibrain-standdown). The release is a
        /// keep_alive:0 for each id this brain may have loaded, and AI Brain's default id can be the very model
        /// Remembrance is using on the same server: a game starting mid-transcription would evict it, and the per-check
        /// release in FullscreenBlocked would evict it again before each later chunk of Remembrance's map-reduce
        /// summary. Addendum 1 of the brief withdrew the release from the Remembrance reason for exactly that; this
        /// applies it to the overlap. Neither reason lets AI Brain send anything new, so what stays resident is only
        /// what was there already. Recorded under `#### feature/aibrain-standdown` in docs/DESIGN-REGISTER.md.
        /// </summary>
        private void ReleaseModelForFullscreen()
        {
            if (RemembrancePhase() != null) return;
            try
            {
                _ = _session.ReleaseModelAsync(_lifetime.Token);
            }
            catch { }
        }

        /// <summary>Host said a fullscreen app appeared or went away. Appearing is the one that matters: it is
        /// the only moment we can release VRAM BEFORE the game needs it rather than after.</summary>
        private void OnFullscreenChanged(bool active)
        {
            bool standingDown = active && _settings != null && _settings.StandDownForFullscreen;
            // Recorded on BOTH edges: a game starting mid-turn holds that turn's fallback back, and one closing frees it.
            _fullscreenAtLastRead = standingDown;
            if (!standingDown) return;
            ReleaseModelForFullscreen();
        }

        // ---- the stand-down's second reason: Remembrance running a local model (lane feature/aibrain-standdown) ----
        //
        // Remembrance publishes `remembrance.busy` on the host's shared context while whisper or its Ollama summary runs
        // (RemembranceBusyFlag). AI Brain reads it AT USE, at each decision, on the UI thread, with ReadContext: the
        // register's settled pattern for a value read when it is needed (docs/DESIGN-REGISTER.md, "IHost.ContextChanged
        // is the push half"), and all this reason needs, because it has nothing to do ON the change. It declines and
        // releases nothing (Addendum 1 of the brief: AI Brain's default gemma3:4b can be the very model Remembrance is
        // using on the same server). Reading at use also honours a flag published before this module loaded, and
        // leaves no handler for Shutdown to detach.

        /// <summary>The pane's answer to an action that would send a chat to the local model while Remembrance is busy.</summary>
        internal const string RemembranceBusyAnswer = "⚠ Remembrance is using the local model right now. Try again when it finishes.";

        /// <summary>What the companion says when the hotkey or the tray row is declined while Remembrance is busy.</summary>
        internal const string RemembranceBusySpokenLine = "Remembrance is using the model right now. Ask me again when it's done.";

        /// <summary>What the companion says when the hotkey or the tray row is declined by the graphics-card guard: a
        /// fullscreen app is running and the brain runs on the local model (1.4.0).</summary>
        internal const string FullscreenGpuSpokenLine = "A fullscreen app is running, so I'm leaving the graphics card to it. Ask me again when it closes.";

        /// <summary>
        /// Say <see cref="RemembranceBusySpokenLine"/> through the normal speech path, to the companion the declined ask
        /// was for: the hotkey and the tray row have none of their own and use the last one seen, as a started turn
        /// does. Nothing is said when that companion is gone; the log line Ask writes beside this one still stands.
        /// One line, and nothing else: no thinking cue, no emotion, since no turn started.
        /// </summary>
        private static void SayRemembranceBusy(IHost host, ICompanion pet)
        {
            SayIfInView(host, pet, RemembranceBusySpokenLine);
        }

        /// <summary>
        /// Say a declined explicit ask's reason to the companion it was for, when the user can see that companion (1.4.0).
        /// Nothing when it is gone, and nothing when it is stood down for a fullscreen window: the host would hold the
        /// line and say it after the game, by which time "ask me again" answers a question nobody remembers asking. The
        /// log line Ask writes beside it stands either way.
        /// </summary>
        private static void SayIfInView(IHost host, ICompanion pet, string line)
        {
            if (pet == null || !host.IsCompanionAlive(pet) || CompanionStoodDown(host, pet)) return;
            try { host.Say(pet, line); } catch { }
        }

        /// <summary>
        /// Remembrance's phase while its flag is set and fresh and this module's switch is on; null otherwise: clear,
        /// absent, stale, malformed, or the switch off, in which case the context is not read at all. A malformed or a
        /// stale value fails open and is logged once, so the same value read at every later decision adds no line,
        /// while a recurrence after a clean reading is logged again. Every reading also sets what the cloud+local
        /// composite's fallover consults (LocalFallbackHold). UI thread.
        /// </summary>
        private string RemembrancePhase()
        {
            AiSettings s = _settings;
            IHost host = _host;
            string phase = null;
            if (s != null && s.StandDownForRemembrance && host != null)
            {
                string raw;
                try { raw = host.ReadContext(RemembranceBusyFlag.Key); }
                catch { raw = ""; }
                string malformed;
                RemembranceBusyFlag flag = RemembranceBusyFlag.Parse(raw, out malformed);
                DateTime now = UtcNow();
                if (malformed != null)
                {
                    LogRemembranceValueOnce(raw, "remembrance.busy ignored: " + malformed);
                }
                else if (flag != null && !flag.IsFreshAt(now))
                {
                    LogRemembranceValueOnce(raw, "remembrance.busy ignored: it was published at " +
                        flag.AtUtc.ToString("o", CultureInfo.InvariantCulture) + ", more than " +
                        ((int)RemembranceBusyFlag.StaleAfter.TotalHours).ToString(CultureInfo.InvariantCulture) +
                        (flag.AtUtc > now ? " hours ahead of this clock" : " hours ago"));
                }
                else
                {
                    _remembranceLoggedValue = null;   // a clean reading: the next bad value is news again
                    if (flag != null) phase = flag.Phase;
                }
            }
            _remembranceBusyAtLastRead = phase != null;
            return phase;
        }

        /// <summary>The phase when it blocks a request to the LOCAL slot: RemembrancePhase on a local slot, and null on a
        /// cloud one, whose requests go ahead (Addendum 1: this reason protects the local GPU only).</summary>
        private string RemembranceBlockingPhase()
        {
            string phase = RemembrancePhase();
            return phase != null && IsLocalSlot(_settings) ? phase : null;
        }

        /// <summary>
        /// Why the cloud+local composite may NOT fall over to the local slot now, or null when it may: not while the UI
        /// thread's most recent reading of Remembrance's flag was busy, and since 1.4.0 not while its most recent reading
        /// of a fullscreen app said one was running (a cloud turn goes ahead during a game; its fallback would load the
        /// local model beside it). CreateBrain hands it to FallbackBackend, which calls it on the pool thread a cloud
        /// failure arrives on and logs the reason, so it answers from those readings rather than reading anything there.
        /// Every remark's decision takes both readings (the drop, the poke, the hotkey and the tray row all pass Ask's
        /// checks), as does an audition's press, so for a remark they are at the latest the ones its own turn started
        /// from; a game starting mid-turn is read by OnFullscreenChanged.
        /// </summary>
        private string LocalFallbackHold()
        {
            if (_remembranceBusyAtLastRead) return "Remembrance is using the local model";
            if (_fullscreenAtLastRead) return "a fullscreen app is running, and the local model would load beside it";
            return null;
        }

        /// <summary>Log a line about a published value once: the same value read again at a later decision is not
        /// logged again. UI thread.</summary>
        private void LogRemembranceValueOnce(string raw, string line)
        {
            if (string.Equals(raw, _remembranceLoggedValue, StringComparison.Ordinal)) return;
            _remembranceLoggedValue = raw;
            IHost host = _host;
            if (host == null) return;
            try { host.Log(Info.Id, line); } catch { }
        }

        /// <summary>Test seam: the clock the flag's freshness is judged by. Null in the shipped module; the self-test
        /// moves it past the 8-hour bound rather than waiting eight hours.</summary>
        internal Func<DateTime> UtcNowForDiagnostics;

        private DateTime UtcNow()
        {
            Func<DateTime> clock = UtcNowForDiagnostics;
            return clock != null ? clock() : DateTime.UtcNow;
        }

        /// <summary>The composite's fallover decision as the module answers it, for the self-test: null when it may fall
        /// over, else the reason it may not.</summary>
        internal string LocalFallbackHoldForDiagnostics() { return LocalFallbackHold(); }

        /// <summary>Poke responder: the first poke of a session becomes an AI quip about the screen when
        /// the brain is on. Declines when off, so Fortunes (or nothing) handles it instead. Text-only —
        /// a vision glance can take tens of seconds, far too slow to feel like a reaction to a click.</summary>
        private bool OnPokeReaction(ICompanion pet)
        {
            if (!_session.Enabled) return false;
            if (GpuGuardBlocks()) return false;   // same rule as the drop; Fortunes answers instead
            if (RemembranceBlockingPhase() != null) return false;   // and the drop's second reason, likewise
            return Ask(pet, false);
        }

        // ---- the ask flow (mirrors the old StartUp.AskAboutScreen) --------------------------------

        /// <summary>
        /// Kick off one screen-commentary turn for a specific pet, on the UI thread (the drop, the poke, the
        /// hotkey, the tray row). <paramref name="subject"/> is the pet this turn belongs to; the hotkey and the
        /// tray row have no natural one, so they pass null and fall back to the last pet seen. Returns TRUE only
        /// when a turn was actually STARTED. (Two summaries sat here, the first naming an idle tick that went in
        /// 1.2.3, RA-054.)
        ///
        /// The drop and poke responders forward this as their "handled" answer. The chain runs highest
        /// priority first until one handler returns true and this module registers at 10 to outrank
        /// Fortunes, so claiming a turn that was declined is what makes the pet go silent instead of
        /// falling through to a fortune. Both callers used to `Ask(...); return true;`, which reported
        /// "handled" for all four of the early returns below -- most often RequestInProgress, i.e. exactly
        /// while a slow vision load is already running and a second poke arrives.
        ///
        /// The two hotkey/tray callers use this as a statement and ignore the result, which is correct: there is
        /// no responder chain behind them to fall through to. That is also why THEIR refusals are logged and the
        /// responders' are not (RA-060, extending F067's one line to every early return): a declined drop falls
        /// through to a fortune the user hears, while a dropped hotkey press is silent to the user and, until
        /// 2026-09-30, to the log SUPPORT.md asks for.
        /// </summary>
        private bool Ask(ICompanion subject, bool allowVision)
        {
            IHost host = _host;
            AiSessionManager session = _session;
            bool explicitPath = subject == null;
            if (host == null) return false;
            if (!session.Enabled) return Declined(host, explicitPath, "brain off");
            if (!host.SpeechEnabled) return Declined(host, explicitPath, "speech off");
            // One in-flight ask at a time, so at most one pending subject. Per-pet concurrency (two pets
            // asked at once) is BACKLOG #16(a) and deliberately not attempted here.
            if (session.RequestInProgress) return Declined(host, explicitPath, "busy");
            // The graphics-card guard applies to EVERY entry point, not only the two responders. A global hotkey is
            // delivered while a game has focus, and the explicit path allows vision, so a press during a game
            // used to load the vision model beside the game the setting exists to protect (F067). The responders
            // keep their own call in front of this one: declining THERE is what lets the chain fall through to
            // Fortunes, and the source invariant asserts it there. The explicit path has no chain behind it, so a
            // refusal here would be silent to the user and to the log SUPPORT.md asks for; hence the line, for
            // every caller that reaches it, and since 1.4.0 the reason SAID to a companion in view. F067 chose the
            // log alone because "on a single monitor the companion is hidden while the game runs"; on two monitors
            // it is not, and a press that only logged read as broken (the owner, 2026-10-07). SayIfInView still
            // says nothing to a hidden companion, which is the single-monitor case F067 was right about.
            if (GpuGuardBlocks())
            {
                LogDeclined(host, "fullscreen stand-down");
                if (explicitPath) SayIfInView(host, _lastPet, FullscreenGpuSpokenLine);
                return false;
            }
            // The second reason, Remembrance running a local model (lane feature/aibrain-standdown), through the
            // explicit-path decline the other refusals use (RA-060) and under a category of its own, because "busy"
            // already means a turn in progress. On a cloud slot this answers null and the turn goes ahead; the reading
            // it took is what that turn's fallback decision sees (RemembrancePhase).
            //
            // This one is also SAID (SayRemembranceBusy), as the fullscreen refusal above has been since 1.4.0: the owner's
            // decision of 2026-10-02, because the companion stays on screen while Remembrance works, so a hotkey press that
            // only logged would read as broken. Said only to a companion in view (SayIfInView). Only the hotkey and
            // the tray row reach here in practice: the drop and the poke decline before they call Ask, silently, so
            // Fortunes answers (the order invariant in tests/runtime-hardening-selftest.ps1 pins that), and a
            // responder that lost its own check would SPEAK here instead of falling through, which is what the module
            // self-test's silent-drop check catches.
            string remembrancePhase = RemembranceBlockingPhase();
            if (remembrancePhase != null)
            {
                SayRemembranceBusy(host, subject ?? _lastPet);
                return Declined(host, explicitPath, "remembrance stand-down (" + remembrancePhase + ")");
            }
            ICompanion pet = subject ?? _lastPet;
            if (pet == null || !host.IsCompanionAlive(pet)) return Declined(host, explicitPath, "no companion");
            // The companion's own stand-down (1.4.0, host 1.5.0's ICompanionStandDown), on every engine: one hidden
            // with no free monitor would have its answer held until the game ends, so the turn is not started at all
            // and nothing is said (it could not be seen). One that moved to a free monitor is in view and answers,
            // which is the owner's rule of 2026-10-07. After the companion check, so a refusal names the right reason.
            if (CompanionStoodDown(host, pet))
                return Declined(host, explicitPath, "companion stood down for a fullscreen window");

            _lastInteractionUtc = DateTime.UtcNow;
            ScreenContext ctx;
            try { ctx = host.CaptureScreenContext(pet); } catch { ctx = null; }
            if (ctx == null) return Declined(host, explicitPath, "capture failed");

            // A "pondering" cue while the model responds (we are on the UI thread here). It belongs to the pet
            // being asked: PlayAnimationAll + SayAll made EVERY pet ponder a question only one of them was
            // asked, which is the same bug the answer below had.
            try { PlayEmotionOn(host, pet, "thinking"); host.Say(pet, "…"); } catch { }

            Func<ScreenContext, string, bool, ICompanion, Task> sink = AskSinkForDiagnostics;
            _ = sink != null
                ? sink(ctx, ctx.WindowUnderCompanion, allowVision, pet)
                : AskCoreAsync(session, ctx, ctx.WindowUnderCompanion, allowVision, pet);
            return true;
        }

        /// <summary>A refusal on the explicit path (the hotkey, the tray row) is said in the log with its category;
        /// a responder's is not, because the chain falls through to Fortunes and the user hears a fortune (RA-060).
        /// Always false: the turn was not started.</summary>
        private bool Declined(IHost host, bool explicitPath, string reason)
        {
            if (explicitPath) LogDeclined(host, reason);
            return false;
        }

        private void LogDeclined(IHost host, string reason)
        {
            try { host.Log(Info.Id, "ask declined: " + reason); } catch { }
        }

        /// <summary>
        /// Test seam: where a STARTED turn goes instead of <see cref="AskCoreAsync"/>. Null in the shipped
        /// module. The module self-test sets it to record the routing decision each entry point makes, which
        /// pet and whether vision was allowed, WITHOUT the turn reaching the session and a backend: the decision
        /// under test is made before this point, by OnDrop, OnPokeReaction, the tray row and the hotkey, and
        /// everything after it needs a screen, a model and a network. Same shape as
        /// AiSessionManager.ReconfigureAdmittedForDiagnostics. It exists because BUG-010 was a routing
        /// argument that four sentences described and nothing asserted.
        /// </summary>
        internal Func<ScreenContext, string, bool, ICompanion, Task> AskSinkForDiagnostics;

        /// <summary>Play the first animation this pet actually defines for an emotion. The module owns the
        /// emotion -&gt; candidates mapping, so this needs no host verb beyond TryPlayAnimation.</summary>
        private void PlayEmotionOn(IHost host, ICompanion pet, string emotion)
        {
            if (host == null || pet == null) return;
            foreach (string name in EmotionAnimations(emotion))
                if (host.TryPlayAnimation(pet, name)) break;
        }

        private async Task AskCoreAsync(AiSessionManager session, ScreenContext ctx, string petZone, bool allowVision, ICompanion subject)
        {
            BrainResponse r;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { r = await session.AskAsync(ctx, petZone, allowVision, _lifetime.Token).ConfigureAwait(false); }
            catch { r = null; }
            // The Status card's "last remark" (feature/cli-backend): when, how long, and on failure the brain's own
            // class for it (a category, never a message: the CLI's words can name an account).
            RecordRemark(DateTime.Now, clock.ElapsedMilliseconds,
                r != null && !string.IsNullOrWhiteSpace(r.Text) ? null : (session.LastAskFailure ?? "nothing came back"));
            if (r == null || string.IsNullOrWhiteSpace(r.Text)) return;

            // Apply on the UI thread: map the emotion to an animation, then speak.
            PostToUi(delegate
            {
                IHost host = _host;
                if (host == null) return;
                // The subject is carried through rather than re-read from _lastPet, which CompanionSpawned,
                // CompanionLanded and CompanionPoked all move -- and a model round trip is easily long enough for that to
                // happen. If the pet that asked is gone, DROP the answer. Handing it to a different pet is the
                // same bug wearing a hat: that pet showed no "…" and was never asked.
                if (!host.IsCompanionAlive(subject))
                {
                    host.Log(Info.Id, "answer dropped: the companion it was for is no longer on screen");
                    return;
                }
                try
                {
                    PlayEmotionOn(host, subject, r.Emotion);
                    host.Say(subject, r.Text);
                }
                catch { }
            });
        }

        // ---- state application (mirrors ApplyAiBrainState + ApplyAiTriggers) ----------------------

        /// <summary>Build/retire the backend and (re)arm the hotkey from the current settings. (The idle loop this
        /// used to name went in 1.2.3, F064.)</summary>
        private void ApplyState()
        {
            AiSettings s = _settings ?? new AiSettings();
            // Publish the user's name so other modules (the fortunes welcome) address them the same when the
            // brain is on; clear it when off so they fall back to their own default (the Windows user name).
            try { if (_host != null) _host.SetOwnerName((s.AiBrainEnabled && !string.IsNullOrWhiteSpace(s.UserName)) ? s.UserName.Trim() : ""); }
            catch { }
            string err = null;
            bool allowed = s.AiBrainEnabled && CanUse(s, out err);
            // The reason the brain is NOT being built, said once per Apply. `err` was computed and dropped, so
            // an invalid endpoint, a missing consent or a missing cloud model disabled the brain with nothing
            // in the log while the tray row still read "Disable AI" (F101, F099).
            if (s.AiBrainEnabled && !allowed && _host != null)
                try { _host.Log(Info.Id, "AI brain not started: " + err); } catch { }
            bool prepare = allowed && (s.AutoStartServer || s.WarmUpDesired);
            // A private copy, taken HERE on the UI thread. The factory below runs on a pool thread once the previous
            // brain has retired (up to ~2 s later), and it used to read the live instance: a pane Save in that
            // window rotated the key or replaced a collection under CreateBrain's read (F068, F100). The copy
            // cannot be saved.
            AiSettings forBrain = s.CloneForBrain();
            // Retire WITHOUT evicting when the replacement targets the same backend and models. Every Apply
            // rebuilds the brain (the persona is read from its settings clone, so a name or disposition edit must
            // reach a NEW brain, and a fingerprint that skipped the rebuild would have to know every field the
            // brain reads), but under "keep" or "server" residency the old brain's retirement also evicted a model
            // the user asked to keep resident, so a hotkey edit cost a cold reload (F095, F065). The fingerprint
            // names every field that decides WHICH model is resident WHERE and nothing else; under "unload" the
            // model is gone after each remark anyway, so the eviction on retire stays (it is free there). The
            // COMPARISON is the session's, made at retire time against the brain it actually has: recorded here,
            // when the Apply was issued, the fingerprint described the brain the session WOULD build, and an Apply
            // cancelled while queued behind an ask left its fingerprint behind for the next one to match (R-012).
            bool keepResident = !string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase);
            // While Remembrance runs a local model (lane feature/aibrain-standdown, Addendum 1): this rebuild warms nothing
            // and the brain it retires is disposed without its eviction, because AI Brain's model can be the very one
            // Remembrance is using on the same server. The first ask after the flag clears pays a cold start, and a model
            // the old brain kept resident is left to its own keep_alive (under "keep", until something unloads it).
            // Recorded under `#### feature/aibrain-standdown` in docs/DESIGN-REGISTER.md.
            bool leaveModelsAlone = RemembrancePhase() != null;
            Func<AiSettings, AiBrain> factorySeam = BrainFactoryForDiagnostics;

            // Fire-and-forget: the session serializes generations, so a stale config can never apply.
            _ = _session.ReconfigureForBackendAsync(
                allowed
                    ? (Func<AiBrain>)delegate
                    {
                        return factorySeam != null
                            ? factorySeam(forBrain)
                            : CreateBrain(forBrain, forBrain.KeepAliveForRequests, LocalFallbackHold, _cli);
                    }
                    : null,
                allowed,
                prepare,
                _lifetime.Token,
                allowed ? BackendFingerprint(s) : null,
                keepResident,
                leaveModelsAlone);

            if (_hotkey != null) { try { _hotkey.Dispose(); } catch { } _hotkey = null; }
            if (allowed && s.HotkeyEnabled && _host != null)
                _hotkey = _host.RegisterHotkey(s.Hotkey, delegate { Ask(null, true); });

        }

        // ---- brain construction (mirrors StartUp.CreateBrain / CanUseAiConfiguration) -------------

        /// <summary>
        /// Every setting that decides which model is resident where: the local slot's endpoint, protocol and two
        /// models, the cloud selector with its endpoint and models, the fallback switch, the residency, the
        /// executable path, and whether vision is on. UseVision joined on 2026-09-30 (R-011): it decides which of the
        /// two models the warm-up and every ask load, so under "keep" a vision toggle left the vision model resident
        /// for nothing until shutdown; the cost is one cold reload on that toggle, which is the eviction the residency
        /// otherwise never gets. Persona fields are deliberately absent: the brain is rebuilt on every Apply
        /// regardless, and this decides only whether its retirement EVICTS (F095). Handed to the session, which owns
        /// the live brain's fingerprint and compares at retire time (R-012). Internal so the self-test can pin what
        /// is and is not in it.
        /// </summary>
        internal static string BackendFingerprint(AiSettings s)
        {
            return string.Join("\n", new[]
            {
                s.Endpoint ?? "", s.LocalBackendKind ?? "", s.TextModel ?? "", s.VisionModel ?? "",
                s.Provider ?? "", s.OpenAiBaseUrl ?? "", s.CloudTextModel ?? "", s.CloudVisionModel ?? "",
                s.UseLocalFallback ? "fallback" : "no-fallback", s.ModelResidency ?? "", s.OllamaPath ?? "",
                s.UseVision ? "vision" : "text", s.CliBackend ?? "",
            });
        }

        /// <summary>
        /// The keep_alive the AUDITION brain's Ollama client sends. Under the default "unload" residency every chat
        /// carries keep_alive:0, so five back-to-back samples raced Ollama's eviction and could pay up to four extra
        /// loads, about 5 s each on gemma3:4b against about 400 ms of inference (F070; the repo's one recorded
        /// audition timing, 5498/3803/419/380/385 ms, shows the race being won three times out of five). The
        /// audition is a burst the module itself issues, so it holds the model for a minute between samples and
        /// PreviewDispositionAsync evicts explicitly when the run ends. The other residencies keep their own value:
        /// "keep" is resident anyway and "server" defers to Ollama.
        /// </summary>
        internal static int? AuditionKeepAliveSeconds(AiSettings s)
        {
            return string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase)
                ? (int?)AuditionKeepAliveWindowSeconds
                : s.KeepAliveForRequests;
        }

        private const int AuditionKeepAliveWindowSeconds = 60;

        /// <summary>The live settings, for the module self-test only: it reads the seeded endpoint (RA-072) and
        /// flips the residency between audition presses (RA-073).</summary>
        internal AiSettings SettingsForDiagnostics { get { return _settings; } }

        /// <summary>
        /// Test seam: builds the audition's brain in place of <see cref="CreateBrain(AiSettings, int?, Func{bool})"/>, handed
        /// the settings and the keep_alive the audition decided on. Null in the shipped module. It exists because
        /// the audition's three decisions (the keep_alive window, no warm-up, the eviction at the end) are made in
        /// RunAuditionAsync and were asserted only on the helpers they call, so reverting any of the three call
        /// sites compiled and left both self-test flags green (RA-073). Same shape as AskSinkForDiagnostics.
        /// </summary>
        internal Func<AiSettings, int?, AiBrain> AuditionBrainFactoryForDiagnostics;

        /// <summary>
        /// Test seam: builds the LIVE brain in place of CreateBrain, handed the private settings copy ApplyState took.
        /// Null in the shipped module; set before Init, which is the first ApplyState. It exists so the Remembrance
        /// stand-down checks can put a counting double behind the session and assert what reaches a backend (a
        /// reachability probe, a warm-up, an eviction) and what never does, with no server of any kind. Same shape as
        /// AuditionBrainFactoryForDiagnostics.
        /// </summary>
        internal Func<AiSettings, AiBrain> BrainFactoryForDiagnostics;

        /// <summary>How long the audition waits for its end-of-run eviction before handing the pane its text:
        /// RetireBrainAsync's own bound, for the same reason (RA-056).</summary>
        private static readonly TimeSpan AuditionUnloadBudget = TimeSpan.FromSeconds(2);

        /// <summary>Evict the brain's models within a budget. An unload still pending afterwards is observed and
        /// left to the brain's disposal, which follows at once in the caller's using block and aborts it (RA-056).
        /// Internal so the self-test can drive it against a backend whose unload never completes.</summary>
        internal static async Task UnloadWithinAsync(AiBrain brain, TimeSpan budget, CancellationToken ct)
        {
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                bounded.CancelAfter(budget);
                Task unload;
                try { unload = brain.UnloadAsync(bounded.Token); }
                catch { return; }
                Task completed = await Task.WhenAny(unload, Task.Delay(budget, ct)).ConfigureAwait(false);
                if (completed == unload)
                {
                    try { await unload.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { }
                }
                else
                {
                    AiEndpointPolicy.ObserveTaskFailure(unload);
                }
            }
        }

        /// <summary>
        /// The "Status" row of the pane: whether the brain is off, started, or NOT started and why. CanUse's refusal
        /// (an invalid endpoint, consent missing, a cloud slot with no model: the two F101 gates) had no channel
        /// but the diagnostic log: Save reported success, the tray offered an inert "Ask about my screen" (hidden
        /// now: the row is gated on the session, not on the stored switch) and nothing on the pane said why remarks
        /// never came (R-013). Read from the SETTINGS rather than the session, so it is right the moment the pane
        /// reloads after Apply and needs no wait on the pool thread that builds the brain.
        /// </summary>
        internal static string BrainStatusLine(AiSettings s)
        {
            return BrainStatusLine(s, null);
        }

        /// <param name="remembrancePhase">The phase Remembrance's flag names while it blocks the local slot
        /// (RemembranceBlockingPhase, read from the shared context at the pane's Load), or null. Said only for a brain that
        /// would otherwise read "On.": one that is off or not started has nothing to stand down, and its own line is the
        /// one to read.</param>
        internal static string BrainStatusLine(AiSettings s, string remembrancePhase)
        {
            if (s == null) return "No settings.";
            if (!s.AiBrainEnabled) return "Off. Tick \"Enable AI brain\" and Apply to start it.";
            string why;
            if (!CanUse(s, out why)) return "Not started: " + why;
            if (remembrancePhase != null) return "Standing down while Remembrance is " + remembrancePhase + ".";
            return "On.";
        }

        // internal, not private: the self-test builds a cloud-primary brain from settings alone (no network is
        // touched by construction) to assert what the failure line will name as its endpoint (F073).
        internal static AiBrain CreateBrain(AiSettings s)
        {
            return CreateBrain(s, s.KeepAliveForRequests);
        }

        /// <param name="localKeepAliveSeconds">The keep_alive the LOCAL Ollama client puts on each chat request: the
        /// residency's own value for the live brain, a short positive window for the audition brain (F070).</param>
        /// <param name="localFallbackHold">Asked by the cloud+local composite before each fallover: the reason it may
        /// not fall over, or null when it may; a null delegate always allows one. The module passes LocalFallbackHold,
        /// so a fallover waits while Remembrance is busy or (1.4.0) a fullscreen app runs (lane
        /// feature/aibrain-standdown).</param>
        /// <param name="cli">The module's coding-agent CLI runner, which a CLI slot's backend calls through (lane
        /// feature/cli-backend). Null builds a runner with no folder, whose every call answers "no data folder".</param>
        internal static AiBrain CreateBrain(AiSettings s, int? localKeepAliveSeconds, Func<string> localFallbackHold = null,
            CodingAgentCli cli = null)
        {
            // A CLI slot first: it has no endpoint, consent or model of the user's, so none of the checks below apply.
            // Its brain names the CLI where the others name a host, never substitutes (there is nothing to list), and
            // its calls are bounded by the same per-request timeout as any backend's.
            if (IsCliSlot(s))
            {
                CodingAgentKind agent = CodingAgents.FromId(s.CliBackend);
                var cliBackend = new CodingAgentBackend(cli, agent, TimeSpan.FromSeconds(s.TimeoutSeconds));
                AiBrain cliBrain = new AiBrain(cliBackend, s.ActiveSlotSnapshot());
                cliBrain.BackendHostDescription = CodingAgents.IdOf(agent) + "-cli";
                cliBrain.SubstituteMissingModel = false;
                cliBrain.ModelLister = null;
                return cliBrain;
            }
            string endpoint = SelectedEndpoint(s);
            string normalized, error;
            if (!AiEndpointPolicy.TryNormalize(endpoint, out normalized, out error))
                throw new InvalidDataException(error);
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
                throw new InvalidOperationException("Cloud data consent is required for a non-local AI endpoint.");

            TimeSpan timeout = TimeSpan.FromSeconds(s.TimeoutSeconds);
            // The host(s) the brain will name in its log lines: the endpoint it was built against, plus the
            // local leg when one is wrapped in below. The snapshot the brain receives keeps the LOCAL Endpoint
            // for a cloud-primary brain, so without this every line said "endpoint=localhost" (F073).
            string backendHosts = AiBrain.DescribeEndpoint(normalized);
            // No cloud selected (Provider == "") -> the LOCAL backend (BuildLocalBackend: Ollama-native or a
            // generic OpenAI-compatible /v1 server per LocalBackendKind). A cloud selector -> the OpenAI-
            // compatible backend with the cloud-scoped key; and when "use local as fallback" is on and the
            // local slot is a valid loopback endpoint, wrap it in a FallbackBackend so a retryable cloud
            // failure fails over to the local model. The brain's settings snapshot carries the active
            // (primary) slot's models; the composite maps to the local models on fallback.
            ICompanionBrainBackend backend;
            if (IsLocalSlot(s))
            {
                backend = BuildLocalBackend(s, normalized, timeout, localKeepAliveSeconds);
            }
            else
            {
                ICompanionBrainBackend cloud = new OpenAiCompatBackend(normalized, s.ApiKey, timeout);
                string localNormalized, localError;
                if (s.UseLocalFallback &&
                    AiEndpointPolicy.TryNormalize(s.Endpoint, out localNormalized, out localError) &&
                    AiEndpointPolicy.IsLoopbackEndpoint(localNormalized))
                {
                    ICompanionBrainBackend local = BuildLocalBackend(s, localNormalized, timeout, localKeepAliveSeconds);
                    backend = new FallbackBackend(cloud, local, s.CloudVisionModel, s.TextModel, s.VisionModel)
                    {
                        LocalFallbackHold = localFallbackHold,
                    };
                    backendHosts += "->" + AiBrain.DescribeEndpoint(localNormalized);
                }
                else
                {
                    backend = cloud;
                }
            }
            AiBrain brain = new AiBrain(backend, s.ActiveSlotSnapshot());
            brain.BackendHostDescription = backendHosts;
            // Substitution (BUG-002: a configured model the backend lacks is replaced by the first usable listing) is a
            // courtesy for a local backend, where the first listing is free. A cloud primary bills every request, so a
            // model the user never chose is not sent there: the ask ends on an advisory naming the host (R-022).
            brain.SubstituteMissingModel = IsLocalSlot(s);
            // Let the brain re-validate its configured model against what the backend actually offers
            // (BUG-002). Uses the SAME listing call the Options pane uses, so the two can never disagree
            // about what is installed. Captures the backend the brain owns, not a fresh one.
            brain.ModelLister = delegate(CancellationToken ct) { return ListBackendModelsAsync(backend, ct); };
            return brain;
        }

        /// <summary>
        /// List what a backend offers, or NULL when that backend cannot enumerate. Null and empty mean different
        /// things downstream: <see cref="AiModelPolicy.ChooseModel"/> treats an unknown inventory as "do not
        /// complain", so a backend that cannot enumerate must not be able to make the brain claim a model is
        /// missing. Through <see cref="IModelLister"/> rather than two type tests: the type tests answered null
        /// for the cloud+local composite, i.e. for every cloud user who left the default fallback on, so a
        /// removed or mistyped cloud id was never re-validated and BUG-002's spoken advisory never fired on the
        /// configuration it was written for (F103).
        /// </summary>
        internal static async Task<IReadOnlyList<ModelListing>> ListBackendModelsAsync(
            ICompanionBrainBackend backend, CancellationToken ct)
        {
            IModelLister lister = backend as IModelLister;
            if (lister == null) return null;
            return await lister.ListModelsAsync(ct).ConfigureAwait(false);
        }

        // internal, not private: the self-test asserts the cloud-slot rule below without a host.
        internal static bool CanUse(AiSettings s, out string error)
        {
            error = null;
            if (s == null) { error = "AI settings are unavailable."; return false; }
            // A CLI slot needs nothing configured here: no endpoint, no model, and no consent switch, because choosing
            // the CLI is the consent (owner decision, 2026-10-06: "If the feature is enabled, the user is well aware").
            // Whether the CLI is installed and signed in is the CLI card's to say, at use (feature/cli-backend).
            if (IsCliSlot(s)) return true;
            string normalized;
            if (!AiEndpointPolicy.TryNormalize(SelectedEndpoint(s), out normalized, out error)) return false;
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
            {
                error = "Approve cloud data sharing before using a non-local AI endpoint.";
                return false;
            }
            // A cloud slot has no model until the user picks one: the dropdown is empty until "Refresh cloud
            // models" runs, so a first-time cloud setup naturally leaves it blank, and the brain used to fill
            // the blank with the LOCAL default "gemma3:4b" and send that to the provider: HTTP 400 on every
            // remark, logged as unreachable, or with the fallback off a silent substitution to whatever the
            // provider listed first, billed to the user's key (F101). Refused here, where ApplyState logs the
            // reason, rather than discovered one remark at a time.
            if (!IsLocalSlot(s))
            {
                if (string.IsNullOrWhiteSpace(s.CloudTextModel))
                {
                    error = "Pick a cloud text model first (Refresh cloud models, then choose one).";
                    return false;
                }
                if (s.UseVision && string.IsNullOrWhiteSpace(s.CloudVisionModel))
                {
                    error = "Pick a cloud vision model first (Refresh cloud models, then choose one), or turn vision off.";
                    return false;
                }
            }
            return true;
        }

        // Schema v2: the LOCAL slot is active (Ollama at Endpoint) when no cloud provider is selected
        // (Provider == ""); any cloud selector (openai/openrouter/custom) makes the cloud slot primary.
        // A CLI slot is neither (feature/cli-backend): it wins over the cloud selector, and nothing on it is local, so the
        // Remembrance stand-down, which protects the local GPU, never applies there.
        private static bool IsLocalSlot(AiSettings s)
        {
            return s == null || (!IsCliSlot(s) && string.IsNullOrEmpty(s.Provider));
        }

        internal static bool IsCliSlot(AiSettings s)
        {
            return s != null && CodingAgents.FromId(s.CliBackend) != CodingAgentKind.None;
        }

        private static string SelectedEndpoint(AiSettings s)
        {
            return IsLocalSlot(s) ? s.Endpoint : s.OpenAiBaseUrl;
        }

        /// <summary>
        /// Build the LOCAL backend for a normalized local endpoint: the native <see cref="OllamaClient"/> (its
        /// lifecycle features — auto-start/warm-up/unload via <see cref="AiSettings.OllamaPath"/> — only make
        /// sense here) when <see cref="AiSettings.LocalBackendKind"/> is Ollama-native, else a generic
        /// <see cref="OpenAiCompatBackend"/> (llama.cpp/LM Studio/other — those lifecycle calls are already
        /// harmless no-ops on that backend) with no key, since local servers don't need one. Shared by
        /// <see cref="TestConnectionAsync"/> and both local-backend sites in <see cref="CreateBrain"/> so the
        /// LOCAL slot is never hardcoded to one protocol.
        /// </summary>
        // The residency dropdown. Labels carry the trade-off so the choice is legible without a help panel;
        // the STORED value is the stable token, so rewording a label cannot invalidate a saved setting.
        private const string ResidencyUnloadLabel = "Unload after each remark (frees VRAM)";
        private const string ResidencyKeepLabel = "Keep loaded while the app runs (fastest)";
        private const string ResidencyServerLabel = "Leave it to Ollama";

        internal static string[] ResidencyLabels()
        {
            return new[] { ResidencyUnloadLabel, ResidencyKeepLabel, ResidencyServerLabel };
        }

        internal static string ResidencyLabel(string stored)
        {
            if (string.Equals(stored, AiSettings.ResidencyKeep, StringComparison.OrdinalIgnoreCase)) return ResidencyKeepLabel;
            if (string.Equals(stored, AiSettings.ResidencyServer, StringComparison.OrdinalIgnoreCase)) return ResidencyServerLabel;
            return ResidencyUnloadLabel;
        }

        // An unrecognised label falls back to the DEFAULT rather than throwing or storing the label text: the
        // pane is the only thing that produces these, so anything else means the schema moved under us.
        internal static string ResidencyFromLabel(string label)
        {
            if (string.Equals(label, ResidencyKeepLabel, StringComparison.Ordinal)) return AiSettings.ResidencyKeep;
            if (string.Equals(label, ResidencyServerLabel, StringComparison.Ordinal)) return AiSettings.ResidencyServer;
            return AiSettings.ResidencyUnload;
        }

        /// <summary>
        /// What is resident in VRAM right now, read from the server rather than asserted.
        ///
        /// Also states the reload cost, which is real and is paid per remark under "unload": better said here than
        /// discovered as lag. This comment used to describe a second thing, "Preload model on launch" pinning
        /// keep_alive to 10 minutes and outliving a short eject window; that setting was folded into the residency
        /// choice in 1.3.0 and the last trace of its 10-minute pin, in OllamaClient.WarmUpAsync, went with F106.
        /// </summary>
        private string VramStatusLine(AiSettings s)
        {
            // No "these two settings fight each other" paragraph any more: there is one setting, and it cannot
            // disagree with itself. What is left is the honest cost of the choice actually made.
            string cost;
            if (string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase))
                // "a second or two" was optimistic by roughly an order of magnitude. Measured on
                // gemma3:4b, the recommended and smallest sensible model: cold load about 5 seconds on
                // the text path and about 11 with an image, against ~60ms and ~400ms warm. A bigger
                // model is worse. Quoting the real range matters because this setting's whole purpose
                // is trading latency for VRAM, and a user cannot make that trade against a wrong number.
                cost = "  The model reloads for each remark, which costs roughly five seconds — ten or more " +
                       "for a vision glance, and longer on a larger model — that is the trade for the VRAM.";
            else if (string.Equals(s.ModelResidency, AiSettings.ResidencyKeep, StringComparison.OrdinalIgnoreCase))
                cost = "  The model stays loaded for the whole session, so remarks are instant and the VRAM is held throughout.";
            else
                cost = "  Ollama decides (documented as 5 minutes, unless OLLAMA_KEEP_ALIVE is set on this machine).";

            if (!string.Equals(s.LocalBackendKind, "ollama", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.LocalBackendKind))
                return "This setting is Ollama-only; the selected local backend has no equivalent.";

            // Serve what we last learned, and start a refresh if it has gone stale. Never wait.
            string cached = _vramResident;
            long stamp = Interlocked.Read(ref _vramStampTicks);
            bool stale = stamp == 0 ||
                (DateTime.UtcNow - new DateTime(stamp, DateTimeKind.Utc)).TotalSeconds > VramFreshnessSeconds;
            if (stale) BeginVramProbe(s);
            return (cached ?? "Checking what is resident…") + cost;
        }

        /// <summary>
        /// Refresh <see cref="_vramResident"/> on a pool thread. At most one in flight: a pane that
        /// rebuilds three times in a row must not start three probes, each holding a socket for two
        /// seconds against a server that is not there.
        /// </summary>
        private void BeginVramProbe(AiSettings s)
        {
            if (Interlocked.CompareExchange(ref _vramProbing, 1, 0) != 0) return;
            Task.Run(delegate
            {
                try { _vramResident = ProbeVramResident(s); }
                catch { _vramResident = "Could not ask the server what is resident (it may not be running)."; }
                finally
                {
                    Interlocked.Exchange(ref _vramStampTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Exchange(ref _vramProbing, 0);
                }
            });
        }

        /// <summary>The network half of the VRAM line, with no UI thread anywhere near it.</summary>
        private static string ProbeVramResident(AiSettings s)
        {
            try
            {
                using (var client = new OllamaClient(
                    AiEndpointPolicy.NormalizeOrThrow(
                        string.IsNullOrWhiteSpace(s.Endpoint) ? "http://localhost:11434" : s.Endpoint,
                        "endpoint"),
                    TimeSpan.FromSeconds(2),
                    s.OllamaPath))
                {
                    IReadOnlyList<OllamaClient.RunningModel> running =
                        client.RunningModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (running == null || running.Count == 0)
                        return "Nothing resident — no model is holding VRAM.";

                    var sb = new StringBuilder();
                    foreach (OllamaClient.RunningModel m in running)
                    {
                        if (sb.Length > 0) sb.Append("; ");
                        sb.Append(m.Name);
                        if (m.VramBytes > 0)
                            sb.Append(" (").Append((m.VramBytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)).Append(" GB");
                        else sb.Append(" (VRAM unreported");
                        if (m.ExpiresAt.HasValue)
                        {
                            double secs = (m.ExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
                            sb.Append(", ").Append(secs <= 0
                                ? "evicting now"
                                : "evicts in " + Math.Round(secs) + "s");
                        }
                        sb.Append(')');
                    }
                    return sb.ToString();
                }
            }
            catch
            {
                // Not reachable is a legitimate answer, not an error to explain: the server may simply be off.
                return "Could not ask the server what is resident (it may not be running).";
            }
        }

        // internal, not private: this is the seam where a settings value becomes a live client, and mutation
        // testing showed nothing covered it -- breaking the propagation was SILENT. A correct setting nobody
        // plumbs through is the exact failure this project's rule about source-text checks warns about.
        internal static ICompanionBrainBackend BuildLocalBackend(AiSettings s, string normalizedLocalEndpoint, TimeSpan timeout)
        {
            return BuildLocalBackend(s, normalizedLocalEndpoint, timeout, s.KeepAliveForRequests);
        }

        /// <param name="keepAliveSeconds">What the Ollama client puts on each chat request; the residency's value
        /// for the live brain (the overload above), a short window for the audition brain (F070).</param>
        internal static ICompanionBrainBackend BuildLocalBackend(
            AiSettings s, string normalizedLocalEndpoint, TimeSpan timeout, int? keepAliveSeconds)
        {
            if (string.Equals(s.LocalBackendKind, "openai-compat", StringComparison.OrdinalIgnoreCase))
                return new OpenAiCompatBackend(normalizedLocalEndpoint, "", timeout);
            // keep_alive is an Ollama-native field, so the residency setting only reaches the Ollama client.
            // On an OpenAI-compat server it has no equivalent and is silently not applied, which is why the
            // pane says the setting is Ollama-only rather than appearing to work everywhere.
            return new OllamaClient(normalizedLocalEndpoint, timeout, s.OllamaPath)
            {
                KeepAliveSeconds = keepAliveSeconds,
            };
        }

        /// <summary>
        /// Prioritized candidate animations per emotion; the host plays the first one each pet's XML
        /// defines. Neutral/unknown => no forced animation.
        ///
        /// CHOSEN BY COVERAGE, and the original names are kept FIRST so no companion that already
        /// reacted changes what it plays. The tail is a fallback for the pets that silently did
        /// nothing, because StartUp.PlayAnimationOnAll tries each candidate and then does nothing at
        /// all -- so a list that misses is indistinguishable from a feature that is switched off.
        ///
        /// MEASURED 2026-09-28 across all 54 Companions/*/animations.xml, case-insensitively, the
        /// same comparison FormCompanion.TryPlayAnimation uses:
        ///
        ///     emotion    shipped candidates      reached   with the fallback
        ///     happy      flower, jump, boing       18/54          42/54
        ///     excited    run, jump, boing          35/54          42/54
        ///     sad        sleep1a, sleep2a           8/54          39/54
        ///     thinking   sleep1a                    8/54          40/54
        ///     confused   rotate1a, boing            8/54          43/54
        ///
        /// The old table came from the eSheep-era StartUp code, and 32 of this corpus are converted
        /// shimeji with entirely different names. "thinking" fires on EVERY ask, immediately before
        /// the reply, so on 46 of 54 companions the reaction users were told about never happened.
        /// boing exists on exactly ONE companion; flower on one.
        ///
        /// NOTHING REACHES ALL 54, and that is not a list that can be improved into existence: the
        /// intersection across this corpus is EMPTY, and the four names on 50+ pets are the engine's
        /// reserved lifecycle animations -- fall 53, drag 52, kill 51, sync 50 -- so a "safe" list
        /// built from them would offer to play the dying animation. AgentFlow's PetAnimations.cs
        /// records the same measurement for its own dropdown and reached the same conclusion.
        /// </summary>
        private static string[] EmotionAnimations(string emotion)
        {
            if (string.IsNullOrWhiteSpace(emotion)) return NoAnimation;
            switch (emotion.Trim().ToLowerInvariant())
            {
                case "happy":    return new string[] { "flower", "jump", "boing", "bounce", "run", "walk" };
                case "excited":  return new string[] { "run", "jump", "boing", "dash", "bounce", "walk" };
                case "sad":      return new string[] { "sleep1a", "sleep2a", "sit", "sit_down", "sitwithlegsup", "stand" };
                case "thinking": return new string[] { "sleep1a", "sit", "sit_down", "stand", "turn" };
                case "confused": return new string[] { "rotate1a", "boing", "turn", "tripping", "stand", "walk" };
                default:         return NoAnimation;
            }
        }

        /// <summary>
        /// The engine's reserved lifecycle animations. They are the ONLY names present on nearly
        /// every companion, which makes them the tempting answer to "what is safe to play" and the
        /// wrong one: playing `kill` as a reaction to a cheerful reply is not a coverage win.
        /// </summary>
        internal static readonly string[] ReservedLifecycleAnimations =
            new string[] { "fall", "drag", "kill", "sync", "spawn" };

        /// <summary>The emotions this module maps, for the self-test to iterate rather than restate.</summary>
        internal static readonly string[] MappedEmotions =
            new string[] { "happy", "excited", "sad", "thinking", "confused" };

        /// <summary>Test seam: the candidate list for an emotion, without reaching a live host.</summary>
        internal static string[] EmotionAnimationsForSelfTest(string emotion)
        {
            return EmotionAnimations(emotion);
        }

        /// <summary>One-time, non-destructive migration: if the module has no settings yet but the base
        /// ai-settings.json exists, copy it (including the DPAPI-encrypted keys, decryptable by the same
        /// Windows user) into the module store. The base file is left intact: the host reads it once more for
        /// the random-drop bridge (LocalData.MigrateRandomDropIfAbsent). Any Fortunes-era keys it carries land
        /// in this module's ExtensionData, inert (F093; the base has not read them since the Fortunes module
        /// took its own settings).</summary>
        private static void MigrateFromBaseIfNeeded(string moduleDir)
        {
            try
            {
                string moduleFile = Path.Combine(moduleDir, "ai-settings.json");
                if (File.Exists(moduleFile)) return;

                string baseRoot = Environment.GetEnvironmentVariable("DESKTOP_AI_COMPANION_DATA_ROOT");
                if (string.IsNullOrWhiteSpace(baseRoot))
                    baseRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DesktopAICompanion");
                string baseFile = Path.Combine(baseRoot, "ai-settings.json");
                if (!File.Exists(baseFile)) return;

                File.Copy(baseFile, moduleFile, false);
            }
            catch { }
        }

        private void PostToUi(Action action)
        {
            if (action == null) return;
            SynchronizationContext ui = _ui;
            if (ui != null) ui.Post(delegate { action(); }, null);
            else action();
        }


        /// <summary>
        /// The module's own self-test, for <c>--module-selftest=aibrain</c>.
        ///
        /// A DELEGATION, not a second set of assertions: AiEngineProbe.Run already holds them, and the host's
        /// dedicated flag has run it for a long time. What was missing was the entry point ON THE MODULE
        /// TYPE, which is what the convention looks for -- so this module reported "the module exposes
        /// static bool SelfTest(out string detail)" and stopped after four generic checks, while its real
        /// assertions ran only under the other flag.
        ///
        /// Safe to name this SelfTest now. It used not to be: the harness once took the FIRST
        /// <c>bool SelfTest(out string)</c> anywhere in the assembly, so a helper could beat the module's
        /// own entry point (Reminder had six). That was fixed -- ModuleConventionSelfTest resolves it
        /// against the module TYPE -- and the comments elsewhere warning about it are stale.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            return AiEngineProbe.Run(out detail);
        }
        public void Shutdown()
        {
            IHost host = _host;
            if (host != null)
            {
                host.CompanionSpawned -= OnPetSeen;
                host.CompanionLanded -= OnPetSeen;
                if (_fullscreenChanged != null)
                {
                    try { host.FullscreenChanged -= _fullscreenChanged; } catch { }
                    _fullscreenChanged = null;
                }
                host.CompanionPoked -= OnPetPoked;
            }
            if (_dropResponder != null) { try { _dropResponder.Dispose(); } catch { } _dropResponder = null; }
            if (_pokeResponder != null) { try { _pokeResponder.Dispose(); } catch { } _pokeResponder = null; }
            if (_hotkey != null) { try { _hotkey.Dispose(); } catch { } _hotkey = null; }
            try { _lifetime.Cancel(); _lifetime.Dispose(); } catch { }
            try { _session.Dispose(); } catch { }
            // The sink is a STATIC field holding a delegate over this instance. Leaving it set would keep
            // the module alive past unload and route lines at a nulled host, so it is cleared here. The
            // delegate re-reads _host each call, so an in-flight line during teardown is a no-op, not a
            // throw.
            AiBrain.LogSink = null;
            _host = null;
        }
    }
}
