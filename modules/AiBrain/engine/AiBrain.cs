using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;   // ABI ScreenContext / PixelRect (replaces the base ScreenCaptureContext)
using System.Text.Json.Nodes;
using DesktopAICompanion.ModuleKit;   // AtomicFile / CrossSessionLock / UnicodeTextProgress

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Orchestrates one "look at the screen and react" turn:
    /// capture -> (OCR text | downscaled image) -> Ollama -> parse {text, emotion}.
    /// Purely additive: it observes the screen and calls the backend; it never touches the
    /// pet physics engine. Any failure results in a null response (the pet stays silent).
    /// </summary>
    internal sealed class AiBrain : IDisposable
    {
        private readonly ICompanionBrainBackend _backend;

        /// <summary>The backend this brain owns, for the self-test only: it asserts what AiBrainModule.CreateBrain wired
        /// into the cloud+local composite (the Remembrance fallback hold, lane feature/aibrain-standdown).</summary>
        internal ICompanionBrainBackend BackendForDiagnostics { get { return _backend; } }

        private readonly AiSettings _settings;
        private readonly string _textModel;
        private readonly string _visionModel;
        private readonly bool _useVision;
        // Not readonly since 2026-09-30: ForgetTesseractResolution takes the path the user has just chosen (R-015).
        private string _tesseractPath;

        /// <summary>
        /// The resolved Tesseract executable, or null, once <see cref="_tesseractResolved"/>. Resolving walks a
        /// configured path, two install directories and every PATH entry, and it ran on every ask, twice (F077).
        /// The answer changes only when Tesseract is installed or removed; every Apply builds a new brain, which
        /// resolves afresh, and the "Test OCR" button resolves afresh on purpose. Asks are serialized per brain by
        /// the session, so a race here can at worst resolve twice.
        /// </summary>
        private string _resolvedTesseract;
        private bool _tesseractResolved;
        /// <summary>How many times this brain walked the resolution; the self-test asserts one.</summary>
        internal int TesseractResolutionsForDiagnostics;

        /// <summary>
        /// Where this engine's diagnostic lines go. <c>AiBrainModule</c> points it at
        /// <c>IHost.Log(Info.Id, ...)</c>; left null (self-tests, the probe) the lines are discarded.
        ///
        /// Static, and deliberately so: the engine is constructed in several places (the live module, the
        /// probe, the self-test doubles) and threading a sink through every constructor would change more
        /// call sites than it is worth. Follows the host's own <c>Animations.SoundSink</c> precedent.
        ///
        /// NEVER pass prompt text, capture content, OCR output, model replies or API keys to this. The
        /// diagnostic log is what SUPPORT.md tells users to attach to an issue, and this is the one module
        /// where careless logging would export the contents of the user's screen. Model ids, endpoint
        /// HOSTS, sizes, latencies and error categories only.
        /// </summary>
        internal static Action<string> LogSink;

        /// <summary>
        /// How this brain finds out what the backend actually offers. <c>AiBrainModule</c> points it at the
        /// same listing call the Options pane uses; left null (the probe, the self-test doubles) the list
        /// stays unknown, which <see cref="AiModelPolicy.ChooseModel"/> treats as "do not complain".
        /// </summary>
        internal Func<CancellationToken, Task<IReadOnlyList<ModelListing>>> ModelLister { get; set; }

        /// <summary>
        /// Asked before a turn about the screen pays for a capture: true when the model the backend will send to is one
        /// that takes NO images by its own catalog's word (lane feature/cli-model-effort: a Codex model the user chose in
        /// the CLI card, CodingAgentBackend.ChosenModelTakesNoImages). Null, the default, for every other backend. See
        /// <see cref="SendsScreenshot"/>.
        /// </summary>
        internal Func<bool> ModelTakesNoImages { get; set; }

        /// <summary>Whether the "reads the screen as text" line has been logged for this brain: once per brain (a brain
        /// lives until the next Apply), not once per remark.</summary>
        private int _textOnlyModelLogged;

        /// <summary>
        /// Whether a turn about the screen sends a screenshot: Use vision on, the caller allowing it (the poke does not),
        /// and the model able to take one. A model whose own catalog says it takes no images is read the screen as TEXT,
        /// with the OCR the text path runs, which is what the pane's "When OCR is used" row says happens to a remark whose
        /// model cannot see. Chosen over ending the turn on an advisory (ChooseModel's rule for a cloud primary's blind
        /// model, R-022) because nothing here is swapped: the model is still the one the user chose, it is only sent what
        /// it can read, so the companion keeps talking on the model the user picked to save usage. The shared runner
        /// refuses such a screenshot as ModelCannotSee all the same, as a backstop for a catalog this brain had not seen
        /// cached yet (the very first Codex call fetches it). Decided before the capture, so the capture is the text
        /// path's width and nothing is sent that would be refused.
        /// </summary>
        internal bool SendsScreenshot(bool allowed)
        {
            if (!allowed || !_useVision) return false;
            Func<bool> takesNoImages = ModelTakesNoImages;
            bool blind;
            try { blind = takesNoImages != null && takesNoImages(); }
            catch { blind = false; }
            if (!blind) return true;
            if (Interlocked.Exchange(ref _textOnlyModelLogged, 1) == 0)
                Log("vision: the model chosen takes no images (its catalog says so); the screen is read as text");
            return false;
        }

        /// <summary>Last known backend inventory; null until <see cref="PrepareAsync"/> fills it.</summary>
        private IReadOnlyList<ModelListing> _available;

        /// <summary>
        /// The advisory most recently spoken, so a broken configuration is reported ONCE rather than on
        /// every idle tick. A companion repeating "gemma3:4b isn't available" every thirty seconds is a
        /// worse bug than the one being reported.
        /// </summary>
        private string _lastAdvisory;

        /// <summary>
        /// Last known backend reachability, or null before the first check. Exists so availability is
        /// logged on the TRANSITION rather than on every probe: the ask path checks before each turn, so
        /// logging the state itself would write a line every idle tick and bury everything else in the
        /// diagnostic file. Same reasoning as <see cref="_lastAdvisory"/>, for the same reason.
        /// </summary>
        private bool? _lastBackendUp;

        /// <summary>
        /// What the companion has actually said recently, newest last, so it stops saying it again.
        ///
        /// THE PROBLEM THIS SOLVES IS THE NORMAL CASE, not an edge case. The system prompt has always
        /// carried "Do not repeat anything you have said recently", and that sentence was INERT: every
        /// ask is an independent single-turn request, so nothing in the model's context said what it had
        /// said before. A user who spends the day in one editor gets one unchanging screen description
        /// and therefore the same remark, over and over, which is the single most likely way this feature
        /// becomes annoying enough to switch off.
        ///
        /// Mirrors <c>SmartFortunes._recent</c>, which solved exactly this for the fortune picker ("so a
        /// stable foreground window rotates"). The mechanism has to differ because this GENERATES rather
        /// than picking from a pool: a pool can be filtered, a model has to be told.
        ///
        /// Instance state, and the brain outlives an ask (<c>AiSessionManager</c> caches it), so the
        /// memory spans a session. It resets when the brain is retired, which a settings change or an
        /// enable/disable does. That is acceptable: a reset costs at most one repeated remark.
        /// </summary>
        private readonly Queue<string> _recentRemarks = new Queue<string>();

        /// <summary>
        /// How many remarks are remembered for DUPLICATE DETECTION. Larger than the prompt recall below,
        /// because checking a candidate against memory is free while putting memory into the prompt is
        /// not.
        /// </summary>
        private const int RecentRemarkMemory = 40;

        /// <summary>
        /// How many recent remarks are HANDED to the prompt builder. This is a pre-cut of the memory, not
        /// what the model reads: <see cref="DescribeAlreadySaid"/> quotes only the last
        /// <see cref="RemarksQuotedInPrompt"/> of whatever it is given, so the effective recall in every
        /// prompt is four, not the eight this comment used to describe as "a few hundred tokens" (F084).
        /// Bounded because this is prefill on every single ask; raising the quoted count to the full memory
        /// would put the transcript above the screen description in size, i.e. spend more on what NOT to
        /// say than on what to react to. Whether the quoted count should rise to meet this one is an
        /// AiBrain decision; the two constants are named separately so the gap is visible rather than a
        /// comment's mistake.
        /// </summary>
        private const int RecentRemarkPromptRecall = 8;

        /// <summary>How many remarks <see cref="DescribeAlreadySaid"/> quotes back to the model, oldest
        /// dropped first. Internal so the self-test can assert the bound with MORE remarks than this.</summary>
        internal const int RemarksQuotedInPrompt = 4;

        /// <summary>
        /// Emit one diagnostic line. Never throws: a broken sink must not be able to take down an AI turn,
        /// which is the whole reason this layer swallows exceptions in the first place.
        /// </summary>
        private static void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            Action<string> sink = LogSink;
            if (sink == null) return;
            try { sink(message); } catch { }
        }

        /// <summary>
        /// Record backend reachability, logging only when it CHANGED. <paramref name="reason"/> is a
        /// category (never a message, which can carry a URL or a key in a query string) and is only used
        /// when going down, because "why is it up" is not a question.
        ///
        /// The first check logs too, since null-to-known is a transition and the launch answer is the one
        /// a user reporting "it never says anything" most needs in the file.
        /// </summary>
        private void NoteBackendAvailability(bool up, string reason)
        {
            if (_lastBackendUp.HasValue && _lastBackendUp.Value == up) return;
            bool first = !_lastBackendUp.HasValue;
            _lastBackendUp = up;
            Log("backend " + (up ? "reachable" : "unreachable") +
                (first ? " (first check)" : " (was " + (!up ? "reachable" : "unreachable") + ")") +
                (up || string.IsNullOrEmpty(reason) ? "" : " reason=" + reason) +
                " endpoint=" + BackendHostDescription);
        }

        /// <summary>
        /// The host(s) the backend was actually built against, for the availability, inventory and failure
        /// lines. Defaults to the snapshot's local Endpoint host, which is right for the local slot only: a
        /// cloud-primary brain's snapshot still carries the local URL (ActiveSlotSnapshot promotes only the
        /// models), so every line said "endpoint=localhost" during a cloud outage (F073).
        /// AiBrainModule.CreateBrain sets it to the cloud host, or "cloudHost->localHost" for the composite.
        /// </summary>
        internal string BackendHostDescription { get; set; }

        /// <summary>
        /// Whether a configured model the backend does not offer is replaced by the first usable listing (BUG-002's
        /// substitution). True for a local backend, where the first listed model costs nothing; false for a cloud
        /// primary, where it would bill a model the user never chose, which F101 already called a defect for the
        /// blank id. Set by AiBrainModule.CreateBrain from the primary slot (R-022); true by default for the
        /// local-only constructions the self-test builds.
        /// </summary>
        internal bool SubstituteMissingModel { get; set; } = true;

        /// <summary>Why the last AskAboutScreenAsync produced nothing, as a category ("not signed in", "http-401"), or
        /// null after one that answered. For the pane's Status card (lane feature/cli-backend); never a message.</summary>
        internal string LastFailure { get; private set; }

        /// <summary>
        /// The failure line's fields, from the path actually TAKEN: the model that was sent (a substitute, or
        /// "(unresolved)" when the failure came before resolution), whether this turn was a vision turn, and
        /// the backend host. The old line read the SETTING (<c>_useVision ? _visionModel : _textModel</c>) and
        /// the local endpoint from the snapshot, so a failed text-path poke under UseVision logged the vision
        /// model with vision=True, and a cloud outage logged "endpoint=localhost" (F073).
        /// </summary>
        internal string DescribeFailure(Exception ex, ModelChoice choice, bool useVisionPath)
        {
            return DescribeError(ex) +
                   " model=" + (choice != null && !string.IsNullOrEmpty(choice.Model) ? choice.Model : "(unresolved)") +
                   " vision=" + useVisionPath +
                   " endpoint=" + BackendHostDescription;
        }

        /// <summary>
        /// Ask the backend whether it is there, recording any transition on the way through.
        ///
        /// Deliberately RETHROWS rather than converting a failure into false: both callers already have
        /// their own handler (PrepareAsync returns false, AskAboutScreenAsync logs and returns null), and
        /// swallowing here would take the exception away from them and change what they report.
        /// </summary>
        private async Task<bool> CheckBackendAvailableAsync(CancellationToken ct)
        {
            try
            {
                bool up = await _backend.IsAvailableAsync(ct).ConfigureAwait(false);
                await ObserveReachabilityAsync(up, "backend reported unavailable", ct).ConfigureAwait(false);
                return up;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                NoteBackendAvailability(false, DescribeError(ex));
                throw;
            }
        }

        /// <summary>
        /// Record reachability and, when the backend has just been seen up, take its inventory. Until 2026-09-29
        /// the inventory was taken in PrepareAsync alone, once per brain: a brain built lazily on its first ask
        /// (auto-start off, no warm-up) never ran PrepareAsync and so never re-validated its configured model, and
        /// a model pulled while the server was restarted stayed "missing" until the next Apply (F071). Listing on
        /// the transition to up, the first check included, covers both; a backend that stays up is not re-listed
        /// per ask, and the pane's Refresh reaches the live brain through AiSessionManager.RefreshInventoryAsync.
        /// </summary>
        private async Task ObserveReachabilityAsync(bool up, string downReason, CancellationToken ct)
        {
            bool wasUp = _lastBackendUp == true;
            NoteBackendAvailability(up, up ? null : downReason);
            // On the transition to up, and while the inventory is still UNKNOWN. The composite is up through its
            // local leg while its cloud primary is away, so the primary's return is never a transition of the whole
            // and the configured cloud id was not re-validated until the next Apply (RA-062). While nothing is
            // known the cost is one listing per check; the composite answers null, without a request, while its
            // primary's own probe last said down, and an empty listing never becomes the inventory (below), so
            // "unknown" stays unknown until a listing answers. A known inventory is still never re-listed per ask.
            if (up && (!wasUp || _available == null)) await RefreshInventoryAsync(ct).ConfigureAwait(false);
        }

        /// <summary>The reachability check, for the self-test: it drives the transition record and the inventory
        /// refresh without a screen.</summary>
        internal Task<bool> CheckBackendAvailableForDiagnosticsAsync(CancellationToken ct)
        {
            return CheckBackendAvailableAsync(ct);
        }

        /// <summary>Said when a brain factory throws on the session's pool thread (F100); category only.</summary>
        internal static void LogBuildFailure(Exception ex)
        {
            Log("brain build failed: " + DescribeError(ex));
        }

        /// <summary>
        /// Classify a swallowed exception into a short, non-identifying category. The message itself can
        /// carry a URL or a file path, so only the type name and a coarse bucket are recorded.
        /// </summary>
        internal static string DescribeError(Exception ex)
        {
            if (ex == null) return "none";
            // A coding-agent CLI's failure, by its class ("cli-not-signed-in", "cli-timed-out"): never its message, which
            // can quote what the CLI said, and that can name an account (feature/cli-backend).
            DesktopAICompanion.CodingAgent.CodingAgentCliException cli = ex as DesktopAICompanion.CodingAgent.CodingAgentCliException;
            if (cli != null) return "cli-" + DesktopAICompanion.CodingAgent.CodingAgentCli.OutcomeWord(cli.Answer.Outcome);
            // TimeoutException is what AiEndpointPolicy's end-to-end deadline throws, and it was missing from
            // the bucket named for it: a deadline that fired logged as "TimeoutException" while a client-side
            // cancel logged as "timeout-or-cancelled", two spellings of one thing (found while adding the F107
            // case below).
            if (ex is TaskCanceledException || ex is OperationCanceledException || ex is TimeoutException)
                return "timeout-or-cancelled";
            // BEFORE the HttpRequestException test it derives from: an answered status is a category of its own.
            // Every 4xx and 5xx used to be filed as "backend-unreachable", so a bad cloud key logged "backend
            // reachable (first check)" and "screen ask failed: backend-unreachable" for the same turn (F107).
            // The status code carries nothing identifying; the provider's message can, and stays out.
            AiBackendHttpException http = ex as AiBackendHttpException;
            if (http != null) return "http-" + http.StatusCode.ToString(CultureInfo.InvariantCulture);
            if (ex is HttpRequestException) return "backend-unreachable";
            if (ex is System.Text.Json.JsonException) return "bad-response-json";
            if (ex is UnauthorizedAccessException) return "access-denied";
            if (ex is InvalidOperationException) return "invalid-state";
            return ex.GetType().Name;
        }

        /// <summary>Endpoint HOST only, never the full URL: a base URL can carry a key in its query.</summary>
        internal static string DescribeEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return "(unset)";
            try { return new Uri(endpoint).Host; }
            catch { return "(unparseable)"; }
        }

        // The last 16x16 luma signature ScreenChanged compared against. ScreenChanged has had no caller since the
        // module's idle loop went in aibrain 1.2.3; the keep is deliberate and recorded (its own summary, and
        // docs/DESIGN-REGISTER.md `#### burn/aibrain`). This used to say "used by the idle loop" (RA-054, RA-066).
        private byte[] _lastFrameSignature;
        private int _disposeStarted;

        // Width a vision image is downscaled to before sending.
        //
        // MEASURED 2026-09-10 against gemma3:4b with an eye chart: seven uncommon words rendered at
        // known font sizes on a 2560x1440 panel, downscaled to each candidate width, read back and
        // scored against a list the model never saw. 3 runs each.
        //
        //   width   11px  14px   18px  24px+   invented words
        //     448     no    no     NO    yes        0
        //     672     no    no    yes    yes        0
        //     896     no  near    yes    yes        0     <-- this value
        //    1120     no  near    yes    yes        0
        //    1344     no  near    2/3    yes        5
        //    1792     no  near    yes    yes        1
        //    2560     no  near    yes    yes        5
        //
        // Three things that were not obvious, and one of which contradicted the comment this replaced:
        //
        //  * LATENCY IS FLAT: 181-283ms across every width, native included. The old comment blamed
        //    "full-screen frames make a vision model crawl (tens of seconds)" -- that is not what
        //    happens. The model resizes internally, so extra pixels are neither slow nor useful. (The
        //    tens-of-seconds figure was really a bigger MODEL, not a bigger image: gemma4:12b took 52s
        //    at this same width.)
        //  * MORE PIXELS MAKE IT WORSE. At 1344 and 2560 the model began INVENTING words that were
        //    never on the chart ("STOP", "DECODER"), five apiece. 896 and 1120 invented none. So the
        //    cap is an accuracy guard, not a bandwidth or speed one.
        //  * 11px SOURCE TEXT IS UNREADABLE AT ANY WIDTH, native included -- a model ceiling, not a
        //    downscale artifact. 14px only ever comes back near-miss ("MARGOLD" for MARIGOLD).
        //
        // 896 is therefore the largest width with zero hallucination, and the smallest that resolves
        // 14px text at all. 672 is equally clean but reads less. Do not raise this to "read the screen
        // better": above 1120 it reads the screen worse.
        private const int VisionMaxWidth = 896;
        /// <summary>OCR reads the larger capture: Tesseract accuracy falls off with glyph height,
        /// and unlike the vision model there is no native input size to match.</summary>
        private const int OcrCaptureWidth = 1280;
        private const int MaximumCaptureWidth = 2048;
        private const int MaximumCaptureHeight = 2048;
        // The pixel budget IS the two clamps. This used to be a third literal (4 * 1024 * 1024) behind a
        // guard in CaptureScreen that no input could reach, because both dimensions are clamped to at most
        // 2048 before it and 2048 * 2048 is exactly that literal (F074). Kept as a name, derived rather
        // than typed, so raising one dimension cap cannot silently leave the other bound in place.
        private const long MaximumCapturePixels = (long)MaximumCaptureWidth * MaximumCaptureHeight;
        private const int MaximumResponseCharacters = 512;
        private const int MaximumEmotionCharacters = 32;

        // U+00AE REGISTERED SIGN, and U+00C2 (capital A with circumflex) -- what that sign's leading UTF-8
        // byte looks like once it has been decoded as ANSI. Written as code points rather than pasted glyphs
        // on purpose: the marker for an encoding bug must not itself depend on how this file gets decoded.
        private const char RegisteredSign = (char)0x00AE;
        private const char AnsiMisdecodeMarker = (char)0x00C2;
        private const int Srccopy = 0x00CC0020;
        private const int Captureblt = 0x40000000;
        private const int Halftone = 4;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool StretchBlt(
            IntPtr destination,
            int destinationX,
            int destinationY,
            int destinationWidth,
            int destinationHeight,
            IntPtr source,
            int sourceX,
            int sourceY,
            int sourceWidth,
            int sourceHeight,
            int rasterOperation);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr deviceContext, int stretchMode);

        /// <summary>
        /// Build the system prompt fresh each call so it reflects the current persona (name, user,
        /// personality — backlog 5.5) and the time of day (5.2). Internal rather than private so a
        /// probe can assert on the real prompt text instead of on a copy of it that can drift.
        /// </summary>
        internal string BuildSystemPrompt()
        {
            return BuildSystemPrompt(null);
        }

        /// <summary>
        /// As <see cref="BuildSystemPrompt()"/>, but for a disposition the user has NOT saved yet.
        ///
        /// Exists for the persona audition ("Show me 5 examples"), whose entire purpose is to hear a
        /// disposition before committing to it. An overload rather than a copy on purpose: everything
        /// else in the prompt -- the name, the no-inventing-names rule, the word budget, the
        /// write-swearing-out-in-full rule, the JSON shape -- must be identical to a real turn, or the
        /// audition is of a different character than the one the user would get.
        /// </summary>
        /// <param name="dispositionIdOverride">
        /// A disposition id to use instead of the saved one; null or unknown falls back to the saved
        /// setting, which is what <see cref="Dispositions.InstructionForId"/> already does for a bad id.
        /// </param>
        internal string BuildSystemPrompt(string dispositionIdOverride)
        {
            string name        = string.IsNullOrWhiteSpace(_settings.CompanionName) ? "a tiny desktop companion" : _settings.CompanionName.Trim();
            string disposition = Dispositions.InstructionForId(
                string.IsNullOrWhiteSpace(dispositionIdOverride) ? _settings.Disposition : dispositionIdOverride);
            string userName = string.IsNullOrWhiteSpace(_settings.UserName) ? "" : _settings.UserName.Trim();
            // Allow the configured name but don't force it into every remark, and forbid reading a name
            // off the screen — window titles and paths ("Administrator", "C:\\Users\\Admin", ...) were
            // being picked up as the user's name.
            string user = userName.Length == 0
                ? " You do not know your human's name, so never invent one or read a name, username or handle off the screen."
                : (" Your human is called " + userName + ". Use their name only when it actually fits the " +
                   "remark, not in every single one; when you do use a name, it must be " + userName +
                   " — never invent one or use any other name, username or handle you see on the screen.");

            return
                "You are " + name + ", a tiny companion living on the user's screen. " +
                "Disposition (apply to the remark text only, keep the JSON exactly as specified): " + disposition +
                " Commit to it fully and stay in character in every word." + user +
                " It is currently " + TimeOfDay() + ". " +
                "Look at what is on the screen (described below) and make one short, in-character remark " +
                "about something specific you actually see there — name a program, file, word or detail from it. " +
                "Be vivid and true to your disposition; never generic, off-topic or merely polite. " +
                "Do not repeat anything you have said recently — make every remark new and different. " +
                "Keep it to one or two sentences, about 20 words each (40 words at most) — for a roast or " +
                "insult-comic disposition, a short setup followed by the knockdown lands well; otherwise one " +
                "sentence is often enough. Do not use quotation marks in the remark. " +
                // Stated once, here, so it reaches every disposition. It used to live only inside the
                // Jules Winnfield instruction text, which meant the Jeff Ross roast and the Drill
                // Sergeant -- both of which ask for real profanity -- never told the model how to write
                // it, and a model that self-censors to "f***" was satisfying its own training rather
                // than the persona the user chose. Measured: nemotron-3-nano did exactly that, mixing a
                // plain curse and an asterisked one in the same remark. Picking a foul-mouthed character
                // and getting asterisks is not the character.
                "If your character swears, write the word out in full — never censor it with asterisks, " +
                "symbols, abbreviations or euphemisms. " +
                "Never say that you are an AI or a language model. " +
                "Reply ONLY with compact JSON of the form " +
                "{\"text\":\"<your remark>\",\"emotion\":\"<one of: happy, sad, thinking, excited, confused, neutral>\"}.";
        }

        /// <summary>
        /// The screen described in words: the front window, the window the companion is standing on, and
        /// what else is open. Extracted from AskAboutScreenAsync so the live persona audition builds its
        /// context with the SAME code a real turn does rather than a copy of it, which is the kind of
        /// duplication Companion Studio's parity self-test exists to stop.
        /// </summary>
        private static string DescribeScreenContext(ScreenContext captureContext, string petZone)
        {
            // Context: the front window (5.1) and the window the pet is standing on (5.6).
            string win = captureContext != null ? captureContext.WindowTitle : "";
            string ctx = "";
            if (!string.IsNullOrWhiteSpace(win))     ctx += "The active window is: " + win + "\n";
            if (!string.IsNullOrWhiteSpace(petZone)) ctx += "You are standing on the window: " + petZone.Trim() + "\n";
            // What else is open, frontmost first (host 1.1.0). One bounded window enumeration, no
            // inference, and often a better basis for a remark than the pixels: "Code in front, Outlook
            // and a browser behind it" is legible where 6px text is not.
            string others = DescribeOtherWindows(captureContext);
            if (others.Length > 0) ctx += others;
            if (ctx.Length > 0) ctx += "\n";
            return ctx;
        }

        /// <summary>
        /// Audition a disposition: generate one remark per canned scene so a persona can be judged by its
        /// VOICE before it is saved.
        ///
        /// Deliberately reuses the live path's prompt builder, retry helper and parser, so what the user
        /// hears is what the companion would actually say. What it does NOT reuse is the screen: see
        /// <see cref="DispositionScenes"/> for why a live capture makes all five samples the same remark.
        ///
        /// Bounded and cancellable, because this is five sequential generations rather than a button
        /// press. Each sample gets its own timeout from a LINKED token, so one stuck generation costs one
        /// sample instead of the whole audition, and the caller's token still cancels the lot. A failed
        /// sample is recorded as a category and the run CONTINUES: four remarks and one timeout is a
        /// useful answer, and stopping on the first failure would make a flaky backend look like a broken
        /// persona.
        ///
        /// Never throws except for caller cancellation.
        /// </summary>
        internal async Task<DispositionAudition> SampleDispositionAsync(
            string dispositionId,
            TimeSpan perSampleTimeout,
            CancellationToken ct)
        {
            return await SampleDispositionAsync(dispositionId, null, null, perSampleTimeout, ct)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// As the canned-scene audition, but optionally against the REAL screen.
        ///
        /// Pass a <paramref name="liveContext"/> and the audition captures once, reads it once (OCR or
        /// vision, exactly as a real turn would), and then asks for several remarks about that one
        /// screen. That is the more honest test of the two: it is literally what the companion would say
        /// about what you are doing, including the quirks a real turn has, such as the capture being
        /// clamped to the COMPANION's monitor rather than the front window's.
        ///
        /// Captured ONCE and reused, not re-captured per sample. Re-capturing would let the screen change
        /// underneath the run, which reintroduces a second variable into the one comparison the feature
        /// exists to make.
        /// </summary>
        /// <param name="liveContext">The live screen, or null for the canned scenes.</param>
        /// <param name="petZone">The window the companion is standing on, as a real turn reports it.</param>
        internal async Task<DispositionAudition> SampleDispositionAsync(
            string dispositionId,
            ScreenContext liveContext,
            string petZone,
            TimeSpan perSampleTimeout,
            CancellationToken ct)
        {
            var samples = new List<DispositionSample>();
            bool live = liveContext != null;
            // The audition is a TEXT turn unless a live screen is being read by a vision model, so it is
            // normally graded against the text model -- auditioning a persona on a 12B vision model would
            // take minutes and measure the wrong thing.
            bool useVisionPath = SendsScreenshot(live);
            // The SAME policy the live ask applies (ResolveBeforeCapture): a cloud primary never substitutes, so an
            // audition of a retired cloud id ends on one "(not run)" sample carrying the advisory instead of five
            // billed requests to whatever the provider lists first. Until 2026-09-30 this call took the overload
            // that defaults allowSubstitution to true, which R-022 had reached only on the ask path (RA-063).
            ModelChoice choice = AiModelPolicy.ChooseModel(
                useVisionPath ? _visionModel : _textModel, _available, useVisionPath,
                SubstituteMissingModel, BackendHostDescription);
            int sampleCount = DispositionScenes.All.Length;
            Log("persona audition: disposition=" + (dispositionId ?? "(saved)") +
                " source=" + (live ? (useVisionPath ? "live-vision" : "live-ocr") : "canned") +
                " samples=" + sampleCount +
                " model=" + (choice.Model ?? "(none)") + " resolve=" + choice.Reason);
            if (!choice.Usable)
            {
                // BUG-002's shape, in the place the backlog predicted it would reappear. Reported once
                // rather than five identical times. Rare on the text path (ChooseModel substitutes
                // whenever the inventory holds anything usable) but reachable when the inventory is
                // known and empty of candidates, and reachable on the VISION path whenever nothing
                // installed can see images.
                samples.Add(new DispositionSample(
                    "(not run)", null, choice.Advisory ?? "no usable model", 0));
                return new DispositionAudition(samples, null, choice.Advisory);
            }
            RememberSent(useVisionPath, choice.Model);

            // Read the screen ONCE, before the loop, so five samples cost one capture and one OCR pass
            // rather than five of each.
            var scenes = new List<PreparedScene>();
            if (live)
            {
                PreparedScene? prepared = null;
                try
                {
                    prepared = await PrepareLiveSceneAsync(liveContext, petZone, useVisionPath, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    samples.Add(new DispositionSample("(your screen)", null,
                        "could not read the screen: " + DescribeError(ex), 0));
                    return new DispositionAudition(samples, choice.Model, choice.Advisory);
                }
                for (int i = 0; i < sampleCount; i++) scenes.Add(prepared.Value);
            }
            else
            {
                foreach (DispositionScenes.Scene scene in DispositionScenes.All)
                    scenes.Add(new PreparedScene(scene.Label, scene.Context, null));
            }

            string system = BuildSystemPrompt(dispositionId);
            // What it has already said, so the prompt's "Do not repeat anything you have said recently"
            // has something to refer to. Without this it is an INERT instruction: each sample is an
            // independent single-turn request, so the model cannot know what the other four said, and
            // five asks about one unchanging screen come back nearly identical. This is what makes a
            // live-screen audition a real test rather than the same sentence five times.
            var alreadySaid = new List<string>();

            foreach (PreparedScene scene in scenes)
            {
                ct.ThrowIfCancellationRequested();
                Stopwatch clock = Stopwatch.StartNew();
                var messages = new List<ChatMessage>
                {
                    ChatMessage.System(system),
                    // A live-VISION scene already ends in the real turn's own instruction ("Look at my screen
                    // and react.", PrepareLiveSceneAsync), so the canned/OCR instruction is not stacked on it:
                    // until 2026-09-30 a live-vision audition prompt carried both (F064).
                    ChatMessage.User(
                        scene.Context
                            + (scene.ImageBase64 == null ? "\n" + DispositionScenes.Instruction : "")
                            + DescribeAlreadySaid(alreadySaid),
                        scene.ImageBase64 == null ? null : new[] { scene.ImageBase64 }),
                };
                using (var perSample = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    perSample.CancelAfter(perSampleTimeout);
                    try
                    {
                        string raw = await ChatWithRetryForDiagnosticsAsync(
                            _backend, choice.Model, messages, perSample.Token).ConfigureAwait(false);
                        BrainResponse resp = Parse(raw);
                        if (resp == null)
                        {
                            samples.Add(new DispositionSample(scene.Label, null,
                                string.IsNullOrWhiteSpace(raw) ? "empty reply" : "unusable reply shape",
                                clock.ElapsedMilliseconds));
                        }
                        else
                        {
                            samples.Add(new DispositionSample(
                                scene.Label, resp.Text, null, clock.ElapsedMilliseconds));
                            alreadySaid.Add(resp.Text);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // The caller cancelling ends the audition; only THIS sample's own timeout is
                        // recoverable. Distinguished by asking the caller's token, not this one, because
                        // the linked source reports cancelled either way.
                        if (ct.IsCancellationRequested) throw;
                        samples.Add(new DispositionSample(
                            scene.Label, null, "timed out", clock.ElapsedMilliseconds));
                    }
                    catch (Exception ex)
                    {
                        // A CLI failure in the pane's own plain words ("Codex is not signed in..."), not its log category;
                        // every other failure keeps the category the sample has always shown (feature/cli-backend). A
                        // refusal of the module's own default says so, as Validate's does (review finding F14).
                        DesktopAICompanion.CodingAgent.CodingAgentCliException cli = ex as DesktopAICompanion.CodingAgent.CodingAgentCliException;
                        samples.Add(new DispositionSample(
                            scene.Label, null,
                            cli != null
                                ? DesktopAICompanion.CodingAgent.CodingAgentCliText.Describe(cli.Agent, cli.Answer, null,
                                    AiSettings.DefaultCliModelFor(cli.Agent)).TrimStart('✗', '⚠', ' ')
                                : DescribeError(ex),
                            clock.ElapsedMilliseconds));
                    }
                }
            }
            return new DispositionAudition(samples, choice.Model, choice.Advisory);
        }

        /// <summary>The most recent remarks, oldest first, capped at what is worth putting in a prompt.</summary>
        private List<string> RecentRemarksForPrompt()
        {
            var all = new List<string>(_recentRemarks);
            int from = Math.Max(0, all.Count - RecentRemarkPromptRecall);
            return all.GetRange(from, all.Count - from);
        }

        /// <summary>Record a spoken remark, evicting the oldest past <see cref="RecentRemarkMemory"/>.</summary>
        private void RememberRemark(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _recentRemarks.Enqueue(text);
            while (_recentRemarks.Count > RecentRemarkMemory) _recentRemarks.Dequeue();
        }

        /// <summary>
        /// Is this effectively something the companion already said?
        ///
        /// Deliberately not byte equality. A model asked twice about the same screen rarely repeats
        /// itself exactly; it rephrases, which reads as a repeat to a human and passes a string compare.
        /// So: normalised equality, or a word-set overlap at or above <see cref="RepeatOverlap"/>.
        ///
        /// The threshold is high on purpose. Suppressing too eagerly is worse than the bug, because the
        /// remedy is a second inference and the failure mode is a companion that goes quiet about a
        /// screen it legitimately has more to say about. Short remarks are exempted below the word floor
        /// for the same reason: two four-word quips can share three words and mean different things.
        /// </summary>
        internal static bool IsRepeatOf(string candidate, IEnumerable<string> previous)
        {
            if (string.IsNullOrWhiteSpace(candidate) || previous == null) return false;
            string normalized = NormalizeForRepeat(candidate);
            if (normalized.Length == 0) return false;
            string[] candidateWords = normalized.Split(' ');
            foreach (string earlier in previous)
            {
                if (string.IsNullOrWhiteSpace(earlier)) continue;
                string earlierNormalized = NormalizeForRepeat(earlier);
                if (earlierNormalized.Length == 0) continue;
                if (normalized == earlierNormalized) return true;
                if (candidateWords.Length < RepeatWordFloor) continue;
                if (WordOverlap(candidateWords, earlierNormalized.Split(' ')) >= RepeatOverlap) return true;
            }
            return false;
        }

        /// <summary>Fraction of the shorter remark's distinct words that the other also has.</summary>
        private static double WordOverlap(string[] left, string[] right)
        {
            var a = new HashSet<string>(left);
            var b = new HashSet<string>(right);
            if (a.Count == 0 || b.Count == 0) return 0;
            int shared = 0;
            foreach (string word in a) if (b.Contains(word)) shared++;
            return (double)shared / Math.Min(a.Count, b.Count);
        }

        /// <summary>Lowercase, punctuation stripped, whitespace collapsed, so rephrasing is comparable.</summary>
        private static string NormalizeForRepeat(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            bool pendingSpace = false;
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (pendingSpace && sb.Length > 0) sb.Append(' ');
                    pendingSpace = false;
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    pendingSpace = true;
                }
            }
            return sb.ToString();
        }

        /// <summary>A copy of <paramref name="list"/> with one more entry, for a retry's avoid-list.</summary>
        private static List<string> AppendedTo(IEnumerable<string> list, string extra)
        {
            var copy = new List<string>(list);
            if (!string.IsNullOrWhiteSpace(extra)) copy.Add(extra);
            return copy;
        }

        /// <summary>At or above this word-set overlap, two remarks count as the same remark.</summary>
        private const double RepeatOverlap = 0.8;

        /// <summary>Below this many words, overlap is too noisy to judge and only exact repeats count.</summary>
        private const int RepeatWordFloor = 6;

        /// <summary>One scene ready to send: a description, and a base64 PNG when the vision path is on.</summary>
        private struct PreparedScene
        {
            public readonly string Label;
            public readonly string Context;
            public readonly string ImageBase64;
            public PreparedScene(string label, string context, string imageBase64)
            {
                Label = label;
                Context = context;
                ImageBase64 = imageBase64;
            }
        }

        /// <summary>
        /// Capture and read the real screen once, for a live audition. Uses the SAME bounds choice, the
        /// same capture, the same OCR and the same context wording a real turn uses, so what the audition
        /// grades is the live behaviour rather than an approximation of it.
        /// </summary>
        private async Task<PreparedScene> PrepareLiveSceneAsync(
            ScreenContext liveContext, string petZone, bool useVisionPath, CancellationToken ct)
        {
            PixelRect mb = liveContext.MonitorBounds;
            Rectangle monitor = new Rectangle(mb.X, mb.Y, mb.Width, mb.Height);
            Rectangle bounds = ChooseCaptureBounds(liveContext.ForegroundWindowBounds, monitor);
            string ctx = DescribeScreenContext(liveContext, petZone);

            using (Bitmap shot = CaptureScreen(bounds, useVisionPath ? VisionMaxWidth : OcrCaptureWidth))
            {
                Log("audition capture: rect=" + bounds.Width + "x" + bounds.Height +
                    " subject=" + (bounds == monitor ? "monitor" : "window") +
                    " uniform=" + UniformityPercent(shot) + "%");
                if (useVisionPath)
                {
                    string b64 = ToBase64Png(shot);   // already captured at VisionMaxWidth (F075)
                    return new PreparedScene(
                        "your screen", ctx + "Look at my screen and react.", b64);
                }
                string ocr = await RunOcrAsync(shot, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(ocr)) ocr = "(the screen has no readable text)";
                ocr = UnicodeTextProgress.TruncateAtCodePointBoundary(ocr, 1500);
                return new PreparedScene(
                    "your screen",
                    ctx + "Here is the text currently visible on my screen:\n\n" + ocr,
                    null);
            }
        }

        /// <summary>Test seam for <see cref="DescribeAlreadySaid"/>: the audition drives exactly five
        /// scenes, one more than the quoted count, so the ageing rule has to be asserted on the builder
        /// itself with more remarks than it quotes (F084).</summary>
        internal static string DescribeAlreadySaidForDiagnostics(IList<string> alreadySaid)
        {
            return DescribeAlreadySaid(alreadySaid);
        }

        /// <summary>
        /// The "you already said this" clause. Empty for the first sample, so a single ask is unchanged.
        ///
        /// Bounded to the last few remarks and to a per-remark length: the point is to rule out repeats,
        /// and a growing verbatim transcript would eventually cost more prefill than the remark it is
        /// trying to vary. Trimmed rather than summarised, because a summary needs another inference.
        /// (This summary sat above the seam's, leaving the builder itself undocumented, RA-064.)
        /// </summary>
        private static string DescribeAlreadySaid(IList<string> alreadySaid)
        {
            if (alreadySaid == null || alreadySaid.Count == 0) return "";
            const int PerRemark = 160;
            var sb = new StringBuilder();
            sb.Append("\n\nYou have ALREADY said the following about this. Say something clearly " +
                      "different this time -- a different detail, a different angle:");
            int from = Math.Max(0, alreadySaid.Count - RemarksQuotedInPrompt);
            for (int i = from; i < alreadySaid.Count; i++)
            {
                string said = (alreadySaid[i] ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                // At a code-point boundary: a remark is model text and can end in an emoji, and a plain Substring
                // at a UTF-16 index split the pair and quoted a lone surrogate back to the model (RA-057). The same
                // helper the OCR text goes through.
                if (said.Length > PerRemark) said = UnicodeTextProgress.TruncateAtCodePointBoundary(said, PerRemark) + "…";
                sb.Append("\n- ").Append(said);
            }
            return sb.ToString();
        }

        /// <summary>Coarse time-of-day label for the persona (backlog 5.2).</summary>
        private static string TimeOfDay()
        {
            int h = DateTime.Now.Hour;
            if (h < 5)  return "late at night";
            if (h < 12) return "the morning";
            if (h < 17) return "the afternoon";
            if (h < 21) return "the evening";
            return "night";
        }

        public AiBrain(ICompanionBrainBackend backend, AiSettings settings)
        {
            _backend = backend;
            _settings = settings ?? new AiSettings();
            // The literal fallback belongs to the LOCAL slot only. A cloud snapshot (Provider set) promotes the
            // cloud ids into TextModel/VisionModel, and an unset cloud model is empty by design
            // (AiSettings.NormalizeOptionalModel: "a cloud slot has no meaningful Ollama default"); filling the
            // blank with an Ollama tag sent "gemma3:4b" to OpenRouter on every remark (F101). Empty stays empty
            // here, and ChooseModel answers "none-configured" with an advisory the user can act on, before any
            // capture.
            bool cloudSlot = !string.IsNullOrEmpty(_settings.Provider);
            string normalizedModel;
            _textModel = AiModelPolicy.TryNormalize(
                _settings.TextModel, out normalizedModel)
                ? normalizedModel
                : (cloudSlot ? "" : "gemma3:4b");
            _visionModel = AiModelPolicy.TryNormalize(
                _settings.VisionModel, out normalizedModel)
                ? normalizedModel
                : (cloudSlot ? "" : "gemma3:4b");
            _useVision = _settings.UseVision;
            _tesseractPath = _settings.TesseractPath;
            BackendHostDescription = DescribeEndpoint(_settings.Endpoint);
        }

        /// <summary>
        /// Launch-time preparation (fire-and-forget): optionally start the backend server, then
        /// preload the active model so the first ask doesn't pay the cold-start cost. Never throws except for
        /// the caller's own cancellation, which is rethrown (AiSessionManager catches it); every other failure
        /// is recorded through NoteBackendAvailability and answered with false (F064).
        /// Returns true when the backend is reachable (used to drive the "AI ready" hint).
        /// </summary>
        /// <param name="warmUp">False for a throwaway brain that will run its own requests straight away (the
        /// persona audition): its samples run on the text model, so warming the model a live vision ask would
        /// use under "keep" residency loaded a second model for nothing (F063). The launch routine keeps true.</param>
        public async Task<bool> PrepareAsync(CancellationToken ct = default(CancellationToken), bool warmUp = true)
        {
            try
            {
                bool up;
                if (_settings.AutoStartServer)
                {
                    up = await _backend.EnsureServerAsync(ct).ConfigureAwait(false);
                    // EnsureServerAsync may START the server, so its answer is a reachability answer too
                    // and belongs in the same transition record. Distinguished in the reason, because
                    // "we tried to launch it and it still is not there" is a different user problem from
                    // "it was never running and we were told not to start it".
                    await ObserveReachabilityAsync(up, "auto-start ran and the backend is still absent", ct).ConfigureAwait(false);
                }
                else
                {
                    up = await CheckBackendAvailableAsync(ct).ConfigureAwait(false);
                }

                // The inventory (what the backend HAS, so the configured model is re-validated and BUG-002's failure
                // mode, a saved id the backend no longer offers, is visible) is taken by ObserveReachabilityAsync on
                // the way through: on this first sight of the backend, and again whenever it comes back (F071).

                // The PATH goes with the id: with one cloud id in both slots the composite cannot tell from the id
                // which local model a fallover will run, and warmed the local vision model for a text session
                // (RA-087). Single-model backends ignore the role.
                if (up && warmUp && _settings.WarmUpDesired)
                    await _backend.WarmUpAsync(_useVision ? _visionModel : _textModel, _useVision, ct).ConfigureAwait(false);

                return up;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Was a bare `catch { return false; }`. Launch preparation failing is the single most
                // consequential silent failure in this module: it decides whether "AI ready" is ever
                // true, and a user whose brain never speaks has no other record of why.
                NoteBackendAvailability(false, DescribeError(ex));
                return false;
            }
        }

        /// <summary>
        /// Ask for one remark, having told the model what it already said, and refuse to accept the same
        /// remark twice.
        ///
        /// This is the whole anti-repetition mechanism for the LIVE product, and it matters most in the
        /// most ordinary case there is: a day spent in one editor is one unchanging screen description,
        /// so without this the companion says the same thing every time it speaks. The system prompt has
        /// always asked it not to; that request was inert, because each ask is an independent single-turn
        /// request with no record of the others.
        ///
        /// Two mechanisms, because one is not enough. The prompt is TOLD the recent remarks (an
        /// instruction a small model may ignore), and the answer is then CHECKED against memory (which it
        /// cannot ignore). Exactly one retry on a repeat: a second inference to avoid boring the user is
        /// worth paying for, a loop is not, and a companion that is slightly repetitive beats one that
        /// goes quiet.
        ///
        /// Internal, and split out of AskAboutScreenAsync deliberately: in there it could only be tested
        /// with a real desktop, a real capture and a real OCR engine, which is not something the gate can
        /// run. Here the probe drives it with a fake backend and no screen at all.
        /// </summary>
        internal async Task<BrainResponse> GenerateWithRepeatGuardAsync(
            string model, string userText, string[] images, CancellationToken ct)
        {
            List<string> recent = RecentRemarksForPrompt();
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(BuildSystemPrompt()),
                ChatMessage.User(userText + DescribeAlreadySaid(recent), images),
            };

            string raw = await ChatWithRetryAsync(model, messages, ct).ConfigureAwait(false);
            BrainResponse resp = Parse(raw);
            // The last place a turn can die quietly. A model that answers promptly in the wrong shape
            // produces exactly the same silence as an unreachable backend, and the Readme already records
            // that a captioner makes the companion "permanently, silently mute". Lengths and an outcome
            // word: never the reply, per LogSink's contract.
            Log("reply parse: " +
                (string.IsNullOrWhiteSpace(raw) ? "empty-reply"
                    : resp == null ? "unusable-shape"
                    : "ok") +
                " model=" + model +
                " rawChars=" + (raw == null ? 0 : raw.Length) +
                (resp == null ? "" : " emotion=" + resp.Emotion));

            if (resp != null && recent.Count > 0 && IsRepeatOf(resp.Text, _recentRemarks))
            {
                Log("repeat guard: the reply repeated a recent remark, asking once more");
                var retryMessages = new List<ChatMessage>
                {
                    ChatMessage.System(BuildSystemPrompt()),
                    ChatMessage.User(
                        userText + DescribeAlreadySaid(AppendedTo(recent, resp.Text)),
                        images),
                };
                string retryRaw = await ChatWithRetryAsync(model, retryMessages, ct).ConfigureAwait(false);
                BrainResponse retryResp = Parse(retryRaw);
                // Keep the retry only if it is actually fresh; otherwise the first answer stands, because
                // two similar remarks are better than none.
                if (retryResp != null && !IsRepeatOf(retryResp.Text, _recentRemarks))
                {
                    Log("repeat guard: the second reply was fresh");
                    resp = retryResp;
                }
                else
                {
                    Log("repeat guard: the second reply repeated too, speaking the first anyway");
                }
            }
            if (resp != null) RememberRemark(resp.Text);
            return resp;
        }

        /// <summary>
        /// Refresh the cached backend inventory. Never throws and never blocks an ask: a listing failure
        /// leaves the previous snapshot (or null) in place, and null simply means "unknown". Called on the
        /// transition to reachable (<see cref="ObserveReachabilityAsync"/>) and by the session for the pane's
        /// Refresh (F071); internal for the latter.
        /// </summary>
        internal async Task RefreshInventoryAsync(CancellationToken ct)
        {
            Func<CancellationToken, Task<IReadOnlyList<ModelListing>>> lister = ModelLister;
            if (lister == null) return;
            try
            {
                IReadOnlyList<ModelListing> listed = await lister(ct).ConfigureAwait(false);
                if (listed == null)
                {
                    // Said, because this used to be the silent reason BUG-002's advisory never fired for a
                    // cloud user with the default fallback (F103): the composite could not enumerate.
                    Log("model inventory: the backend cannot enumerate models, so the configured id is not re-validated");
                    return;
                }
                if (listed.Count == 0)
                {
                    // A listing whose bound tripped comes back empty, as does a refused key and a backend with nothing
                    // pulled. An empty list already reads as "unknown" downstream, but it used to REPLACE a good
                    // inventory, so a re-list on a transition that timed out threw away what the previous one knew
                    // (R-014); and stored as the FIRST inventory it made "unknown" look known, which ended the
                    // re-listing ObserveReachabilityAsync does while nothing is known (RA-062). Never stored.
                    Log(_available == null
                        ? "model inventory: an empty listing leaves the inventory unknown"
                        : "model inventory: an empty listing keeps the previous " + _available.Count + " model(s)");
                    return;
                }
                _available = listed;
                Log("model inventory: " + listed.Count + " model(s) reported by " + BackendHostDescription);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log("model inventory unavailable: " + DescribeError(ex));
            }
        }

        /// <summary>
        /// Turn a <see cref="ModelChoice"/> advisory into something to say, at most once per distinct
        /// message. Returns null when the same advisory has already been delivered.
        /// </summary>
        private BrainResponse AdvisoryOnce(string advisory)
        {
            if (string.IsNullOrWhiteSpace(advisory)) return null;
            if (string.Equals(_lastAdvisory, advisory, StringComparison.Ordinal)) return null;
            _lastAdvisory = advisory;
            return new BrainResponse(advisory, "confused");
        }

        /// <summary>
        /// The model most recently SENT on each path when a turn settled on one: the configured id, or BUG-002's
        /// substitute. <see cref="UnloadAsync"/> (the fullscreen stand-down, retire, shutdown) and the launch
        /// warm-up used to name only the CONFIGURED ids, so under "keep" a substituted local model carried
        /// keep_alive -1, was never evicted, and outlived the process (RA-065). Written where a turn's model is
        /// settled and about to be sent (<see cref="ResolveBeforeCapture"/>, <see cref="SampleDispositionAsync"/>),
        /// read by the release. Two fields because the text and the vision path can each be substituted in one
        /// session; FallbackBackend keeps the same record for its local leg (F104).
        /// </summary>
        private volatile string _sentTextModel;
        private volatile string _sentVisionModel;

        private void RememberSent(bool visionPath, string model)
        {
            if (string.IsNullOrEmpty(model)) return;
            if (visionPath) _sentVisionModel = model;
            else _sentTextModel = model;
        }

        /// <summary>
        /// Ask the backend to unload every model this brain may have loaded: the configured text and vision
        /// ids, and the substitute each path actually sent when that differs (RA-065). Ollama evicts its
        /// keep-alive models; generic OpenAI-compatible providers intentionally do nothing. Best-effort.
        /// De-duplicated the way Ollama compares ids, so one id in both slots is released once.
        /// </summary>
        public async Task UnloadAsync(CancellationToken ct = default(CancellationToken))
        {
            var released = new List<string>();
            foreach (string model in new[] { _textModel, _visionModel, _sentTextModel, _sentVisionModel })
            {
                if (string.IsNullOrEmpty(model)) continue;
                if (released.Exists(delegate(string done) { return string.Equals(done, model, StringComparison.OrdinalIgnoreCase); }))
                    continue;
                released.Add(model);
                try
                {
                    await _backend.UnloadAsync(model, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        /// <summary>
        /// React to whatever is on screen. Returns null when the backend is unavailable or errors,
        /// so the caller can simply stay silent without special-casing exceptions.
        /// </summary>
        public async Task<BrainResponse> AskAboutScreenAsync(
            ScreenContext captureContext,
            string petZone = null,
            bool allowVision = true,
            CancellationToken ct = default(CancellationToken))
        {
            // Hoisted out of the try so the failure line can say which path was taken and which model was sent
            // (F073); a null choice means the failure came before the model was resolved.
            bool useVisionPath = SendsScreenshot(allowVision);
            ModelChoice choice = null;
            LastFailure = null;
            try
            {
                if (!await CheckBackendAvailableAsync(ct).ConfigureAwait(false))
                {
                    LastFailure = "its engine is not reachable";
                    return null;
                }

                Rectangle captureBounds;
                // The monitor that was on offer, kept in scope so the diagnostic below can say whether
                // the window or the whole monitor ended up being the subject.
                Rectangle offeredMonitor = Rectangle.Empty;
                if (captureContext != null)
                {
                    PixelRect mb = captureContext.MonitorBounds;
                    Rectangle monitor = new Rectangle(mb.X, mb.Y, mb.Width, mb.Height);
                    offeredMonitor = monitor;
                    // Prefer the foreground WINDOW over the whole monitor (host 1.1.0). A monitor
                    // shot is downscaled twice before a vision model sees it (monitor -> 1280 ->
                    // 896), so on a 2560-wide display body text arrives about 6px tall and the
                    // model is guessing from colours. A window is a smaller source, so more of it
                    // survives the same budget -- and a monitor shot is mostly NOT what the user is
                    // looking at (wallpaper, other apps, the taskbar), which is exactly the input
                    // that produces a remark about the wrong thing.
                    captureBounds = ChooseCaptureBounds(captureContext.ForegroundWindowBounds, monitor);
                }
                else
                {
                    System.Windows.Forms.Screen primary =
                        System.Windows.Forms.Screen.PrimaryScreen;
                    if (primary == null)
                        throw new InvalidOperationException(
                            "No display is available for screen capture.");
                    captureBounds = primary.Bounds;
                    offeredMonitor = primary.Bounds;
                }
                // Capture straight to the width the chosen path wants, because only one path runs and
                // the old fixed 1280 made the vision path resample TWICE: source -> 1280 -> 896. Two
                // interpolation passes over text are visibly worse than one, and the intermediate was
                // never used by the branch that paid for it. Measured on a 2560-wide capture: the
                // downscaled PNG is not even smaller (395 KB native vs 386 KB at 896, and 639 KB at
                // 1280) because resampling replaces long runs of identical pixels with anti-aliased
                // gradients that will not compress. So the cap exists for the MODEL's benefit, not for
                // bandwidth: 896 is the native input size of a Gemma-family SigLIP encoder, and pixels
                // beyond it are either thrown away by the model or turned into extra image tiles that
                // multiply prefill cost for detail a one-line remark does not need.

                // BUG-002: settle WHICH model before paying for a capture, and say anything that needs saying
                // about it BEFORE the capture too (F072). ResolveBeforeCapture holds the reasoning.
                BrainResponse advisory;
                if (!ResolveBeforeCapture(useVisionPath, out choice, out advisory))
                    return advisory;

                using (Bitmap shot = CaptureScreen(captureBounds, useVisionPath ? VisionMaxWidth : OcrCaptureWidth))
                {
                    // BUG-003: the chosen rect, which of the two candidates won it, and how uniform the
                    // result was. Geometry and one statistic -- never the pixels, never the OCR text.
                    Log("capture: rect=" + captureBounds.Width + "x" + captureBounds.Height +
                        "@" + captureBounds.X + "," + captureBounds.Y +
                        " subject=" + (captureBounds == offeredMonitor ? "monitor" : "window") +
                        " uniform=" + UniformityPercent(shot) + "%");
                    List<ChatMessage> messages = new List<ChatMessage> { ChatMessage.System(BuildSystemPrompt()) };

                    string model;

                    string ctx = DescribeScreenContext(captureContext, petZone);

                    // Routing: with UseVision on, every remark about the screen takes the vision path when the
                    // caller allows it, and the hotkey, the tray row and the unprompted drop all do (owner
                    // decision 2026-09-29, BUG-010). The backlog 6.2 rule this comment used to cite kept the
                    // module's OWN idle loop on the text path when a full-screen glance took about a minute;
                    // the 896 px downscale removed that cost and aibrain 1.2.3 removed the loop. The poke
                    // passes allowVision=false and stays on the fast text path, being a reaction to a click.
                    // OCR is the fallback when vision is off or, on a CLI, when the model chosen takes no images
                    // (SendsScreenshot). A local or cloud slot's blind model is ChooseModel's: substituted on the local
                    // slot, refused with an advisory on a cloud primary (N-cli-model-effort-01).
                    string userText;
                    string[] images = null;
                    if (useVisionPath)
                    {
                        string b64 = ToBase64Png(shot);   // already captured at VisionMaxWidth (F075)
                        // What the vision model is actually being sent. The Readme's accuracy table is
                        // keyed on capture WIDTH, so the only way to tell whether a disappointing remark
                        // came from a degraded input is to know the width and payload that produced it.
                        // A size, not the image.
                        Log("vision payload: cap=" + VisionMaxWidth + "px shot=" + shot.Width + "x" + shot.Height +
                            " pngKB=" + (b64.Length * 3L / 4 / 1024));
                        userText = ctx + "Look at my screen and react.";
                        images = new[] { b64 };
                        model = choice.Model;
                    }
                    else
                    {
                        string ocr = await RunOcrAsync(shot, ct).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(ocr)) ocr = "(the screen has no readable text)";
                        ocr = UnicodeTextProgress.TruncateAtCodePointBoundary(
                            ocr,
                            1500);
                        userText = ctx + "Here is the text currently visible on my screen:\n\n" + ocr;
                        model = choice.Model;
                    }

                    // Generation, the already-said list and the repeat check all live in there, so the
                    // reply-parse diagnostic does too rather than being written twice. Nothing generated here
                    // is discarded any more: a substitution's advisory was spoken before the capture (F072).
                    return await GenerateWithRepeatGuardAsync(
                        model, userText, images, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Still returns null -- the pet staying silent on a broken backend is correct -- but no
                // longer SILENTLY. Before this line a missing model, an unreachable server and "nothing
                // interesting to say" were the same observable event, which is what made BUG-002 take a
                // maintainer bisect to explain. Category and model id only; see LogSink's contract. The
                // fields come from the path actually taken (F073): see DescribeFailure.
                Log("screen ask failed: " + DescribeFailure(ex, choice, useVisionPath));
                DesktopAICompanion.CodingAgent.CodingAgentCliException cliFailure = ex as DesktopAICompanion.CodingAgent.CodingAgentCliException;
                LastFailure = cliFailure != null
                    ? DesktopAICompanion.CodingAgent.CodingAgentCliText.Brief(cliFailure.Answer.Outcome)
                    : DescribeError(ex);
                return null;   // never crash the app over the AI layer
            }
        }

        /// <summary>
        /// Everything a turn decides BEFORE it pays for a capture, and whether it should go on to one.
        ///
        /// BUG-002: settle WHICH model first. A saved id is not evidence the backend still has it ("Refresh
        /// local models" can drop one, and the shipped default is an id many machines never had), and the ask
        /// path returns null on any failure, so an absent model used to be indistinguishable from a quiet
        /// companion. Resolving here also avoids capturing the screen for a request that cannot be sent.
        ///
        /// F072: a SUBSTITUTION is announced here as well, once, instead of after the generation. The old order
        /// wanted the substitute proven before it was named, and paid for that with a capture, an OCR pass and
        /// a full generation (a cold load under the default residency) whose remark was then discarded for the
        /// advisory and remembered as spoken, so the next prompt quoted, under "you have ALREADY said", a line
        /// nobody heard. If the substitute fails after being announced, the log says so; the user hears one
        /// advisory either way.
        ///
        /// Split out so the probe can drive it with a fake inventory and no screen at all.
        /// </summary>
        /// <returns>True to go on to the capture; false to return <paramref name="advisory"/> (null when the
        /// advisory for this configuration has already been spoken) and end the turn.</returns>
        internal bool ResolveBeforeCapture(bool useVisionPath, out ModelChoice choice, out BrainResponse advisory)
        {
            advisory = null;
            choice = AiModelPolicy.ChooseModel(
                useVisionPath ? _visionModel : _textModel,
                _available,
                useVisionPath,
                SubstituteMissingModel,
                BackendHostDescription);
            Log("model resolve: " + choice.Reason +
                " configured=" + (useVisionPath ? _visionModel : _textModel) +
                " using=" + (choice.Model ?? "(none)") +
                " vision=" + useVisionPath);
            if (!choice.Usable)
            {
                advisory = AdvisoryOnce(choice.Advisory);
                return false;
            }
            if (choice.Advisory != null)
            {
                advisory = AdvisoryOnce(choice.Advisory);
                if (advisory != null) return false;
            }
            // Settled and about to be sent: the release has to know this id too when it is a substitute (RA-065).
            RememberSent(useVisionPath, choice.Model);
            return true;
        }

        private async Task<string> ChatWithRetryAsync(string model, IList<ChatMessage> messages, CancellationToken ct)
        {
            return await ChatWithRetryForDiagnosticsAsync(
                _backend,
                model,
                messages,
                ct).ConfigureAwait(false);
        }

        internal static async Task<string> ChatWithRetryForDiagnosticsAsync(
            ICompanionBrainBackend backend,
            string model,
            IList<ChatMessage> messages,
            CancellationToken ct)
        {
            if (backend == null) throw new ArgumentNullException("backend");

            // Latency and attempt count, because "slow" and "failed twice" are the two things a user
            // reporting "the AI does nothing" cannot tell apart from the outside, and neither was
            // recorded anywhere. A 52-second vision generation and a dead backend look identical to
            // someone watching a companion say nothing.
            Stopwatch clock = Stopwatch.StartNew();
            string firstError = null;
            try
            {
                string reply = await backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);
                Log("request ok: model=" + model + " attempts=1 ms=" + clock.ElapsedMilliseconds +
                    " replyChars=" + (reply == null ? 0 : reply.Length));
                return reply;
            }
            // Retry once on a transient transport/timeout/HTTP failure; a deterministic failure (non-transient
            // 4xx/redirect) is not caught here and propagates. Predicate shared with FallbackBackend.
            catch (Exception ex) when (AiEndpointPolicy.IsRetryable(ex, ct))
            {
                firstError = DescribeError(ex);
            }
            // A DETERMINISTIC failure (non-transient 4xx, a redirect) skips the filter above and
            // propagates without ever reaching the retry, so it would otherwise leave no request-outcome
            // line at all. OperationCanceledException is excluded first and deliberately: IsRetryable
            // returns false once ct is cancelled, so an ordinary cancel would land here and be recorded
            // as a failure it is not.
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log("request failed: model=" + model + " attempts=1 ms=" + clock.ElapsedMilliseconds +
                    " error=" + DescribeError(ex) + " retried=no-not-transient");
                throw;
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                string reply = await backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);
                Log("request ok after retry: model=" + model + " attempts=2 ms=" + clock.ElapsedMilliseconds +
                    " firstError=" + firstError + " replyChars=" + (reply == null ? 0 : reply.Length));
                return reply;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // The case the backlog called out as invisible: retried, and gave up. It still
                // propagates (the caller's handler decides what the companion does), but the fact that
                // TWO attempts were spent is recorded here, where the retry actually happened.
                Log("request failed after retry: model=" + model + " attempts=2 ms=" + clock.ElapsedMilliseconds +
                    " firstError=" + firstError + " secondError=" + DescribeError(ex));
                throw;
            }
        }

        /// <summary>
        /// Change-detection gate: true when the screen differs from the last checked frame by more than
        /// <paramref name="thresholdPercent"/> of average luma. First call always returns true. Cheap:
        /// compares a 16x16 grayscale signature.
        /// <para>
        /// Currently has no caller. It backed the module's own idle timer, which was removed in aibrain
        /// 1.2.3 when unprompted commentary moved onto the host's global drop schedule. Kept deliberately
        /// rather than deleted: it is exactly the primitive a "only speak when something changed" option
        /// would need, and it is self-contained.
        /// </para>
        /// </summary>
        public bool ScreenChanged(
            Rectangle captureBounds,
            int thresholdPercent = 4)
        {
            byte[] sig = ComputeSignature(captureBounds);
            if (_lastFrameSignature == null || _lastFrameSignature.Length != sig.Length)
            {
                _lastFrameSignature = sig;
                return true;
            }
            long delta = 0;
            for (int i = 0; i < sig.Length; i++) delta += Math.Abs(sig[i] - _lastFrameSignature[i]);
            _lastFrameSignature = sig;
            double avgDelta = delta / (double)sig.Length;             // 0..255
            return (avgDelta / 255.0 * 100.0) >= thresholdPercent;
        }

        // ---- screen capture ------------------------------------------------

        /// <summary>Smallest window worth capturing on its own. Below this it is a dialog or a
        /// tooltip, and the monitor is the more informative subject.</summary>
        private const int MinimumWindowCaptureWidth = 320;
        private const int MinimumWindowCaptureHeight = 240;

        /// <summary>How many background applications are named to the model. Enough to set a scene, few
        /// enough that a long tab list cannot dominate the prompt.</summary>
        private const int MaximumOtherWindowsDescribed = 4;

        /// <summary>
        /// The foreground window when it is a sensible subject, otherwise the monitor. Pure, so the
        /// awkward cases are testable: a zero rect (no foreground window), a rect too small to be worth
        /// framing, and a window extending past its monitor -- clamped, so a capture cannot wander onto
        /// a neighbouring display or off the desktop.
        /// </summary>
        internal static Rectangle ChooseCaptureBounds(PixelRect foreground, Rectangle monitor)
        {
            // One size check, on the CLAMPED rect, and deliberately not two. A first pass over the raw
            // window looks like belt-and-braces but is dead code: Rectangle.Intersect can only shrink,
            // so clamped is never larger than window, and anything failing the floor before clamping
            // fails it after. A mutation that removed the pre-check survived the whole probe, which is
            // how that was found -- the surviving mutant was equivalent, not untested.
            var window = new Rectangle(foreground.X, foreground.Y, foreground.Width, foreground.Height);
            Rectangle clamped = Rectangle.Intersect(window, monitor);
            if (clamped.Width < MinimumWindowCaptureWidth ||
                clamped.Height < MinimumWindowCaptureHeight)
                return monitor;
            return clamped;
        }

        /// <summary>
        /// "Also open on this screen: outlook, msedge" for the other windows sharing the monitor that is
        /// actually being CAPTURED. Process names rather than titles on purpose: a title is where the
        /// personal data lives, and for "what else is open" the application is the useful part anyway.
        ///
        /// Keyed on <see cref="ScreenContext.MonitorBounds"/> by intersection, not on the foreground
        /// window's <c>MonitorIndex</c>. It was the latter until BUG-003(b) made capture follow the
        /// COMPANION's monitor: from that point the phrase "on this screen" and the pixels being
        /// described could refer to two different displays, so the model was handed a list of windows the
        /// user could not see next to a picture of somewhere else. Geometry rather than the index because
        /// MonitorBounds is a rect and the index is only meaningful against the host's monitor array.
        /// </summary>
        internal static string DescribeOtherWindows(ScreenContext context)
        {
            if (context == null || context.Windows == null || context.Windows.Count == 0) return "";
            PixelRect mb = context.MonitorBounds;
            var captured = new Rectangle(mb.X, mb.Y, mb.Width, mb.Height);
            bool haveMonitor = captured.Width > 0 && captured.Height > 0;

            var names = new List<string>();
            foreach (ScreenWindow w in context.Windows)
            {
                if (w == null || w.IsForeground) continue;
                if (haveMonitor)
                {
                    var wb = new Rectangle(w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height);
                    Rectangle hit = Rectangle.Intersect(wb, captured);
                    if (hit.Width <= 0 || hit.Height <= 0) continue;
                }
                if (string.IsNullOrWhiteSpace(w.ProcessName)) continue;
                string name = w.ProcessName.Trim();
                if (!names.Contains(name)) names.Add(name);
                if (names.Count >= MaximumOtherWindowsDescribed) break;
            }
            if (names.Count == 0) return "";
            return "Also open on this screen: " + string.Join(", ", names.ToArray()) + "\n";
        }

        /// <summary>
        /// Share (0-100) of a sparse sample taken by the single most common colour. 100 means every
        /// sampled pixel was identical.
        ///
        /// This exists because "the capture showed only the wallpaper" (BUG-003(a)) was unfalsifiable
        /// from a user report: nothing recorded what the capture actually contained, and the privacy
        /// constraint rules out writing the bitmap anywhere. A single number cannot reconstruct a screen
        /// but it does separate "captured a blank surface" from "captured something and the model was
        /// unimpressed", which is the distinction the whole investigation lacked.
        ///
        /// Sampled on a fixed grid rather than per pixel: this runs on every vision ask and the answer
        /// only needs to be indicative.
        /// </summary>
        internal static int UniformityPercent(Bitmap bmp)
        {
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return 0;
            const int Grid = 24;
            var counts = new Dictionary<int, int>();
            int samples = 0;
            int stepX = Math.Max(1, bmp.Width / Grid);
            int stepY = Math.Max(1, bmp.Height / Grid);
            for (int y = 0; y < bmp.Height; y += stepY)
            {
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    Color c = bmp.GetPixel(x, y);
                    int key = (c.R << 16) | (c.G << 8) | c.B;
                    int seen;
                    counts.TryGetValue(key, out seen);
                    counts[key] = seen + 1;
                    samples++;
                }
            }
            if (samples == 0) return 0;
            int top = 0;
            foreach (KeyValuePair<int, int> kv in counts)
                if (kv.Value > top) top = kv.Value;
            return (int)Math.Round(100.0 * top / samples);
        }

        private static Bitmap CaptureScreen(Rectangle b, int maxWidth)
        {
            if (b.Width <= 0 || b.Height <= 0 ||
                b.Width > 32768 || b.Height > 32768)
                throw new InvalidOperationException("The selected display dimensions are invalid.");

            int targetWidth = Math.Min(
                b.Width,
                Math.Max(1, Math.Min(MaximumCaptureWidth, maxWidth)));
            int targetHeight = Math.Max(
                1,
                (int)Math.Round(b.Height * (targetWidth / (double)b.Width)));
            if (targetHeight > MaximumCaptureHeight)
            {
                targetWidth = Math.Max(
                    1,
                    (int)Math.Round(targetWidth *
                        (MaximumCaptureHeight / (double)targetHeight)));
                targetHeight = MaximumCaptureHeight;
            }
            // No pixel-budget throw here: after the two clamps above the product is at most
            // MaximumCapturePixels by construction, and the guard that used to sit here could never
            // fire (F074). A budget check that cannot fail hides the coupling it pretends to enforce.
            System.Diagnostics.Debug.Assert((long)targetWidth * targetHeight <= MaximumCapturePixels);

            Bitmap capture = null;
            IntPtr sourceDc = IntPtr.Zero;
            try
            {
                capture = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
                sourceDc = GetDC(IntPtr.Zero);
                if (sourceDc == IntPtr.Zero)
                    throw new InvalidOperationException("The screen device context is unavailable.");

                using (Graphics graphics = Graphics.FromImage(capture))
                {
                    IntPtr destinationDc = graphics.GetHdc();
                    try
                    {
                        SetStretchBltMode(destinationDc, Halftone);
                        if (!StretchBlt(
                                destinationDc,
                                0,
                                0,
                                targetWidth,
                                targetHeight,
                                sourceDc,
                                b.Left,
                                b.Top,
                                b.Width,
                                b.Height,
                                Srccopy | Captureblt))
                            throw new InvalidOperationException("Screen capture failed.");
                    }
                    finally
                    {
                        graphics.ReleaseHdc(destinationDc);
                    }
                }

                Bitmap result = capture;
                capture = null;
                return result;
            }
            finally
            {
                if (sourceDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, sourceDc);
                if (capture != null) capture.Dispose();
            }
        }

        private static string ToBase64Png(Bitmap bmp)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        // ToBase64PngScaled(bmp, maxWidth) was here. Both vision callers capture at the target width first
        // (CaptureScreen(bounds, VisionMaxWidth) bounds the shot to 896 px), so its resample branch could never
        // run, and the helper read as if the vision path still downscaled twice, which the single-resample note
        // in AskAboutScreenAsync records as the bug being fixed (F075). ComputeSignature still needs Drawing2D.

        private static byte[] ComputeSignature(Rectangle captureBounds)
        {
            const int N = 16;
            using (Bitmap shot = CaptureScreen(captureBounds, 256))
            using (Bitmap small = new Bitmap(N, N, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(shot, 0, 0, N, N);
                }
                byte[] sig = new byte[N * N];
                // Read the whole 16x16 in one LockBits pass instead of 256 GetPixel calls.
                // Format24bppRgb stores pixels as B,G,R (3 bytes each); rows are padded to Stride.
                BitmapData data = small.LockBits(
                    new Rectangle(0, 0, N, N),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);
                try
                {
                    int stride = data.Stride;
                    byte[] pixels = new byte[stride * N];
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    int k = 0;
                    for (int y = 0; y < N; y++)
                    {
                        int row = y * stride;
                        for (int x = 0; x < N; x++)
                        {
                            int p = row + x * 3;
                            byte blue  = pixels[p];
                            byte green = pixels[p + 1];
                            byte red   = pixels[p + 2];
                            sig[k++] = (byte)((red * 30 + green * 59 + blue * 11) / 100);
                        }
                    }
                }
                finally
                {
                    small.UnlockBits(data);
                }
                return sig;
            }
        }

        // ---- OCR: tesseract when present, else Windows' built-in engine ----


        private async Task<string> RunOcrAsync(Bitmap bmp, CancellationToken ct)
        {
            // WHICH engine read the screen, recorded before it runs. ResolveTesseract walks a configured
            // path, two install locations and PATH, and every one of its failure modes was invisible:
            // the probe seam above swallows the exception and SelfTestOcrAsync only runs when a user presses
            // a button. A companion reading the screen through Windows OCR when the user believes they
            // installed Tesseract is a silent downgrade in accuracy, not an error.
            // File NAME only, never the resolved path: the path contains the Windows user name.
            string exe = null;
            string resolveError = null;
            try { exe = ResolveTesseractOnce(); }
            catch (Exception ex) { resolveError = DescribeError(ex); }

            // No Tesseract anywhere -> fall back to the OS engine rather than going screen-blind.
            if (string.IsNullOrEmpty(exe))
            {
                Log("ocr engine: " + (WindowsOcr.IsAvailable ? WindowsOcr.DisplayName : "NONE AVAILABLE") +
                    " tesseract=absent" +
                    (resolveError == null ? "" : " resolveError=" + resolveError) +
                    " configured=" + (string.IsNullOrWhiteSpace(_tesseractPath) ? "no" : "yes"));
                string osText = await WindowsOcr.RecognizeAsync(bmp, ct).ConfigureAwait(false);
                Log("ocr result: engine=windows chars=" + (osText == null ? 0 : osText.Length));
                return osText;
            }

            Log("ocr engine: " + Path.GetFileName(exe) + " tesseract=present" +
                " configured=" + (string.IsNullOrWhiteSpace(_tesseractPath) ? "no" : "yes"));
            SweepStaleOcrScratch();
            string tmpPng = Path.Combine(Path.GetTempPath(), "pet_ocr_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                bmp.Save(tmpPng, ImageFormat.Png);

                ProcessStartInfo psi = BuildOcrStartInfo(exe, tmpPng);

                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.EnableRaisingEvents = true;

                    var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    p.Exited += delegate
                    {
                        try { exited.TrySetResult(p.ExitCode); }
                        catch { exited.TrySetResult(-1); }
                    };

                    if (!p.Start())
                    {
                        Log("ocr result: engine=tesseract chars=0 reason=process-did-not-start");
                        return "";
                    }
                    using (ProcessJob job = ProcessJob.TryAttach(p))
                    {
                        if (p.HasExited) exited.TrySetResult(p.ExitCode);

                        Task<string> stdout = ReadBoundedAsync(p.StandardOutput, 32768);
                        Task<string> stderr = ReadBoundedAsync(p.StandardError, 8192);
                        Task timeout = Task.Delay(TimeSpan.FromSeconds(8), ct);
                        Task finished = await Task.WhenAny(exited.Task, timeout).ConfigureAwait(false);

                        if (finished != exited.Task)
                        {
                            if (job != null) job.Terminate();
                            KillProcessTree(p);
                            AiEndpointPolicy.ObserveTaskFailure(stdout);
                            AiEndpointPolicy.ObserveTaskFailure(stderr);
                            ct.ThrowIfCancellationRequested();
                            Log("ocr result: engine=tesseract chars=0 reason=timeout-8s");
                            return "";
                        }

                        int exitCode = await exited.Task.ConfigureAwait(false);
                        Task drain = Task.WhenAll(stdout, stderr);
                        Task drained = await Task.WhenAny(
                            drain,
                            Task.Delay(TimeSpan.FromSeconds(2), ct)).ConfigureAwait(false);
                        if (drained != drain)
                        {
                            if (job != null) job.Terminate();
                            KillProcessTree(p);
                            AiEndpointPolicy.ObserveTaskFailure(drain);
                            ct.ThrowIfCancellationRequested();
                            Log("ocr result: engine=tesseract chars=0 reason=output-drain-timeout-2s");
                            return "";
                        }

                        await drain.ConfigureAwait(false);
                        if (exitCode != 0)
                        {
                            // Exit code only. stderr can name the image path and the tessdata directory,
                            // both of which carry the Windows user name.
                            Log("ocr result: engine=tesseract chars=0 reason=exit-" + exitCode);
                            return "";
                        }
                        string text = CleanOcr(stdout.Result);
                        Log("ocr result: engine=tesseract chars=" + (text == null ? 0 : text.Length));
                        return text;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log("ocr result: engine=tesseract chars=0 reason=" + DescribeError(ex));
                return "";   // tesseract missing or failed -> no OCR text
            }
            finally
            {
                try { File.Delete(tmpPng); } catch { }
            }
        }

        /// <summary>
        /// Remove OCR scratch images an earlier run could not.
        ///
        /// <see cref="RunOcrAsync"/> deletes its own PNG in a finally, which covers the normal path and
        /// cancellation. It does NOT reliably cover the timeout path: there the tesseract process tree is
        /// killed and the delete runs immediately after, so a child that has not finished dying can still
        /// hold the handle, File.Delete throws, and the catch swallows it. These are full screenshots, so a
        /// run of timeouts would quietly leave megabytes in %TEMP%.
        ///
        /// Sweeping on the NEXT call fixes that without making the OCR path slower or racier: by then the
        /// process is long gone. One hour, so a concurrently running instance's in-flight file is never
        /// taken out from under it. Best-effort throughout; a file still locked is simply left for later.
        /// </summary>
        private static void SweepStaleOcrScratch()
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
                foreach (string path in Directory.GetFiles(Path.GetTempPath(), "pet_ocr_*.png"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) > cutoff) continue;
                        File.Delete(path);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// The tesseract invocation, as a factory so a self-test can assert the part that silently broke:
        /// the stdout/stderr ENCODING. Tesseract writes UTF-8, but a redirected stream with no explicit
        /// encoding is decoded using <c>GetConsoleOutputCP()</c>, which returns 0 in a GUI process with no
        /// console; .NET then decodes through codepage 0 == CP_ACP, i.e. the system ANSI codepage (1252 on a
        /// typical box). Every non-ASCII glyph on screen therefore reached the model as mojibake -- "as®"
        /// arrived as "asÂ®", "—" as "â€"", "’" as "â€™" -- and the model dutifully quoted the garbage back
        /// at the user. Pinning UTF-8 here is the whole fix.
        ///
        /// Deliberately LENIENT UTF-8 (replacement fallback), unlike the strict <c>UTF8Encoding(false, true)</c>
        /// this codebase uses for durable files: strict throws mid-read, and RunOcrAsync's catch turns any throw
        /// into "" -- one bad byte would blind the pet to the whole screen. A replacement char loses one glyph.
        /// </summary>
        internal static ProcessStartInfo BuildOcrStartInfo(string exe, string imagePath)
        {
            var utf8 = new UTF8Encoding(false);
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "\"" + imagePath + "\" stdout",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8,
                CreateNoWindow = true
            };

            // Help tesseract find its language data when running from a portable/toolbox layout.
            try
            {
                string exeDir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    string tessdata = Path.Combine(exeDir, "tessdata");
                    if (Directory.Exists(tessdata))
                        psi.EnvironmentVariables["TESSDATA_PREFIX"] = tessdata;
                }
            }
            catch { }

            return psi;
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters)
        {
            char[] buffer = new char[1024];
            StringBuilder retained = new StringBuilder(Math.Min(maxCharacters, 4096));
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                int remaining = maxCharacters - retained.Length;
                if (remaining > 0)
                    retained.Append(buffer, 0, Math.Min(remaining, read));
            }
            return retained.ToString();
        }

        private static void KillProcessTree(Process process)
        {
            if (process == null) return;
            try
            {
                int processId = process.Id;
                using (Process killer = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                    Arguments = "/PID " + processId + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (killer != null) killer.WaitForExit(2000);
                }
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            }
        }

        /// <summary>
        /// Generation of the configured Tesseract path: bumped by <see cref="ForgetTesseractResolution"/>, read
        /// around each walk by <see cref="ResolveTesseractOnce"/>, so a walk that was already running when the
        /// path changed is not cached over the new path (RA-067).
        /// </summary>
        private int _tesseractGeneration;

        /// <summary>Test seam: runs between the walk and the cache write, the window in which
        /// <see cref="ForgetTesseractResolution"/> can land from another pool thread. Null in the shipped module.</summary>
        internal Action BeforeTesseractCacheForDiagnostics;

        /// <summary><see cref="ResolveTesseract"/> once per brain (F077). A resolution that throws is not cached,
        /// so the next ask tries again and logs again.</summary>
        private string ResolveTesseractOnce()
        {
            if (_tesseractResolved) return _resolvedTesseract;
            int generation = Volatile.Read(ref _tesseractGeneration);
            TesseractResolutionsForDiagnostics++;
            string resolved = ResolveTesseract();
            Action seam = BeforeTesseractCacheForDiagnostics;
            if (seam != null) seam();
            // Cache only if nothing re-pointed the path while this walk ran. Both sides are pool threads (an ask's
            // OCR under AskAboutScreenAsync, the pane's Forget after its ConfigureAwait(false)), and a plain write
            // here could publish a walk of the OLD path as the resolution of the NEW one: the user's chosen engine
            // lost until the next Apply, not "one extra resolution" as this used to say (RA-067). The answer is
            // still returned for this read; the next read walks again, with the new path.
            if (Volatile.Read(ref _tesseractGeneration) == generation)
            {
                _resolvedTesseract = resolved;
                _tesseractResolved = true;
            }
            return resolved;
        }

        /// <summary>
        /// Drop the cached resolution and take the path now configured, so the next read resolves afresh. Reached
        /// through AiSessionManager.ForgetOcrResolution from "Test OCR" (and from "Choose OCR engine...", until lane
        /// feature/layout-aibrain made the path a field that Apply saves and rebuilds the brain with): until 2026-09-30
        /// those reset the cache of the throwaway brain they test with, and the LIVE brain kept the engine it had
        /// resolved when it was built, so an install made mid-session never reached a remark (R-015). The path is
        /// written first and the generation bumped after it, so a walk that read the generation before this call
        /// and the path after it still fails the compare and walks again (RA-067).
        /// </summary>
        internal void ForgetTesseractResolution(string configuredTesseractPath)
        {
            _tesseractPath = configuredTesseractPath;
            Interlocked.Increment(ref _tesseractGeneration);
            _tesseractResolved = false;
        }

        /// <summary>The per-brain resolution, for the self-test, which counts the walks.</summary>
        internal string ResolveTesseractForDiagnostics()
        {
            return ResolveTesseractOnce();
        }

        private string ResolveTesseract()
        {
            // The ladder itself lives in AiExecutablePolicy.Resolve, shared with OllamaClient (F087); only the
            // literals are this executable's.
            return AiExecutablePolicy.Resolve(
                _tesseractPath,
                new[]
                {
                    Environment.ExpandEnvironmentVariables(
                        @"%ProgramFiles%\Tesseract-OCR\tesseract.exe"),
                    Environment.ExpandEnvironmentVariables(
                        @"%LOCALAPPDATA%\Programs\Tesseract-OCR\tesseract.exe")
                },
                "tesseract.exe");
        }

        /// <summary>Self-test the OCR path (the "Test OCR" button): resolve the tesseract engine, then OCR a
        /// tiny generated image of known text. Returns a "✓ …"/"✗ …" status the pane colours green/red, so
        /// OCR never fails silently. Safe to call on a throwaway AiBrain (no backend needed).</summary>
        internal async Task<string> SelfTestOcrAsync(CancellationToken ct)
        {
            string exe;
            // Afresh, not from the cache: the button is pressed right after an install (F077). This runs on the
            // throwaway brain TestOcrAsync builds; the LIVE brain's cache is reset by the module through the session
            // (AiSessionManager.ForgetOcrResolution), which until 2026-09-30 nothing did (R-015).
            try { _tesseractResolved = false; exe = ResolveTesseractOnce(); }
            catch { exe = null; }
            bool usingTesseract = !string.IsNullOrWhiteSpace(exe);
            string engine = usingTesseract ? System.IO.Path.GetFileName(exe) : WindowsOcr.DisplayName;
            if (!usingTesseract && !WindowsOcr.IsAvailable)
                return "✗ No OCR engine found — install Tesseract, or add a Windows language pack.";
            try
            {
                // The probe text carries a REGISTERED SIGN on purpose: it is one UTF-8 byte pair (C2 AE), so
                // if the engine's output is ever decoded as ANSI again it comes back as "Â®" and the check
                // below catches it here instead of in a speech bubble. A missed ® is not a failure (OCR
                // accuracy varies); only a mis-DECODED one is.
                using (Bitmap probe = MakeOcrProbeImage("OCR works " + RegisteredSign))
                {
                    string text = await RunOcrAsync(probe, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(text) && text.IndexOf(AnsiMisdecodeMarker) >= 0)
                        return "✗ OCR text is mis-decoded (encoding bug) — using " + engine + ".";
                    string letters = "";
                    if (!string.IsNullOrEmpty(text))
                        foreach (char c in text) if (char.IsLetter(c)) letters += char.ToLowerInvariant(c);
                    if (letters.Length == 0)
                        return usingTesseract
                            ? "✗ Tesseract found but read no text (language data missing?)."
                            : "✗ Windows OCR read no text (no recognizer for your languages?).";
                    // Naming the engine matters: on the Windows fallback the user would otherwise never
                    // learn that installing Tesseract is an option, or which engine produced their results.
                    if (letters.Contains("ocr") || letters.Contains("works"))
                        return "✓ OCR working — using " + engine +
                            (usingTesseract ? "." : ". Install Tesseract for sharper reading.");
                    return "✓ OCR engine ran (" + engine + "); reading is approximate.";
                }
            }
            catch (Exception ex) { return "✗ OCR failed: " + ex.Message; }
        }

        // A small high-contrast image of known text for the OCR self-test.
        private static Bitmap MakeOcrProbeImage(string text)
        {
            var bmp = new Bitmap(320, 80);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var font = new Font("Segoe UI", 28f, FontStyle.Bold))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(text, font, Brushes.Black, new PointF(10f, 15f));
            }
            return bmp;
        }

        private static string CleanOcr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
            return sb.ToString().Trim();
        }

        // ---- response parsing ----------------------------------------------

        private static BrainResponse Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            // Find the JSON object inside the reply instead of demanding the whole reply BE one.
            // Measured against the two vision models installed here: both gemma3:4b and gemma4:12b
            // wrap the object in a ```json fence unprompted, however firmly the prompt says otherwise.
            // JsonNode.Parse throws on the fence, which sent every such reply down the plain-text
            // fallback below -- and because SanitizeResponseText only collapses whitespace, the
            // companion then SPOKE the envelope: backticks, braces, "text":, "emotion": and all. That
            // is worse than silence and it is almost certainly a large part of what reads as the AI
            // misfiring. Bracket-scanning also absorbs a model that prefixes a sentence of preamble.
            string json = ExtractJsonObject(raw);
            if (json != null)
            {
                try
                {
                    JsonNode o = JsonNode.Parse(json);
                    string text = SanitizeResponseText(JsonRead.Str(o["text"]));
                    string emotion = NormalizeEmotion(JsonRead.Str(o["emotion"]));
                    if (!string.IsNullOrWhiteSpace(text))
                        return new BrainResponse(text, emotion);
                }
                catch
                {
                    // Looked like JSON and was not -> fall through to the plain-text fallback.
                }
            }
            // A model that answered in prose is still worth speaking, but only when the reply carried
            // no JSON envelope at all; otherwise this is the path that read the braces out loud.
            string fallback = SanitizeResponseText(json != null ? StripJsonObject(raw, json) : raw);
            return string.IsNullOrWhiteSpace(fallback)
                ? null
                : new BrainResponse(fallback, "neutral");
        }

        /// <summary>
        /// The outermost <c>{...}</c> in a reply, or null when there is none. Deliberately the first
        /// brace to the LAST one rather than a balanced scan: the payload is a flat two-key object, and
        /// a balanced scan would still be defeated by a brace inside a string value, so the simple form
        /// buys the same result for less that can go wrong.
        /// </summary>
        internal static string ExtractJsonObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return raw.Substring(start, end - start + 1);
        }

        /// <summary>What is left of a reply once its JSON envelope is removed. Keeps a model's prose
        /// preamble while ensuring the fallback can never speak the envelope itself.</summary>
        private static string StripJsonObject(string raw, string json)
        {
            if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(json)) return raw;
            int at = raw.IndexOf(json, StringComparison.Ordinal);
            if (at < 0) return raw;
            string before = raw.Substring(0, at);
            string after = raw.Substring(at + json.Length);
            // Drop a code fence left stranded on either side once the object between them is gone.
            return (before + " " + after).Replace("```json", " ").Replace("```", " ");
        }

        internal static string SanitizeResponseText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var clean = new StringBuilder(Math.Min(value.Length, MaximumResponseCharacters));
            bool pendingSpace = false;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = clean.Length > 0;
                    continue;
                }

                int codeUnits = 1;
                if (char.IsHighSurrogate(c))
                {
                    if (index + 1 >= value.Length ||
                        !char.IsLowSurrogate(value[index + 1]))
                        continue;
                    codeUnits = 2;
                }
                else if (char.IsLowSurrogate(c))
                {
                    continue;
                }

                int spaceUnits = pendingSpace && clean.Length > 0 ? 1 : 0;
                if (clean.Length + spaceUnits + codeUnits > MaximumResponseCharacters)
                    break;
                if (spaceUnits != 0)
                    clean.Append(' ');
                pendingSpace = false;
                clean.Append(c);
                if (codeUnits == 2)
                    clean.Append(value[++index]);
            }
            return CollapseDecorativeRuns(clean.ToString().Trim());
        }

        /// <summary>
        /// Maximum consecutive decorative graphemes -- emoji and other non-ASCII symbols -- in one remark.
        /// </summary>
        private const int MaximumDecorativeRun = 2;

        /// <summary>
        /// Collapses a run of consecutive emoji to <see cref="MaximumDecorativeRun"/>.
        ///
        /// Found by the Disposition audition's first real use: asked about a video, gemma3:4b answered
        /// "Self-assessment: Checked. Seems right in the wool. ... [rocket] [sheep] [bread] [thumb] ..."
        /// and degenerated into an emoji run that never mentioned the video, filling 485 of the 512-character
        /// cap. It is an ENGINE defect, not an audition one -- the sample went through this same method, which
        /// collapses whitespace and bounds length and has no notion of repetition, so a speech bubble could
        /// already show it.
        ///
        /// A post-process rather than a prompt rule, deliberately: a prompt rule constrains expression for
        /// every disposition in order to fix one model's degeneration, and small models drop instructions
        /// from an already-long system prompt. This touches no persona.
        ///
        /// Three choices worth knowing before changing it:
        ///
        /// - It counts a RUN, not repeats of one grapheme. The observed reply alternated
        ///   ([heart-eyes] [smiling] [heart-eyes] [smiling] ...), so a same-grapheme rule would have let most
        ///   of it through. Fifteen consecutive emoji are not intentional voice whether or not they differ.
        /// - A single space does not break a run, because the loop above has already collapsed whitespace, so
        ///   "[a] [b] [c]" is the same defect as "[a][b][c]" with spaces in it.
        /// - ASCII is never decorative. "!!!" is emphasis and "$" is money; collapsing those would change
        ///   every remark, which is the outcome this fix exists to avoid. Letters and digits are never
        ///   touched either, so "hmmm" and a CJK character outside the BMP both survive intact.
        ///
        /// Runs after the length bound above rather than before it: a reply whose emoji run comes FIRST
        /// therefore still loses the text the cap cut. Accepted rather than solved, because reordering means
        /// a second surrogate-safe truncation pass, and the degenerate tail is where every observed case put
        /// it.
        /// </summary>
        internal static string CollapseDecorativeRuns(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";

            var result = new StringBuilder(value.Length);
            int decorativeInRun = 0;
            string pendingSpace = null;
            var walker = StringInfo.GetTextElementEnumerator(value);
            while (walker.MoveNext())
            {
                string element = (string)walker.Current;
                if (element.Length == 1 && element[0] == ' ')
                {
                    // Held, not emitted: a space before a dropped emoji must go with it, or a collapsed run
                    // leaves a double space behind.
                    if (result.Length > 0) pendingSpace = element;
                    continue;
                }

                if (IsDecorativeGrapheme(element))
                {
                    if (decorativeInRun >= MaximumDecorativeRun)
                    {
                        pendingSpace = null;
                        continue;
                    }
                    decorativeInRun++;
                }
                else
                {
                    decorativeInRun = 0;
                }

                if (pendingSpace != null)
                {
                    result.Append(pendingSpace);
                    pendingSpace = null;
                }
                result.Append(element);
            }
            return result.ToString();
        }

        /// <summary>
        /// True for an emoji or other non-ASCII symbol grapheme. Category-based rather than range-based: an
        /// astral-plane test alone would catch CJK Extension B, which is letters.
        /// </summary>
        private static bool IsDecorativeGrapheme(string element)
        {
            if (string.IsNullOrEmpty(element)) return false;
            int codePoint = char.IsHighSurrogate(element[0]) && element.Length > 1 &&
                            char.IsLowSurrogate(element[1])
                ? char.ConvertToUtf32(element[0], element[1])
                : element[0];
            if (codePoint <= 0x7F) return false;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
            return category == UnicodeCategory.OtherSymbol ||
                   category == UnicodeCategory.ModifierSymbol;
        }

        private static string NormalizeEmotion(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEmotionCharacters)
                return "neutral";
            switch (value.Trim().ToLowerInvariant())
            {
                case "happy":
                case "sad":
                case "thinking":
                case "excited":
                case "confused":
                case "neutral":
                    return value.Trim().ToLowerInvariant();
                default:
                    return "neutral";
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            if (_backend != null) _backend.Dispose();
        }

        /// <summary>
        /// Best-effort job containment for OCR. Closing the job kills descendants, including the
        /// case where the OCR parent exits while a child keeps redirected pipes open forever.
        /// </summary>
        private sealed class ProcessJob : IDisposable
        {
            private const uint JobObjectLimitKillOnJobClose = 0x00002000;
            private const int JobObjectExtendedLimitInformation = 9;
            private IntPtr _handle;

            private ProcessJob(IntPtr handle)
            {
                _handle = handle;
            }

            public static ProcessJob TryAttach(Process process)
            {
                IntPtr job = IntPtr.Zero;
                IntPtr info = IntPtr.Zero;
                try
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) return null;

                    var limits = new JobObjectExtendedLimitInformationData();
                    limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformationData));
                    info = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(limits, info, false);
                    if (!SetInformationJobObject(
                            job,
                            JobObjectExtendedLimitInformation,
                            info,
                            (uint)size) ||
                        !AssignProcessToJobObject(job, process.Handle))
                    {
                        CloseHandle(job);
                        job = IntPtr.Zero;
                        return null;
                    }

                    ProcessJob result = new ProcessJob(job);
                    job = IntPtr.Zero;
                    return result;
                }
                catch
                {
                    if (job != IntPtr.Zero) CloseHandle(job);
                    return null;
                }
                finally
                {
                    if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                }
            }

            public void Terminate()
            {
                if (_handle == IntPtr.Zero) return;
                try { TerminateJobObject(_handle, 1); } catch { }
            }

            public void Dispose()
            {
                IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateJobObject(
                IntPtr jobAttributes,
                string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(
                IntPtr job,
                int informationClass,
                IntPtr information,
                uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(
                IntPtr job,
                IntPtr process);

            [DllImport("kernel32.dll")]
            private static extern bool TerminateJobObject(
                IntPtr job,
                uint exitCode);

            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectBasicLimitInformation
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectExtendedLimitInformationData
            {
                public JobObjectBasicLimitInformation BasicLimitInformation;
                public IoCounters IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }
        }
    }
}
