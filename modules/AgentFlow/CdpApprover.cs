using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// What a read of the Claude Code webview found: the option labels, and the tool being asked
    /// about. An empty <see cref="Options"/> means no prompt was on screen, which is the normal
    /// case and is never an error.
    /// </summary>
    internal sealed class PromptView
    {
        public string TargetId;
        /// <summary>Which agent rendered this prompt. Decides which click expression is used,
        /// because the two panels share no markup at all.</summary>
        public string Agent = CdpApprover.AgentClaude;
        public string ToolName = "";
        /// <summary>
        /// The header's static text with the path removed -- "Make this edit to ?" and so on.
        /// NEVER LOG THIS. Four of the five prompt shapes render their own header, and a shape
        /// nobody has seen yet can put anything in it. It exists to be MATCHED against a table,
        /// and the table's answer is what gets logged.
        /// </summary>
        public string UnsafeHeader = "";
        /// <summary>
        /// The file extension, lowercased, with no dot. Safe: an extension names a kind of file
        /// and never a person. Computed in the renderer precisely so the path stays there.
        /// </summary>
        public string PathExtension = "";
        public List<string> Options = new List<string>();
        /// <summary>Options the UI itself has disabled. Never pressable, whatever they say.</summary>
        public List<bool> Disabled = new List<bool>();
    }

    /// <summary>
    /// Drives the Claude Code webview over the Chrome DevTools Protocol: reads the option rows of a
    /// pending permission prompt, and presses ONE of them when asked to.
    ///
    /// WHY CDP AND NOT KEYSTROKES. Driving the renderer presses a button without focusing, raising
    /// or restoring the window. Keystroke injection needs the window foreground, races with the
    /// user's own typing, and lands wherever focus happens to be -- which for an "approve" action
    /// is an unacceptable failure mode. The cost is a launch flag and one editor restart, handled
    /// by VsCodeSetup, and it reaches Electron hosts only. See docs/agentflow/README.md.
    ///
    /// WHAT THIS FILE DOES NOT DO. It does not decide. It reads text and presses an index it was
    /// given; every judgement belongs to PromptOptions, which refuses anything it does not
    /// recognise. Keeping the reader ignorant of meaning is what stops a selector change from
    /// turning into a wrong click: a DOM that no longer matches yields no options, and no options
    /// is a refusal rather than a guess.
    ///
    /// SELECTORS ARE DERIVED, NOT GUESSED. Read out of the shipped bundle
    /// (~/.vscode/extensions/anthropic.claude-code-&lt;version&gt;/webview/index.js) on 2026-09-18:
    /// the prompt renders a container whose CSS-module class begins "permissionRequestContainer",
    /// and inside it a "buttonContainer" holding one &lt;button&gt; per option, each opening with a
    /// "shortcutNum" span carrying the keyboard digit. The hash suffix ("_qlaBag") is regenerated
    /// per build, so every selector here matches the semantic PREFIX and never the whole class.
    /// </summary>
    /// <summary>What a read of one target actually told us. Four answers, not two.</summary>
    internal enum ReadOutcome
    {
        /// <summary>The target did not answer at all.</summary>
        NoAnswer,
        /// <summary>It answered, but the conversation was not reachable inside it.</summary>
        Unreachable,
        /// <summary>The conversation was reachable and there is no prompt waiting.</summary>
        NoPrompt,
        /// <summary>There is a prompt.</summary>
        Prompt,
    }

    internal static class CdpApprover
    {
        /// <summary>
        /// Turn a raw read result into what it means.
        ///
        /// A function rather than three inline string comparisons, because the bug this file
        /// shipped on 2026-09-18 was exactly the collapse of two of these into one. The reader
        /// queried the OUTER webview document, which contains nothing but a nested iframe, so it
        /// answered "no prompt" whether or not a prompt was on screen. Everything downstream was
        /// correct; it was simply never given a prompt to be correct about, and the module looked
        /// like it was working because "nothing to do" is what a healthy idle module says.
        ///
        /// Separating Unreachable from NoPrompt is what makes that sayable out loud, and having
        /// it as a named function is what makes it assertable without a browser.
        /// </summary>
        internal static ReadOutcome Interpret(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return ReadOutcome.NoAnswer;
            if (string.Equals(raw, "unreachable", StringComparison.Ordinal))
                return ReadOutcome.Unreachable;
            if (string.Equals(raw, "none", StringComparison.Ordinal)) return ReadOutcome.NoPrompt;
            return ReadOutcome.Prompt;
        }

        /// <summary>The read expression, exposed so a self-test can assert it looks where it must.</summary>
        internal static string ReadExpressionForSelfTest { get { return ReadExpression; } }
        internal static string CodexReadExpressionForSelfTest { get { return CodexReadExpression; } }
        internal static string CodexClickExpressionForSelfTest { get { return CodexClickTemplate; } }

        /// <summary>
        /// Returns a JSON string describing the prompt, or the string "none".
        ///
        /// "none" rather than null so the two failure shapes stay apart: a target with no prompt
        /// answers "none", while a target that could not be reached at all yields no value. Only
        /// the second is worth reporting as a fault.
        /// </summary>
        /// <summary>
        /// Resolve the document the conversation is actually IN, and say so when it cannot.
        ///
        /// VS Code nests webviews. The vscode-webview:// target this attaches to holds eight
        /// elements and nothing else: an index.html whose body is a sandboxed same-origin iframe
        /// ("./fake.html"), and the extension's entire UI lives inside THAT. A querySelector on the
        /// outer document therefore finds nothing, ever -- measured 2026-09-18 with a real prompt
        /// on screen: outer 8 elements, inner 6100, four permissionRequest nodes in the inner one.
        ///
        /// The sandbox carries allow-same-origin, so contentDocument is reachable from the parent
        /// and no separate CDP target or isolated world is needed.
        ///
        /// It picks the RICHEST document rather than assuming depth one, and reports the element
        /// count so the caller can tell "I looked and there is no prompt" from "I could not see
        /// anything to look at". That distinction is the whole reason this function exists as
        /// something other than a one-liner: the first version of this file returned 'none' in
        /// both cases, so it answered identically whether or not a prompt was up, and a test that
        /// cannot fail is what let the bug ship.
        /// </summary>
        private const string DocumentPrelude = @"
  function appDocument() {
    var docs = [document];
    var frames = document.querySelectorAll('iframe');
    for (var fi = 0; fi < frames.length; fi++) {
      try { if (frames[fi].contentDocument) docs.push(frames[fi].contentDocument); } catch (e) { }
    }
    var best = null, bestCount = -1;
    for (var di = 0; di < docs.length; di++) {
      var n = docs[di].querySelectorAll('*').length;
      if (n > bestCount) { bestCount = n; best = docs[di]; }
    }
    return { doc: best, elements: bestCount };
  }
  // The bare webview shell is eight elements. A panel with any conversation in it is thousands.
  // Anything under this floor means the content was not reachable, not that it was empty.
  var MinElements = 30;
";

        private const string ReadExpression = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || a.elements < MinElements) return 'unreachable';
  var c = a.doc.querySelector('[class*=""permissionRequestContainer""]');
  if (!c) return 'none';
  var bc = c.querySelector('[class*=""buttonContainer""]');
  if (!bc) return 'none';
  var out = { tool: '', header: '', ext: '', options: [], disabled: [] };
  var hd = c.querySelector('[class*=""permissionRequestHeader""]');
  if (hd) {
    var st = hd.querySelector('strong');
    if (st) out.tool = (st.textContent || '').trim();
    // The header MINUS the path. Only four of the five prompt shapes name a tool in <strong>;
    // the rest say 'Make this edit to <path>?' and friends, so the static half is the only
    // thing that identifies them. Taken off a clone with the path spans removed, because the
    // point is to leave the path behind rather than to trim it afterwards.
    var clone = hd.cloneNode(true);
    var ps = clone.querySelectorAll('[class*=""permissionPath""]');
    for (var pi = 0; pi < ps.length; pi++) ps[pi].parentNode.removeChild(ps[pi]);
    out.header = (clone.textContent || '').replace(/\s+/g, ' ').trim();
    // The EXTENSION is computed here, in the renderer, and the path itself never crosses the
    // wire. Upstream does file_path.split('/').pop(), which on Windows splits nothing, so this
    // span holds a full absolute path -- exactly the thing the diagnostic log must never carry.
    var p = hd.querySelector('[class*=""permissionPath""]');
    if (p) {
      var m = (p.textContent || '').trim().match(/\.([A-Za-z0-9]{1,8})$/);
      if (m) out.ext = m[1].toLowerCase();
    }
  }
  var btns = bc.querySelectorAll('button');
  for (var i = 0; i < btns.length; i++) {
    var b = btns[i];
    var t = b.textContent || '';
    var n = b.querySelector('[class*=""shortcutNum""]');
    if (n) t = t.substring((n.textContent || '').length);
    out.options.push(t.trim());
    out.disabled.push(!!b.disabled);
  }
  return JSON.stringify(out);
})()";

        /// <summary>
        /// Press option <c>{0}</c>, but ONLY if its label is still exactly <c>{1}</c>.
        ///
        /// The re-check is the whole point and is not paranoia. Reading, classifying and pressing
        /// are separate round trips, and the webview is live throughout: the prompt can be answered
        /// by the user, replaced by the next one, or re-rendered with the options in a different
        /// order in between. Pressing a remembered INDEX alone would, in exactly that window, press
        /// whatever had moved into position -- and the option that most often sits beside "Yes" is
        /// "Yes, and don't ask again", a permanent grant. So the label travels with the index and
        /// the click is abandoned when they no longer agree.
        ///
        /// Returns clicked, gone, moved or disabled.
        /// </summary>
        /// <summary>
        /// Read a Codex permission prompt.
        ///
        /// Anchored on aria, not on classes. Codex is styled with Tailwind utilities, so the
        /// container reads "ms-auto flex min-w-0 items-center gap-2 ..." -- layout atoms that say
        /// nothing about what the element IS and change whenever the design does. The one stable
        /// hook on the card is the split button's own aria-label, and the enclosing <form> groups
        /// exactly the prompt's buttons. Both were read off a live prompt on 2026-09-21.
        ///
        /// THE TRIGGER IS NOT AN OPTION. It carries no text, so returning it would hand the
        /// classifier an empty string, and one unrecognised option refuses the whole prompt -- so
        /// including it would make Codex permanently unactionable while looking like a
        /// classifier problem. It is dropped by identity, not by "skip blank labels", because
        /// dropping blanks would also hide a real option that failed to render.
        /// </summary>
        private const string CodexReadExpression = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || a.elements < MinElements) return 'unreachable';
  var trigger = a.doc.querySelector('button[aria-label=""Approval options""]');
  if (!trigger) return 'none';
  var card = trigger.closest ? trigger.closest('form') : null;
  if (!card) card = trigger.parentElement ? trigger.parentElement.parentElement : null;
  if (!card) return 'none';
  var out = { tool: '', header: '', ext: '', options: [], disabled: [] };
  var btns = card.querySelectorAll('button');
  for (var i = 0; i < btns.length; i++) {
    var b = btns[i];
    if (b === trigger) continue;
    out.options.push((b.innerText || b.textContent || '').replace(/\s+/g, ' ').trim());
    out.disabled.push(!!b.disabled);
  }
  if (out.options.length === 0) return 'none';
  return JSON.stringify(out);
})()";

        /// <summary>
        /// Press a Codex option by index, re-checking the label first, exactly as the Claude
        /// clicker does and for the same reason: read, classify and press are separate round
        /// trips and the panel is live throughout.
        ///
        /// The index counts options with the trigger EXCLUDED, so the same filter has to run here
        /// or index 1 means a different button in each expression.
        ///
        /// A plain .click() is enough. That was verified against a live prompt rather than
        /// assumed: the option buttons respond to it, while the split button's own trigger needs
        /// a pointer sequence -- which is why reaching the menu is a separate problem and this
        /// expression does not try.
        ///
        /// TOKENS, NOT string.Format. The first version of this was a format string that
        /// concatenated DocumentPrelude, whose braces are single -- so every call threw
        /// FormatException before the click could happen. Claude's clicker dodges that by
        /// inlining its own doubled-brace copy of the prelude, which works and duplicates the
        /// function. Substituting tokens removes the hazard instead of restating it: there is
        /// no escaping to get right, so there is nothing to get wrong when the JS next changes.
        /// </summary>
        private const string CodexClickTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc) return 'gone';
  var trigger = a.doc.querySelector('button[aria-label=""Approval options""]');
  if (!trigger) return 'gone';
  var card = trigger.closest ? trigger.closest('form') : null;
  if (!card) card = trigger.parentElement ? trigger.parentElement.parentElement : null;
  if (!card) return 'gone';
  var opts = [];
  var btns = card.querySelectorAll('button');
  for (var i = 0; i < btns.length; i++) { if (btns[i] !== trigger) opts.push(btns[i]); }
  var idx = __INDEX__;
  if (idx < 0 || idx >= opts.length) return 'gone';
  var b = opts[idx];
  var t = (b.innerText || b.textContent || '').replace(/\s+/g, ' ').trim();
  if (t !== __LABEL__) return 'moved';
  if (b.disabled) return 'disabled';
  b.click();
  return 'clicked';
})()";

        /// <summary>Fill the Codex click template. Separate and internal so the self-test can
        /// build it and prove both tokens were replaced, which is the check that would have
        /// caught the FormatException before it reached a live prompt.</summary>
        internal static string BuildCodexClick(int index, string expectedLabel)
        {
            return CodexClickTemplate
                .Replace("__INDEX__", index.ToString(CultureInfo.InvariantCulture))
                .Replace("__LABEL__", JsonEncode(expectedLabel ?? ""));
        }

        private const string ClickExpressionFormat = @"
(function () {{
  function appDocument() {{
    var docs = [document];
    var frames = document.querySelectorAll('iframe');
    for (var fi = 0; fi < frames.length; fi++) {{
      try {{ if (frames[fi].contentDocument) docs.push(frames[fi].contentDocument); }} catch (e) {{ }}
    }}
    var best = null, bestCount = -1;
    for (var di = 0; di < docs.length; di++) {{
      var n = docs[di].querySelectorAll('*').length;
      if (n > bestCount) {{ bestCount = n; best = docs[di]; }}
    }}
    return best;
  }}
  var d = appDocument();
  if (!d) return 'gone';
  var c = d.querySelector('[class*=""permissionRequestContainer""]');
  if (!c) return 'gone';
  var bc = c.querySelector('[class*=""buttonContainer""]');
  if (!bc) return 'gone';
  var btns = bc.querySelectorAll('button');
  var i = {0};
  if (i < 0 || i >= btns.length) return 'gone';
  var b = btns[i];
  var t = b.textContent || '';
  var n = b.querySelector('[class*=""shortcutNum""]');
  if (n) t = t.substring((n.textContent || '').length);
  if (t.trim() !== {1}) return 'moved';
  if (b.disabled) return 'disabled';
  b.click();
  return 'clicked';
}})()";

        /// <summary>The marker that identifies a Claude Code webview among VS Code's targets.</summary>
        internal const string ClaudeTargetMarker = "extensionId=Anthropic.claude-code";

        /// <summary>
        /// Find a pending prompt and, if <paramref name="press"/> says so, press the approve-once
        /// row. Returns a line worth logging, or null when there was nothing to do.
        ///
        /// One browser connection for the whole sweep. Attaching per target through the BROWSER
        /// endpoint rather than opening each target's own debugger socket, because the per-target
        /// socket answers HTTP 500 here -- measured against VS Code 1.138 / Chrome 148 on
        /// 2026-09-18 -- which is what a target that already has a client looks like. Target.
        /// attachToTarget with flatten works on the same targets the direct socket refuses.
        /// </summary>
        public static string Sweep(int port, Func<PromptView, string> press, int timeoutMs,
                                   out bool sawPanel)
        {
            sawPanel = false;

            // Both agents, Claude first. They render in separate webviews on the SAME debug
            // port, share no markup, and are read by different expressions -- so the agent
            // travels with the target rather than being inferred from what the page looks like.
            var work = new List<KeyValuePair<string, string>>();
            foreach (string id in ClaudeTargetIds(port, timeoutMs))
                work.Add(new KeyValuePair<string, string>(id, AgentClaude));
            foreach (string id in CodexTargetIds(port, timeoutMs))
                work.Add(new KeyValuePair<string, string>(id, AgentCodex));
            if (work.Count == 0) return null;

            string browserUrl = BrowserSocketUrl(port, timeoutMs);
            if (string.IsNullOrEmpty(browserUrl)) return null;

            bool sawReadable = false;
            string pressed = null;
            try
            {
                using (var session = new CdpSession(browserUrl, timeoutMs))
                {
                    foreach (KeyValuePair<string, string> item in work)
                    {
                        string targetId = item.Key;
                        string agent = item.Value;
                        string sessionId = session.Attach(targetId);
                        if (sessionId == null) continue;
                        try
                        {
                            string raw = session.Evaluate(sessionId,
                                agent == AgentCodex ? CodexReadExpression : ReadExpression);
                            ReadOutcome outcome = Interpret(raw);
                            // Unreachable is NOT "no prompt", and the difference is load-bearing:
                            // conflating them is precisely how this shipped unable to see anything
                            // while looking healthy.
                            if (outcome == ReadOutcome.NoAnswer
                                || outcome == ReadOutcome.Unreachable) continue;
                            sawReadable = true;
                            if (outcome == ReadOutcome.NoPrompt) continue;
                            PromptView view = Parse(targetId, raw);
                            if (view == null || view.Options.Count == 0) continue;
                            view.Agent = agent;
                            string note = press(view);
                            // BREAK, never return. Returning from here skipped the
                            // `sawPanel = sawReadable` below, so every sweep that actually
                            // pressed something reported "cannot see the agent panel" -- the tray
                            // went amber at the exact moment the feature worked.
                            if (note != null) { pressed = note; break; }
                        }
                        finally { session.Detach(sessionId); }
                    }
                }
            }
            catch (Exception exception)
            {
                // Deliberately broad. The editor closing, the port moving, a target vanishing
                // mid-read: all mean the same thing to the caller, which is that there is no
                // answer, so do nothing. An approver that threw on a closed editor would take the
                // poll down with it.
                //
                // But it SAYS SO now. Returning null here made a sweep that threw identical to a
                // sweep that found nothing, and the two want opposite responses: one is a closed
                // editor, the other is a bug in the reader. Type only, never the message, which
                // is the one string in scope that could carry page text.
                sawPanel = false;
                return "the sweep could not finish (" + exception.GetType().Name + ")";
            }
            // Assigned from the local AFTER the try, so a throw half way through a sweep reports
            // "could not see" rather than leaving a stale true behind: the tray dot goes green on
            // this, and green has to mean a panel was read on THIS pass.
            sawPanel = sawReadable;
            if (pressed != null) return pressed;
            if (!sawReadable)
                return "cannot see inside the agent panel: the debugging port answers, "
                       + "but nothing in it exposes the conversation. Approving cannot work "
                       + "until that is fixed -- it is not the same as 'no prompt waiting'.";
            return null;
        }

        /// <summary>
        /// Press one row of a prompt already read, re-checking its label first.
        ///
        /// Takes its own connection rather than reusing the sweep's, because the click happens
        /// after the classifier has run and the caller decides whether it happens at all; threading
        /// a live session through that decision would put an open debugger socket on the far side
        /// of a branch that usually says no.
        /// </summary>
        public static string Click(int port, string targetId, int index, string expectedLabel,
                                   int timeoutMs)
        {
            return Click(port, targetId, index, expectedLabel, timeoutMs, AgentClaude);
        }

        public static string Click(int port, string targetId, int index, string expectedLabel,
                                   int timeoutMs, string agent)
        {
            if (targetId == null || index < 0 || expectedLabel == null) return "gone";
            string browserUrl = BrowserSocketUrl(port, timeoutMs);
            if (string.IsNullOrEmpty(browserUrl)) return "gone";
            string expression = string.Equals(agent, AgentCodex, StringComparison.Ordinal)
                ? BuildCodexClick(index, expectedLabel)
                : string.Format(CultureInfo.InvariantCulture, ClickExpressionFormat,
                      index.ToString(CultureInfo.InvariantCulture), JsonEncode(expectedLabel));
            try
            {
                using (var session = new CdpSession(browserUrl, timeoutMs))
                {
                    string sessionId = session.Attach(targetId);
                    if (sessionId == null) return "gone";
                    try
                    {
                        string result = session.Evaluate(sessionId, expression);
                        return string.IsNullOrEmpty(result) ? "gone" : result;
                    }
                    finally { session.Detach(sessionId); }
                }
            }
            catch (Exception) { return "gone"; }
        }

        /// <summary>
        /// Turn the read expression's JSON into a view. Internal so the self-test can exercise the
        /// parsing without a running editor, which is the only half of this file a CI box can see.
        /// </summary>
        internal static PromptView Parse(string targetId, string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    var view = new PromptView
                    {
                        TargetId = targetId,
                        ToolName = Str(root, "tool"),
                        UnsafeHeader = Str(root, "header"),
                        PathExtension = Str(root, "ext"),
                    };
                    JsonElement options;
                    if (!root.TryGetProperty("options", out options)
                        || options.ValueKind != JsonValueKind.Array)
                        return null;
                    foreach (JsonElement option in options.EnumerateArray())
                        view.Options.Add(option.ValueKind == JsonValueKind.String
                            ? (option.GetString() ?? "") : "");
                    JsonElement disabled;
                    if (root.TryGetProperty("disabled", out disabled)
                        && disabled.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement flag in disabled.EnumerateArray())
                            view.Disabled.Add(flag.ValueKind == JsonValueKind.True);
                    // A short disabled list must not make a row look enabled by omission, and it
                    // must not throw either. Pad, so index lookups are always in range.
                    while (view.Disabled.Count < view.Options.Count) view.Disabled.Add(false);
                    return view;
                }
            }
            catch (JsonException) { return null; }
        }

        /// <summary>
        /// The webview targets Claude Code owns.
        ///
        /// Filtered on extensionId rather than title or URL host, because it is the only stable
        /// identifier available: the title is the user's file name and the vscode-webview:// host
        /// is a per-session GUID.
        /// </summary>
        /// <summary>The marker that identifies a Codex webview among VS Code's targets.
        ///
        /// Codex renders in a webview exactly as Claude Code does, on the same debug port. That
        /// is worth stating because the module's own notes said otherwise for a while: the
        /// transcript-side gap is real, but "Codex is a terminal app so it cannot be pressed"
        /// was wrong, and was measured wrong on 2026-09-21 by reading this list.</summary>
        internal const string CodexTargetMarker = "extensionId=openai.chatgpt";

        internal const string AgentClaude = "claude";
        internal const string AgentCodex = "codex";

        internal static List<string> ClaudeTargetIds(int port, int timeoutMs)
        {
            return TargetIds(port, ClaudeTargetMarker, timeoutMs);
        }

        internal static List<string> CodexTargetIds(int port, int timeoutMs)
        {
            return TargetIds(port, CodexTargetMarker, timeoutMs);
        }

        internal static List<string> TargetIds(int port, string marker, int timeoutMs)
        {
            var ids = new List<string>();
            string json = HttpGet(Endpoint(port, "/json/list"), timeoutMs);
            if (string.IsNullOrEmpty(json)) return ids;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Array) return ids;
                    foreach (JsonElement item in document.RootElement.EnumerateArray())
                    {
                        if (Str(item, "url").IndexOf(marker,
                                StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        string id = Str(item, "id");
                        if (id.Length > 0) ids.Add(id);
                    }
                }
            }
            catch (JsonException) { }
            return ids;
        }

        private static string BrowserSocketUrl(int port, int timeoutMs)
        {
            string json = HttpGet(Endpoint(port, "/json/version"), timeoutMs);
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                    return Str(document.RootElement, "webSocketDebuggerUrl");
            }
            catch (JsonException) { return null; }
        }

        private static string Endpoint(int port, string path)
        {
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + path;
        }

        // ---- the wire -----------------------------------------------------------------------

        /// <summary>
        /// One browser-level CDP connection, with the request/reply matching CDP needs.
        ///
        /// Replies are matched by ID and everything else is discarded, because the socket carries
        /// events as well as answers: attaching to a target immediately produces target lifecycle
        /// notifications, and a reader that took the next message as its reply would read one of
        /// those instead. That failure is intermittent and looks like a flaky editor, which is the
        /// worst way for it to present.
        /// </summary>
        private sealed class CdpSession : IDisposable
        {
            private readonly ClientWebSocket _socket = new ClientWebSocket();
            private readonly CancellationTokenSource _cancel;
            private int _nextId;

            public CdpSession(string url, int timeoutMs)
            {
                _cancel = new CancellationTokenSource(Math.Max(1000, timeoutMs * 4));
                try
                {
                    _socket.ConnectAsync(new Uri(url), _cancel.Token).GetAwaiter().GetResult();
                }
                catch
                {
                    // `using (var s = new CdpSession(...))` never binds when the constructor
                    // throws, so Dispose -- the only thing that closes the socket and the token
                    // source -- never runs. And this throws on the most ORDINARY path there is:
                    // the user quits VS Code in the moment between the /json/version GET and
                    // this connect.
                    Dispose();
                    throw;
                }
            }

            public string Attach(string targetId)
            {
                JsonElement reply;
                if (!Send("{\"method\":\"Target.attachToTarget\",\"params\":{\"targetId\":"
                          + JsonEncode(targetId) + ",\"flatten\":true}}", out reply))
                    return null;
                JsonElement result;
                if (!reply.TryGetProperty("result", out result)) return null;
                JsonElement sessionId;
                if (!result.TryGetProperty("sessionId", out sessionId)) return null;
                return sessionId.ValueKind == JsonValueKind.String ? sessionId.GetString() : null;
            }

            public void Detach(string sessionId)
            {
                if (string.IsNullOrEmpty(sessionId)) return;
                JsonElement ignored;
                try
                {
                    Send("{\"method\":\"Target.detachFromTarget\",\"params\":{\"sessionId\":"
                         + JsonEncode(sessionId) + "}}", out ignored);
                }
                catch (Exception) { }
            }

            /// <summary>Runtime.evaluate in one target. Null unless the result is a plain string.</summary>
            public string Evaluate(string sessionId, string expression)
            {
                JsonElement reply;
                if (!Send("{\"sessionId\":" + JsonEncode(sessionId)
                          + ",\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":"
                          + JsonEncode(expression)
                          + ",\"returnByValue\":true,\"awaitPromise\":false}}", out reply))
                    return null;
                JsonElement outer;
                if (!reply.TryGetProperty("result", out outer)) return null;
                // An expression that threw comes back with exceptionDetails and a result of
                // whatever was thrown. Treat it as no answer: the expressions here are written to
                // return a string, so anything else means they did not run as this file assumes.
                if (outer.TryGetProperty("exceptionDetails", out _)) return null;
                JsonElement inner;
                if (!outer.TryGetProperty("result", out inner)) return null;
                JsonElement value;
                if (!inner.TryGetProperty("value", out value)) return null;
                return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }

            private bool Send(string bodyWithoutId, out JsonElement reply)
            {
                reply = default(JsonElement);
                _nextId++;
                int id = _nextId;
                string json = "{\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ","
                              + bodyWithoutId.Substring(1);
                byte[] payload = Encoding.UTF8.GetBytes(json);
                _socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true,
                    _cancel.Token).GetAwaiter().GetResult();

                // Bounded, so a target that only ever sends events cannot hold the poll thread.
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    string message = Receive();
                    if (message == null) return false;
                    try
                    {
                        // `using`, not paired Dispose calls. Two throws here are NOT
                        // JsonException and so escaped both the catch and the disposal,
                        // abandoning a pooled buffer of up to the 1 MiB receive cap:
                        // TryGetProperty throws when the root is not an object, and GetInt32
                        // throws on a number outside int range. Neither is something CDP
                        // produces in normal operation, which is precisely why it would never
                        // have been noticed.
                        using (var document = JsonDocument.Parse(message))
                        {
                            JsonElement replyId;
                            if (document.RootElement.TryGetProperty("id", out replyId)
                                && replyId.ValueKind == JsonValueKind.Number
                                && replyId.GetInt32() == id)
                            {
                                reply = document.RootElement.Clone();
                                return true;
                            }
                        }
                    }
                    catch (JsonException) { }
                    catch (InvalidOperationException) { }
                    catch (FormatException) { }
                }
                return false;
            }

            private string Receive()
            {
                var builder = new StringBuilder();
                byte[] buffer = new byte[16 * 1024];
                const int Cap = 1024 * 1024;
                while (true)
                {
                    WebSocketReceiveResult result = _socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), _cancel.Token)
                        .GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage) break;
                    if (builder.Length > Cap) break;
                }
                return builder.ToString();
            }

            public void Dispose()
            {
                try
                {
                    if (_socket.State == WebSocketState.Open)
                        _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", _cancel.Token)
                            .GetAwaiter().GetResult();
                }
                catch (Exception) { }
                _socket.Dispose();
                _cancel.Dispose();
            }
        }

        /// <summary>
        /// One client for the life of the process, not one per call.
        ///
        /// The sweep makes two of these calls and a press makes a third, every ten seconds
        /// while auto-approve is on -- roughly 17,000 HttpClient and SocketsHttpHandler pairs
        /// a day. The `using` did dispose each one, so this was churn rather than unbounded
        /// growth, but every disposal closed a fresh loopback connection into TIME_WAIT: a
        /// steady-state floor of about 48 sockets doing nothing, and a number the next person
        /// to read a netstat would have had to chase.
        ///
        /// The timeout moves to a per-request CancellationToken because a shared client's
        /// Timeout cannot be changed once a request has been made on it.
        /// </summary>
        private static readonly HttpClient Http = new HttpClient();

        private static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                using (var cancel = new CancellationTokenSource(Math.Max(200, timeoutMs)))
                using (HttpResponseMessage response =
                           Http.GetAsync(url, cancel.Token).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode) return null;
                    return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
            catch (Exception) { return null; }
        }

        private static string Str(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value)) return "";
            return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "") : "";
        }

        /// <summary>
        /// Quote a string for embedding in JSON, including inside the click expression.
        ///
        /// JsonSerializer rather than hand-rolled quoting, because the value being embedded is an
        /// option LABEL read back out of someone else's UI: it can hold quotes, backslashes, line
        /// separators and anything else, and a label that broke out of its quotes would turn the
        /// re-check into arbitrary script running in the editor's renderer.
        /// </summary>
        internal static string JsonEncode(string text)
        {
            return JsonSerializer.Serialize(text ?? "");
        }
    }
}
