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
        public string ToolName = "";
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
    internal static class CdpApprover
    {
        /// <summary>
        /// Returns a JSON string describing the prompt, or the string "none".
        ///
        /// "none" rather than null so the two failure shapes stay apart: a target with no prompt
        /// answers "none", while a target that could not be reached at all yields no value. Only
        /// the second is worth reporting as a fault.
        /// </summary>
        private const string ReadExpression = @"
(function () {
  var c = document.querySelector('[class*=""permissionRequestContainer""]');
  if (!c) return 'none';
  var bc = c.querySelector('[class*=""buttonContainer""]');
  if (!bc) return 'none';
  var out = { tool: '', options: [], disabled: [] };
  var h = c.querySelector('[class*=""permissionRequestHeader""] strong');
  if (h) out.tool = (h.textContent || '').trim();
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
        private const string ClickExpressionFormat = @"
(function () {{
  var c = document.querySelector('[class*=""permissionRequestContainer""]');
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
        public static string Sweep(int port, Func<PromptView, string> press, int timeoutMs)
        {
            List<string> targetIds = ClaudeTargetIds(port, timeoutMs);
            if (targetIds.Count == 0) return null;

            string browserUrl = BrowserSocketUrl(port, timeoutMs);
            if (string.IsNullOrEmpty(browserUrl)) return null;

            try
            {
                using (var session = new CdpSession(browserUrl, timeoutMs))
                {
                    foreach (string targetId in targetIds)
                    {
                        string sessionId = session.Attach(targetId);
                        if (sessionId == null) continue;
                        try
                        {
                            string raw = session.Evaluate(sessionId, ReadExpression);
                            if (string.IsNullOrEmpty(raw) || raw == "none") continue;
                            PromptView view = Parse(targetId, raw);
                            if (view == null || view.Options.Count == 0) continue;
                            string note = press(view);
                            if (note != null) return note;
                        }
                        finally { session.Detach(sessionId); }
                    }
                }
            }
            catch (Exception)
            {
                // Deliberately broad. The editor closing, the port moving, a target vanishing
                // mid-read: all mean the same thing to the caller, which is that there is no
                // answer, so do nothing. An approver that threw on a closed editor would take the
                // poll down with it.
                return null;
            }
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
            if (targetId == null || index < 0 || expectedLabel == null) return "gone";
            string browserUrl = BrowserSocketUrl(port, timeoutMs);
            if (string.IsNullOrEmpty(browserUrl)) return "gone";
            string expression = string.Format(CultureInfo.InvariantCulture, ClickExpressionFormat,
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
                    var view = new PromptView { TargetId = targetId, ToolName = Str(root, "tool") };
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
        internal static List<string> ClaudeTargetIds(int port, int timeoutMs)
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
                        if (Str(item, "url").IndexOf(ClaudeTargetMarker,
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
                _socket.ConnectAsync(new Uri(url), _cancel.Token).GetAwaiter().GetResult();
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
                        var document = JsonDocument.Parse(message);
                        JsonElement replyId;
                        if (document.RootElement.TryGetProperty("id", out replyId)
                            && replyId.ValueKind == JsonValueKind.Number
                            && replyId.GetInt32() == id)
                        {
                            reply = document.RootElement.Clone();
                            document.Dispose();
                            return true;
                        }
                        document.Dispose();
                    }
                    catch (JsonException) { }
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

        private static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMilliseconds(Math.Max(200, timeoutMs));
                    return client.GetStringAsync(url).GetAwaiter().GetResult();
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
