using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

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
    /// took DOM focus. input/textarea select() move focus without going through HTMLElement.focus, as in Chromium.
    /// <c>document.hasFocus()</c> is true for the focused window and its ancestors. setTimeout and Date run on a clock
    /// that follows real time plus every <see cref="Advance"/>, and due timers run in due order (with the clock AT their
    /// due time) before every evaluation and on <see cref="Pump"/>. Window capture listeners, and the user's trusted
    /// pointer press (<see cref="UserPointer"/>), which moves focus natively. A card's Yes button runs a handler that
    /// approves and then focuses as told: synchronously, in a 50 ms timeout, through select(), through <c>reveal</c> (a
    /// path no in-page hold can reach: an extension calling reveal() on its view), Claude Code's permission-clear
    /// effect (card removed, setTimeout 100, composer focus unless an editable is active), its diff path (the same, ten
    /// seconds later), or a Codex follow-on card whose primary button autofocuses on mount. The Claude and Codex card
    /// shapes the click templates look for, and the chat box the session-limit send types into.
    ///
    /// WHAT IT DOES NOT MODEL. Real Chromium focus rules (user activation, cross-origin frame restrictions), focus and
    /// blur events, VS Code's webview host and its did-focus message, React scheduling, microtasks,
    /// requestAnimationFrame, background-timer throttling, shadow roots, the Selection API, and the READ expressions
    /// (cloneNode and friends are absent): reads stay canned, as in every other FakeCdpServer test. Selectors: a tag,
    /// `*`, and one [attr="v"] or [attr*="v"], which is all the press, probe and restore expressions use.
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

        private long _skewMs;

        /// <summary>The page clock: real time since the page was made, plus every <see cref="Advance"/>.</summary>
        private long Now { get { return _clock.ElapsedMilliseconds + Interlocked.Read(ref _skewMs); } }

        /// <summary>Run due timers up to now.</summary>
        internal void Pump() { Run("__page.pump(" + Now + "); 'ok'"); }

        /// <summary>Move the page clock <paramref name="ms"/> ahead of real time and run what fell due, in due order, so
        /// a card's ten-second deferred path costs the check no ten seconds.</summary>
        internal void Advance(int ms)
        {
            Interlocked.Add(ref _skewMs, ms);
            Pump();
        }

        /// <summary>The user presses the pointer in <paramref name="frame"/>'s chat box: a TRUSTED pointerdown and
        /// mousedown to the app window's capture listeners, then the browser's own focus move (not through any focus
        /// method, so no hold can stop it).</summary>
        internal void UserPointer(string frame) { Run("__page.userPointer('" + frame + "'); 'ok'"); }

        /// <summary>The user types in <paramref name="frame"/>: a trusted keydown and beforeinput, no focus change.</summary>
        internal void UserType(string frame) { Run("__page.userType('" + frame + "'); 'ok'"); }

        /// <summary>The user presses a dropdown trigger in <paramref name="frame"/> that prevents the native focus; its menu
        /// mounts 50 ms later and focuses itself in script.</summary>
        internal void UserPressNoFocus(string frame) { Run("__page.userPressNoFocus('" + frame + "'); 'ok'"); }

        /// <summary>The user leaves <paramref name="from"/> by keyboard alone: a trusted Ctrl keydown delivered there, then
        /// the browser's own focus move to <paramref name="to"/>, or out of every panel when it is null.</summary>
        internal void UserKeyTo(string from, string to)
        {
            Run("__page.userKeyTo('" + from + "', '" + (to ?? "") + "'); 'ok'");
        }

        /// <summary>A trusted Escape keydown to <paramref name="frame"/>'s listeners; the state's keyActions counts any
        /// preventDefault or propagation change made on it.</summary>
        internal void UserEscape(string frame) { Run("__page.userEscape('" + frame + "'); 'ok'"); }

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
        /// 'timeout', 'late' (600 ms), 'select', 'reveal', 'effect', 'diff', 'nextcard', 'throw' (the handler throws) or 'none'.</summary>
        internal void AddFrame(string name, string shape, string steal)
        {
            Run("__page.frame('" + name + "', '" + shape + "', '" + steal + "'); 'ok'");
        }

        /// <summary>Put the user in <paramref name="frame"/>'s chat box (or nowhere for null), and clear the focus log.</summary>
        internal void UserIn(string frame)
        {
            Run((frame == null ? "__page.focused = null;" : "__page.user('" + frame + "');") + " __page.focusLog = []; 'ok'");
        }

        /// <summary>Put the user's focus in <paramref name="frame"/> with no element focused (its body), and clear the log.</summary>
        internal void UserAtBody(string frame) { Run("__page.userAt('" + frame + "'); __page.focusLog = []; 'ok'"); }

        /// <summary>A Codex approval card mounts in <paramref name="frame"/> on its own and autofocuses its button.</summary>
        internal void MountCard(string frame) { Run("__page.mountCard('" + frame + "'); 'ok'"); }

        /// <summary>Claude Code's card in <paramref name="frame"/> clears on its own; its composer refocus follows 100 ms later.</summary>
        internal void ClearCard(string frame) { Run("__page.clearCard('" + frame + "'); 'ok'"); }

        /// <summary><paramref name="frame"/> reloads: fresh documents and realms, no guard, same target.</summary>
        internal void ReloadFrame(string frame) { Run("__page.reloadFrame('" + frame + "'); 'ok'"); }

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
  var page = { now: 0, timers: [], seq: 0, focused: null, frames: {}, approved: {}, sent: {}, focusLog: [],
               keysBuilt: 0, keysSent: 0, keyActions: 0 };
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
      if (ev && typeof ev.type === 'string' && ev.type.indexOf('key') === 0) page.keysSent++;
      var hs = this.handlers[ev.type] || [];
      for (var i = 0; i < hs.length; i++) hs[i].call(this, ev);
      return true;
    };
    HTMLElement.prototype.appendChild = function (c) { c.parentNode = this; this.children.push(c); return c; };
    HTMLElement.prototype.removeChild = function (c) {
      var at = this.children.indexOf(c);
      if (at >= 0) { this.children.splice(at, 1); c.parentNode = null; }
      return c;
    };
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
    // select() moves DOM focus without going through HTMLElement.focus, as Codex's Chromium probe measured.
    function HTMLInputElement() { }
    HTMLInputElement.prototype = Object.create(HTMLElement.prototype);
    HTMLInputElement.prototype.select = function () { page.focusTo(win, this); };
    function HTMLTextAreaElement() { }
    HTMLTextAreaElement.prototype = Object.create(HTMLElement.prototype);
    HTMLTextAreaElement.prototype.select = function () { page.focusTo(win, this); };
    function InputEvent(type, init) { this.type = type; this.isTrusted = false; for (var k in init) this[k] = init[k]; }
    function KeyboardEvent(type, init) { page.keysBuilt++; this.type = type; this.isTrusted = false; for (var k in init) this[k] = init[k]; }
    win.listeners = {};
    win.addEventListener = function (type, fn, capture) { (win.listeners[type] = win.listeners[type] || []).push(fn); };
    win.removeEventListener = function (type, fn, capture) {
      var list = win.listeners[type] || [], at = list.indexOf(fn);
      if (at >= 0) list.splice(at, 1);
    };
    win.fire = function (ev) { var list = (win.listeners[ev.type] || []).slice(); for (var i = 0; i < list.length; i++) list[i].call(win, ev); };
    win.HTMLElement = HTMLElement; win.SVGElement = SVGElement;
    win.HTMLInputElement = HTMLInputElement; win.HTMLTextAreaElement = HTMLTextAreaElement;
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
      var e = Object.create(tag === 'textarea' ? HTMLTextAreaElement.prototype
                          : tag === 'input' ? HTMLInputElement.prototype : HTMLElement.prototype);
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
    var notes = inner.make('textarea', { 'class': 'notes' });
    inner.document.body.appendChild(notes);
    var f = { outer: outer, inner: inner, input: input, yes: null, card: null, shellFocus: outer.HTMLElement.prototype.focus,
              focus: inner.HTMLElement.prototype.focus, svgFocus: inner.SVGElement.prototype.focus,
              inputSelect: inner.HTMLInputElement.prototype.select, areaSelect: inner.HTMLTextAreaElement.prototype.select };
    function editable(el) { return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || (el.attrs && el.attrs.contenteditable)); }
    // Claude Code's parent effect when the permission count drops to zero: setTimeout(100), then the composer's
    // safeFocus, which checks visibility and not document focus (audit: index.js 2.1.314 byte 5897170).
    function cardCleared() {
      if (f.card && f.card.parentNode) f.card.parentNode.removeChild(f.card);
      inner.setTimeout(function () { if (!editable(inner.document.activeElement)) input.focus(); }, 100);
    }
    // A Codex follow-on approval card: its primary button has React autoFocus, focused in the mount commit unless an
    // editable in THIS document is active (audit: app-initial byte 550100). 150 ms: inside the 26-240 ms measured.
    function nextCard() {
      if (f.card && f.card.parentNode) f.card.parentNode.removeChild(f.card);
      inner.setTimeout(function () {
        build('codex');
        if (!editable(inner.document.activeElement)) f.yes.focus();
      }, 150);
    }
    function stealAfter() {
      if (steal.indexOf('sync') >= 0) input.focus();
      if (steal.indexOf('timeout') >= 0) inner.setTimeout(function () { input.focus(); }, 50);
      // A deferred refocus well after the press, inside the settle window: the case where the user leaves first.
      if (steal.indexOf('late') >= 0) inner.setTimeout(function () { input.focus(); }, 600);
      if (steal.indexOf('select') >= 0) notes.select();
      if (steal.indexOf('reveal') >= 0) page.focusTo(inner, input);
      if (steal.indexOf('effect') >= 0) cardCleared();
      // Claude's diff path: acceptance raced against 5000 ms, then a further 5000 ms fallback, before the card clears.
      if (steal.indexOf('diff') >= 0) inner.setTimeout(cardCleared, 10000);
      if (steal.indexOf('nextcard') >= 0) nextCard();
      if (steal.indexOf('throw') >= 0) throw new Error('the card handler threw');
    }
    function build(kind) {
      if (kind === 'claude') {
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
        f.card = c;
      } else if (kind === 'codex') {
        var card = inner.make('div', { 'class': 'flex @container/approval-card' });
        var form = inner.make('form');
        card.appendChild(form); inner.document.body.appendChild(card);
        form.appendChild(inner.make('button', {}, 'Deny'));
        f.yes = form.appendChild(inner.make('button', {}, 'Allow once'));
        f.card = card;
      }
      if (f.yes) f.yes.handlers.click = [function () { page.approved[name] = (page.approved[name] || 0) + 1; stealAfter(); }];
    }
    build(shape);
    f.shape = shape; f.steal = steal;
    // A Codex approval card mounting on its own, no press anywhere: its primary button autofocuses in the mount
    // commit unless an editable in THIS document is active (the owner's live failure of 2026-11-02).
    f.mount = function () { build('codex'); if (!editable(inner.document.activeElement)) f.yes.focus(); };
    // Claude Code's permission card clearing on its own (answered elsewhere): the parent effect's 100 ms refocus.
    f.clear = cardCleared;
    input.handlers.keydown = [function (ev) {
      if (ev.key !== 'Enter') return;
      page.sent[name] = input.textContent; input.textContent = '';
      stealAfter();
    }];
    page.frames[name] = f;
  };
  page.user = function (name) { var f = page.frames[name]; page.focusTo(f.inner, f.input); };
  page.userAt = function (name) { var f = page.frames[name]; page.focused = f.inner; f.inner.document.activeElement = f.inner.document.body; };
  // A trusted event to a window's capture listeners, with spies on everything that would alter the user's key.
  function trusted(win, init) {
    var ev = { isTrusted: true, preventDefault: function () { page.keyActions++; },
               stopPropagation: function () { page.keyActions++; }, stopImmediatePropagation: function () { page.keyActions++; } };
    for (var k in init) ev[k] = init[k];
    win.fire(ev);
  }
  // The user types in a frame they are in: a trusted keydown and beforeinput, no focus change.
  page.userType = function (name) {
    var f = page.frames[name];
    trusted(f.inner, { type: 'keydown', key: 'a' });
    trusted(f.inner, { type: 'beforeinput', inputType: 'insertText', data: 'a' });
  };
  // The user leaves one frame by keyboard alone (a Ctrl shortcut, delivered to the frame they are in), and the
  // browser moves focus to another frame natively, or to nowhere (an editor) when to is empty.
  page.userKeyTo = function (from, to) {
    trusted(page.frames[from].inner, { type: 'keydown', key: '2', ctrlKey: true });
    if (!to) { page.focused = null; page.focusLog.push('user:away'); return; }
    var t = page.frames[to];
    page.focused = t.inner; t.inner.document.activeElement = t.input; page.focusLog.push('user:' + to);
  };
  // A trusted Escape keydown to a frame's listeners, to see whether anything installed there acts on a key.
  page.userEscape = function (name) { trusted(page.frames[name].inner, { type: 'keydown', key: 'Escape' }); };
  // The user presses a control whose own handler prevents the native pointer and mouse focus (a dropdown trigger):
  // trusted pointerdown and mousedown, NO focus move, and the menu mounts on a later task and focuses itself in
  // script, as the installed Codex dropdown does.
  page.userPressNoFocus = function (name) {
    var f = page.frames[name];
    trusted(f.inner, { type: 'pointerdown' });
    trusted(f.inner, { type: 'mousedown' });
    f.inner.setTimeout(function () {
      var menu = f.inner.make('div', { role: 'menu' });
      f.inner.document.body.appendChild(menu);
      f.menu = menu;
      menu.focus();
    }, 50);
  };
  page.userPointer = function (name) {
    var f = page.frames[name];
    f.inner.fire({ type: 'pointerdown', isTrusted: true });
    f.inner.fire({ type: 'mousedown', isTrusted: true });
    page.focused = f.inner; f.inner.document.activeElement = f.input; page.focusLog.push('user:' + name);
  };
  page.mountCard = function (name) { page.frames[name].mount(); };
  page.clearCard = function (name) { page.frames[name].clear(); };
  // A reload: the frame's documents and windows are replaced by fresh ones (fresh realms, no guard), same target.
  page.reloadFrame = function (name) {
    var old = page.frames[name];
    if (page.focused === old.inner || page.focused === old.outer) page.focused = null;
    page.frame(name, old.shape, old.steal);
  };
  page.breakClick = function (name) { page.frames[name].yes.click = function () { throw new Error('click threw'); }; };
  page.state = function () {
    var own = Object.prototype.hasOwnProperty;
    var o = { focused: page.focused ? page.focused.name_ : '', log: page.focusLog.join(','), timers: page.timers.length,
              keysBuilt: page.keysBuilt, keysSent: page.keysSent, keyActions: page.keyActions, frames: {} };
    for (var n in page.frames) {
      var f = page.frames[n], p = f.inner.HTMLElement.prototype, sp = f.inner.SVGElement.prototype;
      var ip = f.inner.HTMLInputElement.prototype, tp = f.inner.HTMLTextAreaElement.prototype;
      o.frames[n] = {
        approved: page.approved[n] || 0, sent: page.sent[n] || '',
        inputActive: f.inner.document.activeElement === f.input,
        exact: p.focus === f.focus && own.call(p, 'focus') && sp.focus === f.svgFocus && own.call(sp, 'focus')
               && ip.select === f.inputSelect && own.call(ip, 'select') && tp.select === f.areaSelect && own.call(tp, 'select'),
        patched: p.focus !== f.focus || tp.select !== f.areaSelect,
        listeners: (f.inner.listeners.pointerdown || []).length + (f.inner.listeners.mousedown || []).length
                   + (f.inner.listeners.keydown || []).length + (f.inner.listeners.beforeinput || []).length,
        mousedown: (f.inner.listeners.mousedown || []).length,
        allowing: !!(f.inner.__agentflowFocusGuard && f.inner.__agentflowFocusGuard.allowUntil > page.now),
        hold: !!(f.inner.__agentflowFocusGuard && f.inner.__agentflowFocusGuard.active),
        shellGuard: !!f.outer.__agentflowFocusGuard,
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
