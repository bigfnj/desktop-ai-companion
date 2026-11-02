using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DesktopAICompanion.AgentFlow
{
    // 1.12.3, the owner's report of 2026-11-02: "when it sends a response to an agent card it 'steals' focus ... I have to
    // manually re-focus on the card I was in". AgentFlow never focuses anything itself: it clicks a button in the card's
    // page over CDP. The card's OWN code then focuses its prompt box after the answer, and VS Code activates whichever
    // webview took DOM focus. Two defences, both here: a hold in the page while it is pressed, and a net that puts the
    // user back in the panel they were in if focus left it anyway.
    internal static partial class CdpApprover
    {
        /// <summary>
        /// How long a press holds focus calls in a card the user is not in. The refocus that follows an answered card
        /// rides the card's round trip to its extension and back: the next Codex card was measured in the DOM 26 to
        /// 240 ms after AgentFlow's press (2026-10-21, see <see cref="QueueQuietMs"/>), so this is about three times
        /// the slowest seen, the margin QueueQuietMs uses. Cheap to hold long: the hold only ever swallows a focus call
        /// made while the page does NOT have focus, and the user moving into the card ends it for every later call.
        /// To be revised against the vendor audit (where and when each card focuses after an answer).
        /// </summary>
        internal const int FocusHoldMs = 750;

        /// <summary>How long after a press the safety net looks at where focus went: just past the hold, so a focus the
        /// hold did not cover has happened by then. Paid only when another agent panel had focus at the press.</summary>
        internal const int FocusCheckAfterMs = FocusHoldMs + 50;

        /// <summary>
        /// THE HOLD, in the pressed page, called immediately before the act (after every guard: card id, label
        /// re-check, disabled). If the app document HAS focus the user is in this card and nothing is touched. If it
        /// does not, the focus methods of THAT document's realm (<c>d.defaultView</c>, the nested iframe the card lives
        /// in, not the webview shell) are wrapped for <see cref="FocusHoldMs"/>: a call while the hold is active and
        /// the document still lacks focus does nothing; any other call goes to the original.
        ///
        /// RESTORED EXACTLY: the timer that releases it is armed BEFORE the act, so a click that throws still
        /// restores; release puts each original function object back on the object that owned it, and only where the
        /// wrapper is still the current value (a wrapper someone else wrapped over stays, and goes transparent because
        /// it checks <c>h.active</c>). OVERLAP: the state lives on the realm, and a press while a hold is active only
        /// extends it and re-arms its timer, so it never captures the wrapper as an "original". <c>bypass</c> lets the
        /// safety net refocus through a hold of this page's own.
        ///
        /// Held: HTMLElement and SVGElement focus. NOT held: window.focus (VS Code's webview host focuses the frame
        /// through contentWindow.focus() when the user activates the panel by keyboard, and holding it would eat
        /// that), select() and the Selection API (they change state as well as focus). The safety net covers those.
        /// No ".focus(" and no ".click()" in this text: the session-limit send's self-test forbids both.
        /// </summary>
        private const string FocusHoldPrelude = @"
  function agentflowHoldFocus(d) {
    var w = d && d.defaultView;
    if (!w) return;
    try { if (d.hasFocus()) return; } catch (e) { return; }
    var now = Date.now();
    var h = w.__agentflowFocusHold;
    if (h && h.active) {
      h.until = Math.max(h.until, now + __HOLD_MS__);
      try { w.clearTimeout(h.timer); } catch (e) { }
      h.timer = w.setTimeout(h.release, Math.max(0, h.until - now));
      return;
    }
    h = { active: true, bypass: false, until: now + __HOLD_MS__, saved: [], timer: 0 };
    h.release = function () {
      if (!h.active) return;
      h.active = false;
      for (var si = h.saved.length - 1; si >= 0; si--) {
        var s = h.saved[si];
        try { if (s.owner[s.name] === s.held) s.owner[s.name] = s.original; } catch (e) { }
      }
      if (w.__agentflowFocusHold === h) w.__agentflowFocusHold = null;
    };
    w.__agentflowFocusHold = h;
    try {
      var targets = [[w.HTMLElement, 'focus'], [w.SVGElement, 'focus']];
      for (var hi = 0; hi < targets.length; hi++) {
        var owner = targets[hi][0] && targets[hi][0].prototype, name = targets[hi][1];
        if (!owner || !Object.prototype.hasOwnProperty.call(owner, name)) continue;
        var original = owner[name];
        if (typeof original !== 'function') continue;
        var held = (function (original) {
          return function () {
            if (h.active && !h.bypass && Date.now() < h.until) {
              var focused = false;
              try { focused = d.hasFocus(); } catch (e) { }
              if (!focused) return undefined;
            }
            return original.apply(this, arguments);
          };
        })(original);
        h.saved.push({ owner: owner, name: name, original: original, held: held });
        owner[name] = held;
      }
      h.timer = w.setTimeout(h.release, __HOLD_MS__);
    } catch (e) { h.release(); }
  }
";

        /// <summary>The prelude with its duration filled in. Applied to a template BEFORE any page text is
        /// substituted into it, so a label can never spell the token.</summary>
        private static string WithFocusHoldMs(string template)
        {
            return template.Replace("__HOLD_MS__", FocusHoldMs.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Whether this panel's app document has focus: 'focused', 'blurred' or 'gone'. With
        /// <c>__REMEMBER__</c> true it also keeps the focused element in the page (<c>__agentflowFocusMemo</c>), or
        /// drops a stale one; the element never leaves the page.</summary>
        private const string FocusProbeTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView) return 'gone';
  var d = a.doc, w = d.defaultView;
  var focused = false;
  try { focused = d.hasFocus(); } catch (e) { }
  if (__REMEMBER__) w.__agentflowFocusMemo = focused ? (d.activeElement || null) : null;
  return focused ? 'focused' : 'blurred';
})()";

        /// <summary>Put focus back on the remembered element: 'restored', 'kept' (it never lost focus), 'refused' (the
        /// call ran and focus did not come) or 'gone'. Through a hold of this page's own, if one is active.</summary>
        private const string FocusRestoreExpression = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView) return 'gone';
  var d = a.doc, w = d.defaultView;
  var m = w.__agentflowFocusMemo;
  w.__agentflowFocusMemo = null;
  if (d.hasFocus()) return 'kept';
  var h = w.__agentflowFocusHold;
  if (h) h.bypass = true;
  try {
    if (m && m.isConnected && m !== d.body && m !== d.documentElement && typeof m.focus === 'function') m.focus({ preventScroll: true });
    else w.focus();
  } catch (e) { }
  finally { if (h) h.bypass = false; }
  return d.hasFocus() ? 'restored' : 'refused';
})()";

        internal static string FocusProbeForSelfTest(bool remember) { return FocusProbe(remember); }
        internal static string FocusRestoreForSelfTest { get { return FocusRestoreExpression; } }

        private static string FocusProbe(bool remember)
        {
            return FocusProbeTemplate.Replace("__REMEMBER__", remember ? "true" : "false");
        }

        /// <summary>
        /// The safety net's first half: the panel, other than <paramref name="pressed"/>, whose app document has focus
        /// now, with its focused element remembered in its own page; null when none does (the user is in an editor,
        /// another app, or the pressed card itself). Never throws: a panel that cannot be probed is skipped, and the
        /// press goes ahead without a net.
        /// </summary>
        private static string RememberFocus(CdpSession session, IList<string> panels, string pressed)
        {
            if (panels == null) return null;
            foreach (string panel in panels)
            {
                if (string.IsNullOrEmpty(panel) || string.Equals(panel, pressed, StringComparison.Ordinal)) continue;
                if (string.Equals(EvaluateIn(session, panel, FocusProbe(true)), "focused", StringComparison.Ordinal))
                    return panel;
            }
            return null;
        }

        /// <summary>
        /// The safety net's second half, after a press that clicked while the user was in <paramref name="heldBy"/>:
        /// wait <see cref="FocusCheckAfterMs"/>, and if <paramref name="heldBy"/> lost focus AND the pressed panel has
        /// it, put focus back on the remembered element. Focus anywhere else (the user clicked an editor) is left
        /// alone. The note, for the press's one log line, is null when nothing had to be done.
        /// </summary>
        private static string PutFocusBack(CdpSession session, string heldBy, string pressed)
        {
            try
            {
                Thread.Sleep(FocusCheckAfterMs);
                bool pressedHasIt = string.Equals(EvaluateIn(session, pressed, FocusProbe(false)), "focused", StringComparison.Ordinal);
                bool heldLostIt = string.Equals(EvaluateIn(session, heldBy, FocusProbe(false)), "blurred", StringComparison.Ordinal);
                if (!(pressedHasIt && heldLostIt)) return null;
                string restored = EvaluateIn(session, heldBy, FocusRestoreExpression);
                if (string.Equals(restored, "restored", StringComparison.Ordinal))
                    return "the press took focus from the panel you were in, and it was put back";
                return "the press took focus from the panel you were in, and it could not be put back ("
                       + (string.IsNullOrEmpty(restored) ? "no answer" : restored) + ")";
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
