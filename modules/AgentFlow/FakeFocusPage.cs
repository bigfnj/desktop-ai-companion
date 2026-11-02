using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// A page of agent webviews for the focus checks (1.12.3): the module's REAL expressions, run in a real JS engine
    /// (Windows' own chakra.dll, through its JsRT API) against a small model of the DOM, behind
    /// <see cref="FakeCdpServer.Target.Evaluator"/>.
    ///
    /// WHY A JS ENGINE. What the focus work promises is JS behaviour: a prototype method wrapped and put back as the
    /// same function object, a timer that outlasts a card's own setTimeout, a second press that must not capture the
    /// wrapper. FakeCdpServer answers by matching expression text, which can only restate what the test expects.
    ///
    /// WHAT IT MODELS. Each frame is one CDP target: an outer webview document holding an iframe whose document is the
    /// card's app, each window its own realm (its own HTMLElement constructor and prototype, as in a browser). One page
    /// focus, held by at most one window (null = focus outside every agent panel, an editor say). A focus() call takes
    /// the page's focus for that window, which is the steal as the owner sees it: VS Code activates the webview that
    /// took DOM focus. <c>document.hasFocus()</c> is true for the focused window and its ancestors. setTimeout and Date
    /// run on a clock that follows real time, and due timers run in due order (with the clock AT their due time) before
    /// every evaluation and on <see cref="Pump"/>. A card's Yes button runs a handler that approves and then steals
    /// focus as told: synchronously, in a 50 ms timeout, or through <c>reveal</c>, a path no in-page hold can reach
    /// (an extension calling reveal() on its view). The Claude and Codex card shapes the click templates look for, and
    /// the chat box the session-limit send types into.
    ///
    /// WHAT IT DOES NOT MODEL. Real Chromium focus rules (user activation, cross-origin frame restrictions), focus and
    /// blur events, VS Code's webview host and its did-focus message, microtasks, requestAnimationFrame, the Selection
    /// API, and the READ expressions (cloneNode and friends are absent): reads stay canned, as in every other
    /// FakeCdpServer test. Selectors: a tag, `*`, and one [attr="v"] or [attr*="v"], which is all the press, probe and
    /// restore expressions use.
    /// </summary>
    internal sealed class FakeFocusPage : IDisposable
    {
        [DllImport("chakra.dll")] private static extern int JsCreateRuntime(int attributes, IntPtr threadService, out IntPtr runtime);
        [DllImport("chakra.dll")] private static extern int JsCreateContext(IntPtr runtime, out IntPtr context);
        [DllImport("chakra.dll")] private static extern int JsSetCurrentContext(IntPtr context);
        [DllImport("chakra.dll", CharSet = CharSet.Unicode)]
        private static extern int JsRunScript(string script, UIntPtr sourceContext, string sourceUrl, out IntPtr result);
        [DllImport("chakra.dll")] private static extern int JsConvertValueToString(IntPtr value, out IntPtr text);
        [DllImport("chakra.dll")] private static extern int JsStringToPointer(IntPtr value, out IntPtr chars, out UIntPtr length);
        [DllImport("chakra.dll")] private static extern int JsGetAndClearException(out IntPtr exception);
        [DllImport("chakra.dll")] private static extern int JsDisposeRuntime(IntPtr runtime);

        private readonly object _lock = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private IntPtr _runtime, _context;
        private uint _cookie;

        /// <summary>The last script error, for a check's message. Never page text from a real editor: this page is
        /// the test's own.</summary>
        internal string LastError { get; private set; }

        private FakeFocusPage() { }

        /// <summary>A page, or null with <paramref name="why"/> when the engine is not available on this machine.</summary>
        internal static FakeFocusPage TryCreate(out string why)
        {
            why = null;
            var page = new FakeFocusPage();
            try
            {
                int error = JsCreateRuntime(0, IntPtr.Zero, out page._runtime);
                if (error != 0) { why = "JsCreateRuntime returned " + error; return null; }
                error = JsCreateContext(page._runtime, out page._context);
                if (error != 0) { why = "JsCreateContext returned " + error; page.Dispose(); return null; }
            }
            catch (Exception exception) when (exception is DllNotFoundException || exception is EntryPointNotFoundException)
            {
                why = "chakra.dll JsRT is not available (" + exception.GetType().Name + ")";
                return null;
            }
            if (page.Run(Model) == null) { why = "the DOM model did not load: " + page.LastError; page.Dispose(); return null; }
            return page;
        }

        /// <summary>Run a script of the test's own in the page and return its value as a string, or null when it threw.</summary>
        internal string Run(string script)
        {
            lock (_lock)
            {
                if (_runtime == IntPtr.Zero) return null;
                JsSetCurrentContext(_context);
                try
                {
                    IntPtr result;
                    int error = JsRunScript(script, new UIntPtr(++_cookie), "", out result);
                    if (error != 0)
                    {
                        IntPtr exception;
                        LastError = "script error " + error;
                        if (JsGetAndClearException(out exception) == 0)
                        {
                            string said = AsString(exception);
                            if (said != null) LastError += ": " + said;
                        }
                        return null;
                    }
                    return AsString(result);
                }
                finally { JsSetCurrentContext(IntPtr.Zero); }
            }
        }

        private static string AsString(IntPtr value)
        {
            IntPtr text, chars;
            UIntPtr length;
            if (JsConvertValueToString(value, out text) != 0) return null;
            if (JsStringToPointer(text, out chars, out length) != 0) return null;
            return Marshal.PtrToStringUni(chars, (int)length.ToUInt32());
        }

        private long Now { get { return _clock.ElapsedMilliseconds; } }

        /// <summary>Run due timers up to now.</summary>
        internal void Pump() { Run("__page.pump(" + Now + "); 'ok'"); }

        /// <summary>
        /// Evaluate a module expression AS frame <paramref name="frame"/>'s target, the way CDP's Runtime.evaluate
        /// does: its free identifiers (document, window, setTimeout, Date, InputEvent, KeyboardEvent) are that outer
        /// window's. Due timers run first. Null when the expression threw or returned something other than a string,
        /// which is what CdpSession.Evaluate reads as no answer.
        /// </summary>
        internal string EvaluateAs(string frame, string expression)
        {
            string answer = Run("(function () { var __w = __page.frames['" + frame + "'].outer; __page.pump(" + Now + ");\n"
                                + "  with (__w) { var __r = (" + expression + "\n); }\n"
                                + "  return typeof __r === 'string' ? 's' + __r : 'u'; })()");
            if (answer == null || !answer.StartsWith("s", StringComparison.Ordinal)) return null;
            return answer.Substring(1);
        }

        /// <summary>A frame named <paramref name="name"/>: shape 'claude', 'codex' or 'chat' (no card, only the chat
        /// box), and what its Yes button (or, for 'chat', its Enter key) does after approving: any of 'sync',
        /// 'timeout', 'reveal', 'throw' (the handler throws) or 'none'.</summary>
        internal void AddFrame(string name, string shape, string steal)
        {
            Run("__page.frame('" + name + "', '" + shape + "', '" + steal + "'); 'ok'");
        }

        /// <summary>Put the user in <paramref name="frame"/>'s chat box (or nowhere for null), and clear the focus log.</summary>
        internal void UserIn(string frame)
        {
            Run((frame == null ? "__page.focused = null;" : "__page.user('" + frame + "');") + " __page.focusLog = []; 'ok'");
        }

        /// <summary>The whole page's state as JSON: see __page.state in <see cref="Model"/>.</summary>
        internal string State() { return Run("__page.state()"); }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_runtime == IntPtr.Zero) return;
                JsSetCurrentContext(IntPtr.Zero);
                JsDisposeRuntime(_runtime);
                _runtime = IntPtr.Zero;
            }
        }

        /// <summary>The DOM model. ES5, single quotes only, no backslashes.</summary>
        private const string Model = @"
var __page = (function () {
  var page = { now: 0, timers: [], seq: 0, focused: null, frames: {}, approved: {}, sent: {}, focusLog: [] };
  page.pump = function (now) {
    for (;;) {
      var next = null, at = -1;
      for (var i = 0; i < page.timers.length; i++) {
        var t = page.timers[i];
        if (t.due > now) continue;
        if (next === null || t.due < next.due || (t.due === next.due && t.seq < next.seq)) { next = t; at = i; }
      }
      if (next === null) break;
      page.timers.splice(at, 1);
      if (next.due > page.now) page.now = next.due;
      try { next.fn(); } catch (e) { page.focusLog.push('timer-threw'); }
    }
    if (now > page.now) page.now = now;
  };
  function setTimeoutModel(fn, ms) {
    page.seq++;
    page.timers.push({ id: page.seq, seq: page.seq, due: page.now + Math.max(0, ms || 0), fn: fn });
    return page.seq;
  }
  function clearTimeoutModel(id) {
    for (var i = 0; i < page.timers.length; i++) if (page.timers[i].id === id) { page.timers.splice(i, 1); return; }
  }
  var clock = { now: function () { return page.now; } };
  page.focusTo = function (win, el) {
    page.focused = win;
    win.document.activeElement = el || win.document.body;
    page.focusLog.push(win.name_);
  };
  function parse(sel) {
    var p = { tag: sel, attr: null, op: '', val: '' };
    var lb = sel.indexOf('[');
    if (lb >= 0) {
      p.tag = sel.substring(0, lb);
      var body = sel.substring(lb + 1, sel.length - 1), eq = body.indexOf('=');
      p.attr = body.substring(0, eq);
      if (p.attr.charAt(p.attr.length - 1) === '*') { p.op = '*'; p.attr = p.attr.substring(0, p.attr.length - 1); }
      p.val = body.substring(eq + 2, body.length - 1);
    }
    p.tag = p.tag === '*' ? '' : p.tag.toUpperCase();
    return p;
  }
  function matches(e, p) {
    if (p.tag && e.tagName !== p.tag) return false;
    if (p.attr === null) return true;
    var v = e.attrs[p.attr];
    if (typeof v !== 'string') return false;
    return p.op === '*' ? v.indexOf(p.val) >= 0 : v === p.val;
  }
  function select(root, sel, includeRoot) {
    var p = parse(sel), out = [];
    function walk(e) { for (var i = 0; i < e.children.length; i++) { if (matches(e.children[i], p)) out.push(e.children[i]); walk(e.children[i]); } }
    if (root) { if (includeRoot && matches(root, p)) out.push(root); walk(root); }
    return out;
  }
  function realm(name, parentWin) {
    var win = { name_: name, parent_: parentWin || null };
    function HTMLElement() { }
    HTMLElement.prototype.focus = function (options) { page.focusTo(win, this); };
    HTMLElement.prototype.click = function () {
      var hs = this.handlers.click || [];
      for (var i = 0; i < hs.length; i++) hs[i].call(this, { type: 'click' });
    };
    HTMLElement.prototype.dispatchEvent = function (ev) {
      var hs = this.handlers[ev.type] || [];
      for (var i = 0; i < hs.length; i++) hs[i].call(this, ev);
      return true;
    };
    HTMLElement.prototype.appendChild = function (c) { c.parentNode = this; this.children.push(c); return c; };
    HTMLElement.prototype.querySelectorAll = function (sel) { return select(this, sel, false); };
    HTMLElement.prototype.querySelector = function (sel) { var r = select(this, sel, false); return r.length ? r[0] : null; };
    Object.defineProperty(HTMLElement.prototype, 'textContent', {
      get: function () { var s = this.ownText; for (var i = 0; i < this.children.length; i++) s += this.children[i].textContent; return s; },
      set: function (v) { this.ownText = String(v); this.children = []; }
    });
    Object.defineProperty(HTMLElement.prototype, 'innerText', { get: function () { return this.textContent; } });
    Object.defineProperty(HTMLElement.prototype, 'isConnected', {
      get: function () { for (var x = this; x; x = x.parentNode) if (x === win.document.documentElement) return true; return false; }
    });
    function SVGElement() { }
    SVGElement.prototype.focus = function (options) { page.focusTo(win, this); };
    function InputEvent(type, init) { this.type = type; for (var k in init) this[k] = init[k]; }
    function KeyboardEvent(type, init) { this.type = type; for (var k in init) this[k] = init[k]; }
    win.HTMLElement = HTMLElement; win.SVGElement = SVGElement;
    win.InputEvent = InputEvent; win.KeyboardEvent = KeyboardEvent;
    win.setTimeout = setTimeoutModel; win.clearTimeout = clearTimeoutModel; win.Date = clock;
    win.focus = function () { page.focusTo(win, null); };
    win.window = win;
    var doc = { defaultView: win, activeElement: null, body: null, documentElement: null };
    doc.hasFocus = function () { for (var x = page.focused; x; x = x.parent_) if (x === win) return true; return false; };
    doc.querySelectorAll = function (sel) { return select(doc.documentElement, sel, true); };
    doc.querySelector = function (sel) { var r = doc.querySelectorAll(sel); return r.length ? r[0] : null; };
    win.document = doc;
    win.make = function (tag, attrs, text) {
      var e = Object.create(HTMLElement.prototype);
      e.tagName = tag.toUpperCase(); e.attrs = attrs || {}; e.children = []; e.parentNode = null;
      e.ownText = text || ''; e.handlers = {}; e.disabled = false; e.ownerDocument = doc;
      return e;
    };
    doc.documentElement = win.make('html'); doc.body = win.make('body');
    doc.documentElement.appendChild(doc.body);
    return win;
  }
  page.frame = function (name, shape, steal) {
    var outer = realm(name + '/shell', null), inner = realm(name, outer);
    var iframe = outer.make('iframe');
    iframe.contentDocument = inner.document;
    outer.document.body.appendChild(iframe);
    var input = inner.make('div', { 'aria-label': 'Message input', contenteditable: 'true' });
    inner.document.body.appendChild(input);
    // A conversation's worth of elements, so appDocument() picks the app over the shell and the send's floor is met.
    for (var pi = 0; pi < 40; pi++) inner.document.body.appendChild(inner.make('div', { 'class': 'message' }, 'm'));
    var f = { outer: outer, inner: inner, input: input, yes: null, shellFocus: outer.HTMLElement.prototype.focus,
              focus: inner.HTMLElement.prototype.focus, svgFocus: inner.SVGElement.prototype.focus };
    function stealAfter() {
      if (steal.indexOf('sync') >= 0) input.focus();
      if (steal.indexOf('timeout') >= 0) inner.setTimeout(function () { input.focus(); }, 50);
      if (steal.indexOf('reveal') >= 0) page.focusTo(inner, input);
      if (steal.indexOf('throw') >= 0) throw new Error('the card handler threw');
    }
    if (shape === 'claude') {
      var c = inner.make('div', { 'class': 'permissionRequestContainer_q1' });
      var bc = inner.make('div', { 'class': 'buttonContainer_q2' });
      c.appendChild(bc); inner.document.body.appendChild(c);
      var labels = ['Yes', 'No'];
      for (var li = 0; li < labels.length; li++) {
        var b = inner.make('button');
        b.appendChild(inner.make('span', { 'class': 'shortcutNum_q3' }, String(li + 1)));
        b.appendChild(inner.make('span', {}, labels[li]));
        bc.appendChild(b);
        if (li === 0) f.yes = b;
      }
    } else if (shape === 'codex') {
      var card = inner.make('div', { 'class': 'flex @container/approval-card' });
      var form = inner.make('form');
      card.appendChild(form); inner.document.body.appendChild(card);
      form.appendChild(inner.make('button', {}, 'Deny'));
      f.yes = form.appendChild(inner.make('button', {}, 'Allow once'));
    }
    if (f.yes) f.yes.handlers.click = [function () { page.approved[name] = (page.approved[name] || 0) + 1; stealAfter(); }];
    input.handlers.keydown = [function (ev) {
      if (ev.key !== 'Enter') return;
      page.sent[name] = input.textContent; input.textContent = '';
      stealAfter();
    }];
    page.frames[name] = f;
  };
  page.user = function (name) { var f = page.frames[name]; page.focusTo(f.inner, f.input); };
  page.breakClick = function (name) { page.frames[name].yes.click = function () { throw new Error('click threw'); }; };
  page.state = function () {
    var own = Object.prototype.hasOwnProperty;
    var o = { focused: page.focused ? page.focused.name_ : '', log: page.focusLog.join(','), timers: page.timers.length, frames: {} };
    for (var n in page.frames) {
      var f = page.frames[n], p = f.inner.HTMLElement.prototype, sp = f.inner.SVGElement.prototype;
      o.frames[n] = {
        approved: page.approved[n] || 0, sent: page.sent[n] || '',
        inputActive: f.inner.document.activeElement === f.input,
        exact: p.focus === f.focus && own.call(p, 'focus') && sp.focus === f.svgFocus && own.call(sp, 'focus'),
        patched: p.focus !== f.focus,
        hold: !!f.inner.__agentflowFocusHold,
        shellTouched: f.outer.HTMLElement.prototype.focus !== f.shellFocus || !!f.outer.__agentflowFocusHold
      };
    }
    return JSON.stringify(o);
  };
  return page;
})();
'loaded'";
    }
}
