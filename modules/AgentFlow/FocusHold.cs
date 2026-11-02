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
    // Two defences, both here: a hold in the pressed page while its card settles, and a net that puts the user back in
    // the panel they were in if focus left it anyway.
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

        /// <summary>How long after a press the safety net looks at where focus went. Claude's plain path refocuses 100 ms
        /// after the card clears, and the next Codex card mounted 26 to 240 ms after a press (measured 2026-10-21, see
        /// <see cref="QueueQuietMs"/>), so this covers both with margin. Paid only when another agent panel had focus at
        /// the press. Deferred paths past it (the diff path) are the hold's to cover; the net is secondary.</summary>
        internal const int FocusCheckAfterMs = 800;

        /// <summary>How long the user's panel watches for the user's own navigation after it is remembered, at most: well
        /// past the net's look, so the watch is always there when the net asks, and gone soon after.</summary>
        internal const int FocusMemoMs = FocusCheckAfterMs * 4;

        /// <summary>
        /// THE HOLD, in the pressed page, called immediately before the act (after every guard: card id, label
        /// re-check, disabled). Returns the hold's release when THIS call created it, so the caller can release it at
        /// once if the act throws; null otherwise.
        ///
        /// ALWAYS INSTALLED, decided per call. In THAT document's realm (<c>d.defaultView</c>: the nested iframe the card
        /// lives in, never the webview shell, whose patch would not reach the iframe's own methods) the focus primitives
        /// are replaced by wrappers that ask, at every call, whether the document has focus: if it does, the call goes
        /// to the original (the user is in this card, and the card's own focus is theirs); if it does not, the call does
        /// nothing while the hold lasts. So a press made while the user is in the card still protects them if they leave
        /// before the card settles. Held: HTMLElement and SVGElement focus, and HTMLInputElement and HTMLTextAreaElement
        /// select, which move DOM focus without going through HTMLElement.focus (Codex's Chromium probe, 2026-11-02).
        ///
        /// SCOPED TO THE CARD, NOT TO A CLOCK. No finite window after a click covers the deferred paths, so the hold
        /// stays while <paramref name="busy"/> says a card is still on the page, and lets go <see cref="FocusSettleMs"/>
        /// after it last did, polled every <see cref="FocusPollMs"/>; <see cref="FocusHoldCapMs"/> from the latest press
        /// bounds it whatever the card does. The user's input does NOT end it: typing in the card and then leaving must
        /// still leave the later background focus suppressed.
        ///
        /// THE ACT'S OWN OBSERVATION, for the safety net: <c>w.__agentflowAction</c>, on the acted document's own window
        /// (so another document, or the same frame after a navigation, has none), records this action's id
        /// (<paramref name="action"/>) and whether the document lacked focus AT the act. TRUSTED
        /// pointerdown, mousedown, keydown or beforeinput in that window (capture phase, listening only: no
        /// preventDefault, no propagation change, no key action) marks the current action's <c>intent</c>, which cancels
        /// the net. A focus event's isTrusted is never used: native focus events follow programmatic focus too.
        ///
        /// RESTORED EXACTLY: each original property descriptor is put back on the object that owned it, and only where
        /// the wrapper is still the current value (a wrapper someone else wrapped over stays, and is transparent once
        /// <c>h.active</c> is false); every listener is removed. One state per realm (<c>w.__agentflowFocusHold</c>): a
        /// press while a hold is active only refreshes it (its card test, its cap, its quiet clock), and never captures,
        /// so a wrapper can never be recorded as an "original". <c>bypass</c> lets the safety net refocus through a hold
        /// of this page's own.
        ///
        /// NOT held: window.focus (VS Code's webview host focuses the frame through contentWindow.focus() when the user
        /// activates the panel by keyboard, and holding it would eat that), the Selection API (no traced path reaches
        /// it). No ".focus(" and no ".click()" in this text: the session-limit send's self-test forbids both.
        /// </summary>
        private const string FocusHoldPrelude = @"
  function agentflowHoldFocus(d, busy, action) {
    var w = d && d.defaultView;
    if (!w) return null;
    var focusedAtAct = false;
    try { focusedAtAct = d.hasFocus(); } catch (e) { }
    w.__agentflowAction = { id: action || '', background: !focusedAtAct, intent: false };
    var now = Date.now();
    var h = w.__agentflowFocusHold;
    if (h && h.active) {
      h.busy = busy;
      h.capAt = now + __HOLD_CAP_MS__;
      h.quietSince = 0;
      return null;
    }
    h = { active: true, bypass: false, busy: busy, capAt: now + __HOLD_CAP_MS__, quietSince: 0, saved: [], timer: 0 };
    var inputs = ['pointerdown', 'mousedown', 'keydown', 'beforeinput'];
    var onInput = function (e) {
      if (!e || !e.isTrusted) return;
      var act = w.__agentflowAction;
      if (act) act.intent = true;
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
            if (h.active && !h.bypass && Date.now() < h.capAt) {
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
                .Replace("__MEMO_MS__", FocusMemoMs.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>A fresh id for one press, so the safety net's every step can ask "this action?" (1.12.3).</summary>
        private static string NewFocusAction()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        /// <summary>
        /// Remember the user's panel for action <c>__ACTION__</c>: 'focused' when its app document has focus, with the
        /// DEEP focused element (through open shadow roots) kept on that document's own window
        /// (<c>__agentflowFocusMemo</c>, never a body or root) and a listening-only watch for the user's own navigation
        /// (a trusted pointerdown, or a trusted keydown that could be a command: Ctrl, Alt or Meta held, Tab, Escape, a
        /// function key), which marks the memo so the net declines. Plain typing is not navigation. 'blurred' or 'gone'
        /// otherwise, dropping a stale memo. The watch removes itself after <see cref="FocusMemoMs"/>.
        /// </summary>
        private const string FocusRememberTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView) return 'gone';
  var d = a.doc, w = d.defaultView;
  var old = w.__agentflowFocusMemo;
  if (old && old.dispose) old.dispose();
  w.__agentflowFocusMemo = null;
  var focused = false;
  try { focused = d.hasFocus(); } catch (e) { }
  if (!focused) return 'blurred';
  var el = d.activeElement;
  while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
  if (el && el !== d.body && el !== d.documentElement) {
    var m = { el: el, action: __ACTION__, intent: false, timer: 0 };
    var watch = function (e) {
      if (!e || !e.isTrusted) return;
      if (e.type === 'pointerdown' || e.ctrlKey || e.altKey || e.metaKey
          || e.key === 'Tab' || e.key === 'Escape' || /^F[0-9]+$/.test(e.key || '')) m.intent = true;
    };
    m.dispose = function () {
      try { w.removeEventListener('pointerdown', watch, true); } catch (e) { }
      try { w.removeEventListener('keydown', watch, true); } catch (e) { }
      try { w.clearTimeout(m.timer); } catch (e) { }
      if (w.__agentflowFocusMemo === m) w.__agentflowFocusMemo = null;
    };
    w.addEventListener('pointerdown', watch, true);
    w.addEventListener('keydown', watch, true);
    m.timer = w.setTimeout(m.dispose, __MEMO_MS__);
    w.__agentflowFocusMemo = m;
  }
  return 'focused';
})()";

        /// <summary>
        /// The pressed panel, after the press, for action <c>__ACTION__</c>: 'stolen' only when THIS action's record is
        /// on this document's window, says the document lacked focus AT the act, records no trusted input since, and the
        /// document has focus now. 'other' (another action's record, or none here), 'was-focused' (the user was already in
        /// it at the act), 'user' (trusted input since the act) or 'blurred' otherwise. Only 'stolen' lets the net act.
        /// </summary>
        private const string FocusActionProbeTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView) return 'gone';
  var d = a.doc, w = d.defaultView;
  var act = w.__agentflowAction;
  if (!act || act.id !== __ACTION__) return 'other';
  if (!act.background) return 'was-focused';
  if (act.intent) return 'user';
  var focused = false;
  try { focused = d.hasFocus(); } catch (e) { }
  return focused ? 'stolen' : 'blurred';
})()";

        /// <summary>Put focus back on the element remembered for action <c>__ACTION__</c>: 'restored', 'kept' (the panel
        /// never lost focus), 'user' (the user navigated from it since), 'lost' (no memo for this action on this
        /// document's window, or the element gone), 'refused' (the call ran and focus did not come) or 'gone'. Only the same live
        /// document and the same still-connected element: never a body, a root or another panel. Through a hold of this
        /// page's own, if one is active. The watch is removed whatever the answer.</summary>
        private const string FocusRestoreTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView) return 'gone';
  var d = a.doc, w = d.defaultView;
  var m = w.__agentflowFocusMemo;
  if (m && m.dispose) m.dispose();
  w.__agentflowFocusMemo = null;
  if (!m || m.action !== __ACTION__) return 'lost';
  if (m.intent) return 'user';
  if (d.hasFocus()) return 'kept';
  if (!m.el || !m.el.isConnected || typeof m.el.focus !== 'function') return 'lost';
  var h = w.__agentflowFocusHold;
  if (h) h.bypass = true;
  try { m.el.focus({ preventScroll: true }); } catch (e) { }
  finally { if (h) h.bypass = false; }
  return d.hasFocus() ? 'restored' : 'refused';
})()";

        /// <summary>Drop the memo and its watch for action <c>__ACTION__</c> when the net declines: 'forgot' or 'gone'. A memo
        /// of any other action is left to its own press.</summary>
        private const string FocusForgetTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView) return 'gone';
  var m = a.doc.defaultView.__agentflowFocusMemo;
  if (m && m.action === __ACTION__ && m.dispose) m.dispose();
  return 'forgot';
})()";

        private static string ForAction(string template, string action)
        {
            return WithFocusHoldMs(template).Replace("__ACTION__", JsonEncode(action ?? ""));
        }

        internal static string FocusRememberForSelfTest(string action) { return ForAction(FocusRememberTemplate, action); }
        internal static string FocusActionProbeForSelfTest(string action) { return ForAction(FocusActionProbeTemplate, action); }
        internal static string FocusRestoreForSelfTest(string action) { return ForAction(FocusRestoreTemplate, action); }

        /// <summary>
        /// The safety net's first half: the panel, other than <paramref name="pressed"/>, whose app document has focus
        /// now, remembered for <paramref name="action"/> in its own page; null when none does (the user is in an editor,
        /// another app, or the pressed card itself). Never throws: a panel that cannot be probed is skipped, and the press
        /// goes ahead without a net.
        /// </summary>
        private static string RememberFocus(CdpSession session, IList<string> panels, string pressed, string action)
        {
            if (panels == null) return null;
            foreach (string panel in panels)
            {
                if (string.IsNullOrEmpty(panel) || string.Equals(panel, pressed, StringComparison.Ordinal)) continue;
                if (string.Equals(EvaluateIn(session, panel, ForAction(FocusRememberTemplate, action)), "focused",
                                  StringComparison.Ordinal))
                    return panel;
            }
            return null;
        }

        /// <summary>
        /// The safety net's second half, after a press that clicked while the user was in <paramref name="heldBy"/>:
        /// wait <see cref="FocusCheckAfterMs"/>, then act only on 'stolen' from the pressed panel (this action, which
        /// found it WITHOUT focus at the act, no trusted input there since, and it has focus now), and even then the
        /// restore declines if the user navigated from <paramref name="heldBy"/> since it was remembered. Anything
        /// ambiguous is declined, never guessed. NOT atomic: the look and the restore are two calls to two pages, so a
        /// user who picks an editor in the milliseconds between them is not seen. Secondary by nature: it cannot take
        /// back a key already typed into the wrong card, and once the user types into the pressed card (even by mistake,
        /// after a steal) it declines; the hold is the defence. The note, for the press's one log line, is null when
        /// nothing had to be done or the net declined.
        /// </summary>
        private static string PutFocusBack(CdpSession session, string heldBy, string pressed, string action)
        {
            try
            {
                Thread.Sleep(FocusCheckAfterMs);
                if (!string.Equals(EvaluateIn(session, pressed, ForAction(FocusActionProbeTemplate, action)), "stolen",
                                   StringComparison.Ordinal))
                {
                    // Declined: take the watch out of the user's panel now rather than at its own timer.
                    EvaluateIn(session, heldBy, ForAction(FocusForgetTemplate, action));
                    return null;
                }
                string restored = EvaluateIn(session, heldBy, ForAction(FocusRestoreTemplate, action));
                if (string.Equals(restored, "kept", StringComparison.Ordinal)
                    || string.Equals(restored, "user", StringComparison.Ordinal)) return null;
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
