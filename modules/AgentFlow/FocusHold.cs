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

        /// <summary>The lease's floor (round 5, Codex's review of c7cd714): a guard no sweep has renewed for this long lapses
        /// on its own. A minute, so one slow or skipped sweep does not drop it between sweeps.</summary>
        internal const int FocusGuardLeaseFloorMs = 60000;

        /// <summary>The guard's implementation version (round 6). A guard of any other version, or none (round 4's guard,
        /// still running in a page when this build arrives, had no lease), is removed exactly and replaced, never renewed.</summary>
        internal const int FocusGuardVersion = 6;

        /// <summary>The lease in check intervals: three sweeps' worth, so two missed sweeps in a row still keep it.</summary>
        internal const int FocusGuardLeaseIntervals = 3;

        /// <summary>
        /// The lease for a check interval of <paramref name="checkSeconds"/>: max(<see cref="FocusGuardLeaseFloorMs"/>,
        /// <see cref="FocusGuardLeaseIntervals"/> x the interval). Every sweep renews it on the read it already sends. It
        /// is the cleanup FALLBACK: whatever removal could not do (AgentFlow stopped or crashed, a removal that failed, an
        /// install that landed after Shutdown, a page CDP could not reach) lapses within one lease.
        /// </summary>
        internal static int FocusGuardLeaseMs(int checkSeconds)
        {
            long byInterval = (long)Math.Max(1, checkSeconds) * 1000L * FocusGuardLeaseIntervals;
            return (int)Math.Max(FocusGuardLeaseFloorMs, Math.Min(int.MaxValue, byInterval));
        }

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
        /// THE LEASE (<c>__LEASE_MS__</c>, renewed by every install, which every sweep sends on its read): once
        /// <c>expiresAt</c> has passed, a wrapped call stops suppressing and removes the guard (each call reads the lease, so
        /// this holds even if the page's timers stall), and the guard's one timer removes it too. It stays until
        /// <see cref="FocusGuardRemoveExpression"/> runs, the lease lapses, or the page is replaced (a reloaded page has a
        /// fresh window and is guarded again at the next sweep). One state per realm (<c>w.__agentflowFocusGuard</c>), so a
        /// second install never captures a wrapper as an "original".
        ///
        /// KNOWN LIMIT (Codex's round-4 review): a card could defeat the guard by calling window.focus() and then focusing
        /// an element, since window.focus is deliberately not wrapped (VS Code activates the frame through
        /// contentWindow.focus(), and wrapping it would break switching to a card). No traced Claude Code or Codex path
        /// does this.
        /// </summary>
        private const string FocusGuardInstallTemplate = @"
(function () {" + DocumentPrelude + @"
  var a = appDocument();
  if (!a.doc || !a.doc.defaultView || a.elements < MinElements) return 'unreachable';
  var d = a.doc, w = d.defaultView;
  // One MONOTONIC clock for every duration (round 6): a wall-clock change must not stretch the lease or the allowance.
  var now = function () { return w.performance.now(); };
  var g = w.__agentflowFocusGuard;
  if (g && g.active && g.version === __VERSION__) {
    g.expiresAt = now() + __LEASE_MS__;
    return 'kept';
  }
  // A guard from another build (round 4's had no lease at all) is taken out first, exactly, by its own removal (its
  // saved originals back where its wrappers are still in place, its listeners and timer gone), so nothing of it stays
  // and no active old wrapper is ever captured as an original below.
  if (g && g.active) {
    try { if (typeof g.remove === 'function') g.remove(); } catch (e) { }
    if (w.__agentflowFocusGuard === g) return 'failed';
  }
  g = { active: true, version: __VERSION__, allowUntil: 0, saved: [], expiresAt: now() + __LEASE_MS__, timer: 0 };
  var inputs = ['pointerdown', 'mousedown'];
  var onInput = function (e) {
    if (!e || !e.isTrusted) return;
    g.allowUntil = now() + __GESTURE_MS__;
  };
  g.remove = function () {
    if (!g.active) return;
    g.active = false;
    try { w.clearTimeout(g.timer); } catch (e) { }
    for (var ri = 0; ri < inputs.length; ri++) { try { w.removeEventListener(inputs[ri], onInput, true); } catch (e) { } }
    for (var si = g.saved.length - 1; si >= 0; si--) {
      var s = g.saved[si];
      try { if (s.owner[s.name] === s.held) Object.defineProperty(s.owner, s.name, s.descriptor); } catch (e) { }
    }
    if (w.__agentflowFocusGuard === g) w.__agentflowFocusGuard = null;
  };
  // The guard's one timer: at the lease's end it removes the guard, or waits out a renewal.
  var lapse = function () {
    if (!g.active) return;
    var left = g.expiresAt - now();
    if (left <= 0) { g.remove(); return; }
    g.timer = w.setTimeout(lapse, left);
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
          var t = now();
          if (g.active && t >= g.expiresAt) g.remove();
          else if (g.active && t >= g.allowUntil) {
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
    g.timer = w.setTimeout(lapse, __LEASE_MS__);
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

        /// <summary>The round-4 guard installer, verbatim from a299c3c (round 6 migration test): its guard has no
        /// version, no lease and no timer, and is what the owner's app runs when this build arrives.</summary>
        internal static string LegacyRound4GuardInstall
        {
            get
            {
                return ("\n(function () {" + DocumentPreludeForSelfTest + @"
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
})()")
                    .Replace("__GESTURE_MS__", "1000");
            }
        }

        internal static string DocumentPreludeForSelfTest { get { return DocumentPrelude; } }

        /// <summary>The install with its numbers filled in, for a lease of <paramref name="leaseMs"/>.</summary>
        internal static string FocusGuardInstall(int leaseMs)
        {
            return FocusGuardInstallTemplate
                .Replace("__VERSION__", FocusGuardVersion.ToString(CultureInfo.InvariantCulture))
                .Replace("__GESTURE_MS__", FocusGestureMs.ToString(CultureInfo.InvariantCulture))
                .Replace("__LEASE_MS__", leaseMs.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The guarded read's frame (round 5): the install rides on the read the sweep already sends, so
        /// renewing the guard costs no extra round trip. It answers <c>outcome|read</c>; an install that throws cannot
        /// take the read with it.</summary>
        internal const string GuardedReadPrefix = "(function () {\n  var agentflowGuard;\n  try { agentflowGuard = ";
        internal const string GuardedReadMiddle = "; } catch (e) { agentflowGuard = 'failed'; }\n  return agentflowGuard + '|' + ";
        internal const string GuardedReadSuffix = ";\n})()";

        /// <summary>A read with the guard's install (or renewal) in front of it.</summary>
        internal static string GuardedRead(string readExpression, FocusGuardTicket guard)
        {
            return GuardedReadPrefix + FocusGuardInstall(guard.LeaseMs) + GuardedReadMiddle + readExpression + GuardedReadSuffix;
        }

        /// <summary>Split a guarded read's answer into the install's outcome and the read's own answer. 'failed' is recorded
        /// as a failed install; no answer at all only as unconfirmed (round 6: a missing acknowledgement does not prove the
        /// install failed); 'unreachable' (no conversation yet) is neither.</summary>
        internal static string SplitGuardedRead(string raw, string targetId, string agent, FocusGuardTicket guard)
        {
            string outcome = null;
            int bar = raw == null ? -1 : raw.IndexOf('|');
            if (bar >= 0) { outcome = raw.Substring(0, bar); raw = raw.Substring(bar + 1); }
            if (outcome == "installed" || outcome == "kept") guard.Installed.Add(targetId);
            else if (outcome == "failed") guard.Failed.Add(new KeyValuePair<string, string>(targetId, agent));
            else if (outcome != "unreachable") guard.Unconfirmed.Add(new KeyValuePair<string, string>(targetId, agent));
            return raw;
        }
        internal static string FocusGuardRemoveForSelfTest { get { return FocusGuardRemoveExpression; } }

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

    /// <summary>
    /// One sweep's standing focus guard (1.12.3): the lease to stamp on every panel's read, and what the sweep found: the
    /// panels guarded, and the ones where the install failed (agent beside each), for the degraded log line. Null on a
    /// sweep means the guard is off.
    /// </summary>
    internal sealed class FocusGuardTicket
    {
        public int LeaseMs = CdpApprover.FocusGuardLeaseFloorMs;
        public readonly List<string> Installed = new List<string>();
        public readonly List<KeyValuePair<string, string>> Failed = new List<KeyValuePair<string, string>>();
        /// <summary>Panels whose protection could not be confirmed (round 6): the attach was refused, the read threw, or no
        /// answer came. Not proof of a failed install.</summary>
        public readonly List<KeyValuePair<string, string>> Unconfirmed = new List<KeyValuePair<string, string>>();
    }
}
