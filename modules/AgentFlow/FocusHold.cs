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
        /// The hard cleanup cap, from the latest press: the hold is released at this point whatever the card is doing,
        /// so a wrapper can never outlive it. Claude's diff path races its acceptance against 5000 ms and then schedules
        /// a further 5000 ms fallback before the card clears (index.js byte 5576136), so a refocus can come about ten
        /// seconds after the press. This bounds the patch; it is NOT a claim that every deferred focus lands inside it.
        /// </summary>
        internal const int FocusHoldCapMs = 15000;

        /// <summary>How often the hold looks at whether its card has settled, in the page's own timers.</summary>
        internal const int FocusPollMs = 250;

        /// <summary>How long after a press the safety net looks at where focus went. Claude's plain path refocuses 100 ms
        /// after the card clears, and the next Codex card mounted 26 to 240 ms after a press (measured 2026-10-21, see
        /// <see cref="QueueQuietMs"/>), so this covers both with margin. Paid only when another agent panel had focus at
        /// the press. Deferred paths past it (the diff path) are the hold's to cover; the net is secondary.</summary>
        internal const int FocusCheckAfterMs = 800;

        /// <summary>
        /// THE HOLD, in the pressed page, called immediately before the act (after every guard: card id, label
        /// re-check, disabled). Returns the hold's release when THIS call created it, so the caller can release it at
        /// once if the act throws; null otherwise.
        ///
        /// If the app document HAS focus the user is in this card and nothing is touched. If it does not, the focus
        /// primitives of THAT document's realm (<c>d.defaultView</c>: the nested iframe the card lives in, never the
        /// webview shell, whose patch would not reach the iframe's own methods) are replaced by wrappers: a call while
        /// the hold is active and the document still lacks focus does nothing; any other call goes to the original.
        /// Held: HTMLElement and SVGElement focus, and HTMLInputElement and HTMLTextAreaElement select, which move DOM
        /// focus without going through HTMLElement.focus (Codex's Chromium probe, 2026-11-02).
        ///
        /// SCOPED TO THE CARD, NOT TO A CLOCK. No finite window after a click covers the deferred paths, so the hold
        /// stays while <paramref name="busy"/> says a card is still on the page, and lets go <see cref="FocusSettleMs"/>
        /// after it last did, polled every <see cref="FocusPollMs"/>. <see cref="FocusHoldCapMs"/> from the latest press
        /// releases it whatever the card does. The user's own intent ends it at once: a TRUSTED pointerdown, mousedown
        /// or beforeinput in that window (capture phase), which also marks the page so the safety net leaves the user
        /// where they chose to be. A focus event's isTrusted is not used: native focus events follow programmatic focus
        /// too. Not keydown: this text goes into the Codex panel, and nothing sent there may name a key event (Escape
        /// there is Deny; SelfCheckCodexTransport bans the word). beforeinput is the user typing; a user who moves in by
        /// keyboard alone and has not typed yet is not seen as intent.
        ///
        /// RESTORED EXACTLY: each original property descriptor is put back on the object that owned it, and only where
        /// the wrapper is still the current value (a wrapper someone else wrapped over stays, and is transparent once
        /// <c>h.active</c> is false). One state per realm (<c>w.__agentflowFocusHold</c>): a press while a hold is active
        /// only refreshes it (its card test, its cap, its quiet clock), and never captures, so a wrapper can never be
        /// recorded as an "original". <c>bypass</c> lets the safety net refocus through a hold of this page's own.
        ///
        /// NOT held: window.focus (VS Code's webview host focuses the frame through contentWindow.focus() when the
        /// user activates the panel by keyboard, and holding it would eat that), the Selection API (no traced path
        /// reaches it). No ".focus(" and no ".click()" in this text: the session-limit send's self-test forbids both.
        /// </summary>
        private const string FocusHoldPrelude = @"
  function agentflowHoldFocus(d, busy) {
    var w = d && d.defaultView;
    if (!w) return null;
    try { if (d.hasFocus()) return null; } catch (e) { return null; }
    var now = Date.now();
    var h = w.__agentflowFocusHold;
    w.__agentflowUserInput = false;
    if (h && h.active) {
      h.busy = busy;
      h.capAt = now + __HOLD_CAP_MS__;
      h.quietSince = 0;
      return null;
    }
    h = { active: true, bypass: false, busy: busy, capAt: now + __HOLD_CAP_MS__, quietSince: 0, saved: [], timer: 0 };
    var inputs = ['pointerdown', 'mousedown', 'beforeinput'];
    var onInput = function (e) {
      if (!e || !e.isTrusted) return;
      w.__agentflowUserInput = true;
      h.release();
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
                .Replace("__POLL_MS__", FocusPollMs.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Whether this panel's app document has focus: 'focused', 'blurred', 'user' (it has focus and the user gave
        /// it trusted input since the last press there) or 'gone'. With <c>__REMEMBER__</c> true it also keeps the
        /// DEEP focused element (through open shadow roots) and its document in the page (<c>__agentflowFocusMemo</c>),
        /// or drops a stale one; nothing about the element leaves the page. A body or root is never remembered.
        /// </summary>
        private const string FocusProbeTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView) return 'gone';
  var d = a.doc, w = d.defaultView;
  var focused = false;
  try { focused = d.hasFocus(); } catch (e) { }
  if (__REMEMBER__) {
    var el = focused ? d.activeElement : null;
    while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
    w.__agentflowFocusMemo = el && el !== d.body && el !== d.documentElement ? { el: el, doc: d } : null;
  }
  if (focused && !__REMEMBER__ && w.__agentflowUserInput) return 'user';
  return focused ? 'focused' : 'blurred';
})()";

        /// <summary>Put focus back on the remembered element: 'restored', 'kept' (the panel never lost focus), 'lost'
        /// (no element remembered, a different document now, or the element gone), 'refused' (the call ran and focus did
        /// not come) or 'gone'. Only the same live document and the same still-connected element: never a body, a root
        /// or another panel. Through a hold of this page's own, if one is active.</summary>
        private const string FocusRestoreExpression = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView) return 'gone';
  var d = a.doc, w = d.defaultView;
  var m = w.__agentflowFocusMemo;
  w.__agentflowFocusMemo = null;
  if (d.hasFocus()) return 'kept';
  if (!m || m.doc !== d || !m.el || !m.el.isConnected || typeof m.el.focus !== 'function') return 'lost';
  var h = w.__agentflowFocusHold;
  if (h) h.bypass = true;
  try { m.el.focus({ preventScroll: true }); } catch (e) { }
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
        /// wait <see cref="FocusCheckAfterMs"/>, and only if the PRESSED panel now has focus with no trusted input from
        /// the user there ('focused', not 'user'), put focus back on the remembered element of <paramref name="heldBy"/>
        /// (which itself refuses unless that panel lost focus and the element is still there). Focus anywhere else (the
        /// user clicked an editor) or a user who chose the pressed card is left alone. Secondary by nature: it cannot
        /// take back a key already typed into the wrong card, which is the hold's job. The note, for the press's one
        /// log line, is null when nothing had to be done.
        /// </summary>
        private static string PutFocusBack(CdpSession session, string heldBy, string pressed)
        {
            try
            {
                Thread.Sleep(FocusCheckAfterMs);
                if (!string.Equals(EvaluateIn(session, pressed, FocusProbe(false)), "focused", StringComparison.Ordinal))
                    return null;
                string restored = EvaluateIn(session, heldBy, FocusRestoreExpression);
                if (string.Equals(restored, "kept", StringComparison.Ordinal)) return null;
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
