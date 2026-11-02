using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DesktopAICompanion.AgentFlow
{
    // 1.12.3, the owner's report of 2026-11-02: "when it sends a response to an agent card it 'steals' focus ... I have to
    // manually re-focus on the card I was in". AgentFlow never focuses anything itself. Codex's static audit of the
    // installed bundles (2026-11-02; Claude Code 2.1.314, Codex 26.1021.22027, VS Code 1.144.0) found the cards' OWN code
    // focusing: Claude Code's parent effect runs setTimeout(100) -> the composer's safeFocus -> the "Message input"
    // .focus() once the permission count drops to zero (index.js byte 5897170), checking visibility and not document
    // focus; a Codex approval card's primary button has React autoFocus, focused in the mount commit (app-initial byte
    // 550100). Real DOM focus is what VS Code turns into "this webview's group is active".
    //
    // THE FIX IS A STANDING GUARD (round 4). The owner's live test of the press-time hold failed: each Codex approval card
    // took focus the moment it MOUNTED, before any press, and the owner's typing went into it (where Enter approves and
    // Escape denies). So every sweep installs the guard, idempotently, in every agent webview it reads, and it stays while
    // AgentFlow is approving with the setting on; it is removed from every known panel when either goes off and at
    // Shutdown. The press-time hold is gone: the standing guard is already in place in a panel before that panel is
    // pressed, so one mechanism covers mounts, deferred refocus and press-time steals alike.
    //
    // There is NO safety net that moves focus back (removed by decision on Codex's recheck of 52c5d3a: whether the user
    // chose a card cannot be told reliably from inside the webviews). What remains on presses is detection: one line when
    // the pressed card took focus after it.
    internal static partial class CdpApprover
    {
        /// <summary>
        /// The user's gesture allowance: for this long after a TRUSTED pointerdown or mousedown in a guarded document, its
        /// focus and select calls go through although it does not have focus. Codex's recheck traced the installed Codex
        /// "Approval options" control: its dropdown trigger prevents the native pointer and mouse focus, and the menu
        /// focuses itself when it mounts (dropdown and dist modules, 26.1021.22027), so the user's own entry arrives as a
        /// script focus call made while the page is still unfocused. A menu mounts on the next task or animation frame
        /// after the press; a second covers that with a wide margin (a frame is 16 ms, a hidden page's timers align to one
        /// second). After it, suppression resumes. NOT keydown: a key only reaches a document that already has focus, so it
        /// is never an entry whose focus was prevented, and counting it would let a background refocus through for a second
        /// after the user typed in the card and left it.
        /// </summary>
        internal const int FocusGestureMs = 1000;

        /// <summary>How long after a press the detection looks at where focus went: Claude's plain path refocuses 100 ms
        /// after the card clears, and the next Codex card mounted 26 to 240 ms after a press (measured 2026-10-21, see
        /// <see cref="QueueQuietMs"/>). Paid only when another agent panel had focus at the press.</summary>
        internal const int FocusCheckAfterMs = 800;

        /// <summary>
        /// INSTALL THE STANDING GUARD in this panel's app document: 'installed', 'kept' (already there: a no-op, nothing
        /// captured again), 'unreachable' (no conversation document yet; the next sweep tries again) or 'failed' (a throw
        /// half way, unwound at once).
        ///
        /// In THAT document's realm (<c>d.defaultView</c>: the iframe the card lives in, never the webview shell, whose
        /// patch would not reach the iframe's own methods) the own-property descriptors of HTMLElement and SVGElement
        /// focus and HTMLInputElement and HTMLTextAreaElement select (which move DOM focus without going through
        /// HTMLElement.focus, Codex's Chromium probe) are replaced by wrappers that decide AT EVERY CALL: if the document
        /// has focus the original runs, so the user's own card works normally, and switching to it through VS Code (which
        /// focuses the frame first, through window.focus, which is never wrapped) or by the host's keyboard navigation works
        /// too; if it does not, the call does nothing, unless the user's gesture allowance is open
        /// (<see cref="FocusGestureMs"/>, opened by a TRUSTED pointerdown or mousedown in that window; capture phase,
        /// listening only: no preventDefault, no propagation change). A focus event's isTrusted is never used: native focus
        /// events follow programmatic focus too.
        ///
        /// No settle and no cap: it stays until <see cref="FocusGuardRemoveExpression"/> runs or the page is replaced (a
        /// reloaded page has a fresh window and is guarded again at the next sweep). One state per realm
        /// (<c>w.__agentflowFocusGuard</c>), so a second install never captures a wrapper as an "original".
        /// </summary>
        private const string FocusGuardInstallTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView || a.elements < MinElements) return 'unreachable';
  var d = a.doc, w = d.defaultView;
  var g = w.__agentflowFocusGuard;
  if (g && g.active) return 'kept';
  g = { active: true, allowUntil: 0, saved: [] };
  var inputs = ['pointerdown', 'mousedown'];
  var onInput = function (e) {
    if (!e || !e.isTrusted) return;
    g.allowUntil = Date.now() + __GESTURE_MS__;
  };
  g.remove = function () {
    if (!g.active) return;
    g.active = false;
    for (var ri = 0; ri < inputs.length; ri++) { try { w.removeEventListener(inputs[ri], onInput, true); } catch (e) { } }
    for (var si = g.saved.length - 1; si >= 0; si--) {
      var s = g.saved[si];
      try { if (s.owner[s.name] === s.held) Object.defineProperty(s.owner, s.name, s.descriptor); } catch (e) { }
    }
    if (w.__agentflowFocusGuard === g) w.__agentflowFocusGuard = null;
  };
  w.__agentflowFocusGuard = g;
  try {
    var targets = [[w.HTMLElement, 'focus'], [w.SVGElement, 'focus'], [w.HTMLInputElement, 'select'], [w.HTMLTextAreaElement, 'select']];
    for (var hi = 0; hi < targets.length; hi++) {
      var owner = targets[hi][0] && targets[hi][0].prototype, name = targets[hi][1];
      if (!owner) continue;
      var descriptor = Object.getOwnPropertyDescriptor(owner, name);
      if (!descriptor || typeof descriptor.value !== 'function') continue;
      var held = (function (original) {
        return function () {
          if (g.active && Date.now() >= g.allowUntil) {
            var focused = false;
            try { focused = d.hasFocus(); } catch (e) { }
            if (!focused) return undefined;
          }
          return original.apply(this, arguments);
        };
      })(descriptor.value);
      g.saved.push({ owner: owner, name: name, descriptor: descriptor, held: held });
      Object.defineProperty(owner, name, { value: held, writable: descriptor.writable,
                                           enumerable: descriptor.enumerable, configurable: descriptor.configurable });
    }
    for (var ai = 0; ai < inputs.length; ai++) w.addEventListener(inputs[ai], onInput, true);
  } catch (e) { g.remove(); return 'failed'; }
  return 'installed';
})()";

        /// <summary>
        /// REMOVE THE GUARD from every document of this panel (the shell and each reachable iframe, not only the one
        /// appDocument picks today, so a guard installed when another document was the richest is not left behind): each
        /// original descriptor goes back exactly where its wrapper is still the current value, and the listeners are
        /// removed. Answers the number of guards removed, as a string.
        /// </summary>
        private const string FocusGuardRemoveExpression = @"
(function () {
  var docs = [document];
  var frames = document.querySelectorAll('iframe');
  for (var fi = 0; fi < frames.length; fi++) {
    try { if (frames[fi].contentDocument) docs.push(frames[fi].contentDocument); } catch (e) { }
  }
  var removed = 0;
  for (var di = 0; di < docs.length; di++) {
    var w = docs[di].defaultView;
    var g = w && w.__agentflowFocusGuard;
    if (g && g.active) { g.remove(); removed++; }
  }
  return String(removed);
})()";

        /// <summary>The act's own observation, for detection: called immediately before the click, it records on the acted
        /// document's own window this press's id and whether the document lacked focus AT the act.</summary>
        private const string FocusActPrelude = @"
  function agentflowNoteAct(d, action) {
    var w = d && d.defaultView;
    if (!w) return;
    var focused = false;
    try { focused = d.hasFocus(); } catch (e) { }
    w.__agentflowAction = { id: action || '', background: !focused };
  }
";

        /// <summary>A template with the guard's numbers filled in, before any page text is substituted into it.</summary>
        private static string WithFocusGuardNumbers(string template)
        {
            return template.Replace("__GESTURE_MS__", FocusGestureMs.ToString(CultureInfo.InvariantCulture));
        }

        internal static string FocusGuardInstallForSelfTest { get { return WithFocusGuardNumbers(FocusGuardInstallTemplate); } }
        internal static string FocusGuardRemoveForSelfTest { get { return FocusGuardRemoveExpression; } }

        /// <summary>Install the standing guard in one attached panel, on the sweep's own connection. Never throws.</summary>
        private static string InstallFocusGuard(CdpSession session, string sessionId)
        {
            try { return session.Evaluate(sessionId, WithFocusGuardNumbers(FocusGuardInstallTemplate)); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Remove the standing guard from every agent panel on the port (module Shutdown, and when auto-approve or the
        /// setting goes off). Returns the guards removed; a panel that is gone needs nothing and is skipped. Never throws.
        /// </summary>
        internal static int RemoveFocusGuard(int port, int timeoutMs)
        {
            int removed = 0;
            try
            {
                List<KeyValuePair<string, string>> work = AgentTargets(port, timeoutMs);
                if (work.Count == 0) return 0;
                string browserUrl = BrowserSocketUrl(port, timeoutMs);
                if (string.IsNullOrEmpty(browserUrl)) return 0;
                using (var session = new CdpSession(browserUrl, timeoutMs))
                {
                    foreach (KeyValuePair<string, string> item in work)
                    {
                        int count;
                        if (int.TryParse(EvaluateIn(session, item.Key, FocusGuardRemoveExpression), NumberStyles.Integer,
                                         CultureInfo.InvariantCulture, out count))
                            removed += count;
                    }
                }
            }
            catch (Exception) { }
            return removed;
        }

        /// <summary>A fresh id for one press, so detection asks about this press and no other (1.12.3).</summary>
        private static string NewFocusAction()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        /// <summary>Whether a panel's app document has focus: 'focused', 'blurred' or 'gone'. Reads only.</summary>
        private const string FocusProbeExpression = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc) return 'gone';
  var focused = false;
  try { focused = a.doc.hasFocus(); } catch (e) { }
  return focused ? 'focused' : 'blurred';
})()";

        /// <summary>
        /// The pressed panel, after the press, for action <c>__ACTION__</c>: 'moved' only when THIS action's record is on
        /// this document's window, says the document lacked focus AT the act, and the document has focus now. 'other'
        /// (another action's record, or none here), 'was-focused' (focus was already there at the act) or 'blurred'
        /// otherwise. Reads only.
        /// </summary>
        private const string FocusMovedTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView) return 'gone';
  var d = a.doc, w = d.defaultView;
  var act = w.__agentflowAction;
  if (!act || act.id !== __ACTION__) return 'other';
  if (!act.background) return 'was-focused';
  var focused = false;
  try { focused = d.hasFocus(); } catch (e) { }
  return focused ? 'moved' : 'blurred';
})()";

        private static string ForAction(string template, string action)
        {
            return template.Replace("__ACTION__", JsonEncode(action ?? ""));
        }

        internal static string FocusMovedForSelfTest(string action) { return ForAction(FocusMovedTemplate, action); }

        /// <summary>The line on the press when the pressed card took focus after it. Says what happened and that nothing
        /// was moved back; it does not claim whose doing it was, because that cannot be known from here.</summary>
        internal const string FocusMovedNote = "focus moved to the pressed card; AgentFlow did not move it back";

        /// <summary>
        /// Before a press: whether any agent panel other than <paramref name="pressed"/> has focus, which is when a jump to
        /// the pressed card would take the user out of their work and is worth a line. Reads only. Never throws.
        /// </summary>
        private static bool OtherPanelHasFocus(CdpSession session, IList<string> panels, string pressed)
        {
            if (panels == null) return false;
            foreach (string panel in panels)
            {
                if (string.IsNullOrEmpty(panel) || string.Equals(panel, pressed, StringComparison.Ordinal)) continue;
                if (string.Equals(EvaluateIn(session, panel, FocusProbeExpression), "focused", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// DETECTION ONLY, after a press that clicked while another agent panel had focus: wait
        /// <see cref="FocusCheckAfterMs"/> and ask the pressed panel whether focus moved there after the act. The note,
        /// for the press's one log line, is <see cref="FocusMovedNote"/> when it did and null otherwise. Nothing is ever
        /// focused from here: there is no restore path, by decision (see the comment at the top of this file).
        /// </summary>
        private static string NoteFocusMoved(CdpSession session, string pressed, string action)
        {
            try
            {
                Thread.Sleep(FocusCheckAfterMs);
                string moved = EvaluateIn(session, pressed, ForAction(FocusMovedTemplate, action));
                return string.Equals(moved, "moved", StringComparison.Ordinal) ? FocusMovedNote : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Attach, evaluate one expression, detach, on one connection. Null on any failure.</summary>
        private static string EvaluateIn(CdpSession session, string targetId, string expression)
        {
            try
            {
                string sessionId = session.Attach(targetId);
                if (sessionId == null) return null;
                try { return session.Evaluate(sessionId, expression); }
                finally { session.Detach(sessionId); }
            }
            catch (Exception) { return null; }
        }
    }
}
