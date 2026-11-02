using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DesktopAICompanion.AgentFlow
{
    // 1.12.3, the owner's report of 2026-11-02: "when it sends a response to an agent card it 'steals' focus ... I have to
    // manually re-focus on the card I was in". AgentFlow never focuses anything itself: it clicks a button in the card's
    // page over CDP. Codex's static audit of the installed bundles (2026-11-02; Claude Code 2.1.314, Codex 26.1021.22027,
    // VS Code 1.144.0) found the cards' OWN code focusing afterwards: Claude Code's parent effect
    // runs setTimeout(100) -> the composer's safeFocus -> the "Message input" .focus() once the permission count drops to
    // zero (index.js 2.1.314 byte 5897170), checking visibility and not document focus; a Codex follow-on approval card's
    // primary button has React autoFocus, focused in the mount commit (app-initial byte 550100). Real DOM focus is what
    // VS Code turns into "this webview's group is active" (its webview host samples document.hasFocus() every 250 ms).
    //
    // THE FIX IS THE HOLD, in the pressed page while its card settles. There is NO safety net that moves focus back: it was
    // built, and removed by decision on Codex's recheck of 52c5d3a, because whether the user chose the pressed card cannot
    // be told reliably from inside the webviews (a selection made through VS Code itself reaches neither page as input).
    // What remains is detection: one line on the press when the pressed card took focus after it.
    internal static partial class CdpApprover
    {
        /// <summary>
        /// How long the pressed card must stay quiet (no approval card left on it) before the hold lets go. Claude's
        /// composer refocus is a setTimeout(100) inside a React effect that runs when the card clears (audit, above);
        /// this is that 100 ms, a render, and up to a second of background-timer alignment, with margin.
        /// </summary>
        internal const int FocusSettleMs = 1500;

        /// <summary>
        /// The hard cap, from the latest press. Past it a wrapper stops suppressing anything, even if the page's timers
        /// have stalled, because each call checks the cap itself; the original descriptors and the listeners are put
        /// back when the page next runs the hold's timer, so a page whose timers never run again keeps transparent
        /// wrappers until it does. Claude's diff path races its acceptance against 5000 ms and then schedules a further
        /// 5000 ms fallback before the card clears (index.js byte 5576136), so a refocus can come about ten seconds after
        /// the press. This bounds the suppression; it is NOT a claim that every deferred focus lands inside it.
        /// </summary>
        internal const int FocusHoldCapMs = 15000;

        /// <summary>How often the hold looks at whether its card has settled, in the page's own timers.</summary>
        internal const int FocusPollMs = 250;

        /// <summary>
        /// The user's gesture allowance: for this long after a TRUSTED pointerdown or mousedown in the held
        /// document, its focus and select calls go through although it does not have focus. Codex's recheck traced the
        /// installed Codex "Approval options" control: its dropdown trigger prevents the native pointer and mouse focus,
        /// and the menu focuses itself when it mounts (dropdown and dist modules, 26.1021.22027), so the user's own entry
        /// arrives as a script focus call made while the page is still unfocused. A menu mounts on the next task or
        /// animation frame after the press; a second covers that with a wide margin (a frame is 16 ms, a hidden page's
        /// timers align to one second). After it, background suppression resumes; the hold itself does not end.
        /// </summary>
        internal const int FocusGestureMs = 1000;

        /// <summary>How long after a press the detection looks at where focus went: Claude's plain path refocuses 100 ms
        /// after the card clears, and the next Codex card mounted 26 to 240 ms after a press (measured 2026-10-21, see
        /// <see cref="QueueQuietMs"/>). Paid only when another agent panel had focus at the press.</summary>
        internal const int FocusCheckAfterMs = 800;

        /// <summary>
        /// THE HOLD, in the pressed page, called immediately before the act (after every guard: card id, label
        /// re-check, disabled). Returns the hold's release when THIS call created it, so the caller can release it at
        /// once if the act throws; null otherwise.
        ///
        /// ALWAYS INSTALLED, decided per call. In THAT document's realm (<c>d.defaultView</c>: the nested iframe the card
        /// lives in, never the webview shell, whose patch would not reach the iframe's own methods) the focus primitives
        /// are replaced by wrappers that ask, at every call, whether the document has focus: if it does, the call goes
        /// to the original (the user is in this card, and the card's own focus is theirs); if it does not, the call does
        /// nothing while the hold lasts, unless the user's gesture allowance is open (below). So a press made while the
        /// user is in the card still protects them if they leave before it settles. Held: HTMLElement and SVGElement
        /// focus, and HTMLInputElement and HTMLTextAreaElement select, which move DOM focus without going through
        /// HTMLElement.focus (Codex's Chromium probe, 2026-11-02).
        ///
        /// SCOPED TO THE CARD, NOT TO A CLOCK. No finite window after a click covers the deferred paths, so the hold
        /// stays while <paramref name="busy"/> says a card is still on the page, and lets go <see cref="FocusSettleMs"/>
        /// after it last did, polled every <see cref="FocusPollMs"/>; <see cref="FocusHoldCapMs"/> from the latest press
        /// bounds it whatever the card does. The user's input does NOT end it: typing in the card and then leaving must
        /// still leave the later background focus suppressed.
        ///
        /// THE USER'S GESTURE: a TRUSTED pointerdown or mousedown in that window (capture phase, listening only: no
        /// preventDefault, no propagation change) opens <see cref="FocusGestureMs"/> in which the document's own focus
        /// calls go through unfocused, so an entry whose native focus the control prevented (a dropdown whose menu
        /// focuses itself) still lands. NOT keydown: a key is only delivered to a document that already has focus, so it
        /// is never an entry whose focus was prevented, and counting it would only let a deferred steal through for a
        /// second after the user typed in the card and left it. A focus event's isTrusted is never used: native focus
        /// events follow programmatic focus too.
        ///
        /// THE ACT'S OWN OBSERVATION, for detection: <c>w.__agentflowAction</c>, on the acted document's own window,
        /// records this action's id (<paramref name="action"/>) and whether the document lacked focus AT the act.
        ///
        /// RESTORED EXACTLY: each original property descriptor is put back on the object that owned it, and only where
        /// the wrapper is still the current value (a wrapper someone else wrapped over stays, and is transparent once
        /// <c>h.active</c> is false); every listener is removed. One state per realm (<c>w.__agentflowFocusHold</c>): a
        /// press while a hold is active only refreshes it (its card test, its cap, its quiet clock), and never captures,
        /// so a wrapper can never be recorded as an "original".
        ///
        /// NOT held: window.focus (VS Code's webview host focuses the frame through contentWindow.focus() when the user
        /// activates the panel, and holding it would eat that), the Selection API (no traced path reaches it). No
        /// ".focus(" and no ".click()" in this text: the session-limit send's self-test forbids both.
        /// </summary>
        private const string FocusHoldPrelude = @"
  function agentflowHoldFocus(d, busy, action) {
    var w = d && d.defaultView;
    if (!w) return null;
    var focusedAtAct = false;
    try { focusedAtAct = d.hasFocus(); } catch (e) { }
    w.__agentflowAction = { id: action || '', background: !focusedAtAct };
    var now = Date.now();
    var h = w.__agentflowFocusHold;
    if (h && h.active) {
      h.busy = busy;
      h.capAt = now + __HOLD_CAP_MS__;
      h.quietSince = 0;
      return null;
    }
    h = { active: true, busy: busy, capAt: now + __HOLD_CAP_MS__, quietSince: 0, allowUntil: 0, saved: [], timer: 0 };
    var inputs = ['pointerdown', 'mousedown'];
    var onInput = function (e) {
      if (!e || !e.isTrusted) return;
      h.allowUntil = Date.now() + __GESTURE_MS__;
    };
    h.release = function () {
      if (!h.active) return;
      h.active = false;
      try { w.clearTimeout(h.timer); } catch (e) { }
      for (var ri = 0; ri < inputs.length; ri++) { try { w.removeEventListener(inputs[ri], onInput, true); } catch (e) { } }
      for (var si = h.saved.length - 1; si >= 0; si--) {
        var s = h.saved[si];
        try { if (s.owner[s.name] === s.held) Object.defineProperty(s.owner, s.name, s.descriptor); } catch (e) { }
      }
      if (w.__agentflowFocusHold === h) w.__agentflowFocusHold = null;
    };
    h.tick = function () {
      if (!h.active) return;
      var t = Date.now();
      if (t >= h.capAt) { h.release(); return; }
      var settling = false;
      try { settling = !!(h.busy && h.busy()); } catch (e) { }
      if (settling) h.quietSince = 0;
      else if (!h.quietSince) h.quietSince = t;
      else if (t - h.quietSince >= __SETTLE_MS__) { h.release(); return; }
      h.timer = w.setTimeout(h.tick, __POLL_MS__);
    };
    w.__agentflowFocusHold = h;
    try {
      var targets = [[w.HTMLElement, 'focus'], [w.SVGElement, 'focus'], [w.HTMLInputElement, 'select'], [w.HTMLTextAreaElement, 'select']];
      for (var hi = 0; hi < targets.length; hi++) {
        var owner = targets[hi][0] && targets[hi][0].prototype, name = targets[hi][1];
        if (!owner) continue;
        var descriptor = Object.getOwnPropertyDescriptor(owner, name);
        if (!descriptor || typeof descriptor.value !== 'function') continue;
        var held = (function (original) {
          return function () {
            var t = Date.now();
            if (h.active && t < h.capAt && t >= h.allowUntil) {
              var focused = false;
              try { focused = d.hasFocus(); } catch (e) { }
              if (!focused) return undefined;
            }
            return original.apply(this, arguments);
          };
        })(descriptor.value);
        h.saved.push({ owner: owner, name: name, descriptor: descriptor, held: held });
        Object.defineProperty(owner, name, { value: held, writable: descriptor.writable,
                                             enumerable: descriptor.enumerable, configurable: descriptor.configurable });
      }
      for (var ai = 0; ai < inputs.length; ai++) w.addEventListener(inputs[ai], onInput, true);
      h.timer = w.setTimeout(h.tick, __POLL_MS__);
    } catch (e) { h.release(); return null; }
    return h.release;
  }
";

        /// <summary>The prelude with its numbers filled in. Applied to a template BEFORE any page text is substituted
        /// into it, so a label can never spell a token.</summary>
        private static string WithFocusHoldMs(string template)
        {
            return template
                .Replace("__HOLD_CAP_MS__", FocusHoldCapMs.ToString(CultureInfo.InvariantCulture))
                .Replace("__SETTLE_MS__", FocusSettleMs.ToString(CultureInfo.InvariantCulture))
                .Replace("__POLL_MS__", FocusPollMs.ToString(CultureInfo.InvariantCulture))
                .Replace("__GESTURE_MS__", FocusGestureMs.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>A fresh id for one press, so detection asks about this press and no other (1.12.3).</summary>
        private static string NewFocusAction()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        /// <summary>Whether a panel's app document has focus: 'focused', 'blurred' or 'gone'. Reads only; remembers nothing.</summary>
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
            return WithFocusHoldMs(template).Replace("__ACTION__", JsonEncode(action ?? ""));
        }

        internal static string FocusMovedForSelfTest(string action) { return ForAction(FocusMovedTemplate, action); }

        /// <summary>The line on the press when the pressed card took focus after it. Says what happened and that nothing
        /// was moved back; it does not claim whose doing it was, because that cannot be known from here.</summary>
        internal const string FocusMovedNote = "focus moved to the pressed card; AgentFlow did not move it back";

        /// <summary>
        /// Before a press: whether any agent panel other than <paramref name="pressed"/> has focus, which is when a jump to
        /// the pressed card would take the user out of their work and is worth a line. Reads only. Never throws: a panel
        /// that cannot be probed counts as not focused.
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

        /// <summary>Attach, evaluate one expression, detach, on the press's own connection. Null on any failure.</summary>
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
