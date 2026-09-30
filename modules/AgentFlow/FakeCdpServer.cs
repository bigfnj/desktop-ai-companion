using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// A loopback Chrome DevTools Protocol endpoint, just real enough to drive
    /// <see cref="CdpApprover.Sweep"/> end to end.
    ///
    /// WHY THIS EXISTS. Everything from the wire inwards -- <c>Interpret</c>, <c>Parse</c>, the click
    /// templates -- was asserted only against RECORDED strings handed straight to those methods. That
    /// leaves the part in between untested: target discovery, the attach/evaluate/detach handshake, and
    /// what <c>Sweep</c> concludes from a whole pass. Two of the 1.3.1 fixes were "defended by
    /// structure" for exactly this reason, and one of them -- <c>sawPanel</c> reporting false after a
    /// successful press, which turned the tray amber at the moment the feature worked -- is invisible to
    /// a recorded-string test, because it is about control flow AFTER a press rather than about parsing.
    ///
    /// WHAT IT SPEAKS. Only the four things CdpApprover actually asks for:
    ///   GET  /json/list       the target list, each with an id and a url carrying the agent marker
    ///   GET  /json/version    the browser-level webSocketDebuggerUrl
    ///   ws   Target.attachToTarget / Target.detachFromTarget
    ///   ws   Runtime.evaluate  answered per target from a script supplied by the test
    ///
    /// It is NOT a CDP implementation and must not grow into one. Anything this does not answer should
    /// stay unanswered, so a test that needs more has to say so out loud.
    ///
    /// SENDS AN EVENT BEFORE EVERY REPLY, on purpose. The real socket carries target lifecycle
    /// notifications as well as answers, and CdpSession.Send matches replies by id precisely because a
    /// reader that took the next message would sometimes read an event instead. A fake that only ever
    /// replied would let that regression back in silently, so this one always interleaves.
    /// </summary>
    internal sealed class FakeCdpServer : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly List<Target> _targets = new List<Target>();
        private readonly object _lock = new object();
        private int _evaluateCount;
        private int _attachCount;
        private int _listCount;

        internal sealed class Target
        {
            public string Id;
            public string Url;
            /// <summary>What Runtime.evaluate returns for this target, or null to answer with no value
            /// at all -- which is how a target that cannot be read presents on the wire.</summary>
            public string EvaluateResult;
            /// <summary>What Runtime.evaluate returns for the CLICK template (recognised by its `b.click()`),
            /// or null to answer it like any other expression. Production presses through a second connection
            /// inside the sweep, so a press test needs the read and the click told apart (F048).</summary>
            public string ClickResult;
            /// <summary>Answer Runtime.evaluate with exceptionDetails instead of a value.</summary>
            public bool Throws;
            /// <summary>Refuse the attach, as a target that vanished mid-sweep does.</summary>
            public bool RefuseAttach;
        }

        internal int Port { get; private set; }
        internal int EvaluateCount { get { lock (_lock) return _evaluateCount; } }
        internal int AttachCount { get { lock (_lock) return _attachCount; } }
        /// <summary>GETs of /json/list served. A sweep should need exactly one (F044).</summary>
        internal int ListCount { get { lock (_lock) return _listCount; } }

        internal FakeCdpServer(IEnumerable<Target> targets)
        {
            if (targets != null) _targets.AddRange(targets);

            // A free port, found by asking the OS rather than by guessing one and hoping. A hard-coded
            // port makes the test fail for anyone who happens to be running something on it, which is a
            // test failure that says nothing about the code.
            Port = FreePort();
            _listener.Prefixes.Add("http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/");
            _listener.Start();
            Task.Run(new Action(Loop));
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        private void Loop()
        {
            while (!_cancel.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch { return; }   // disposed: the only way out of GetContext
                // Each connection on its own task. Serve blocks until the client closes its socket, and until
                // 2026-09-30 it ran on THIS thread, so no second request was accepted while a WebSocket session
                // was open. Production presses INSIDE the sweep's session (Decide -> CdpApprover.Click opens a
                // second connection), so a press could not be tested at all: the nested GET waited in the
                // listener queue, timed out, and read as "gone" (F048). The catch stays inside the task, so a
                // fake that throws still cannot take the test process with it.
                HttpListenerContext accepted = context;
                Task.Run(delegate
                {
                    try { Handle(accepted); }
                    catch { /* a fake server that throws must not take the test process with it */ }
                });
            }
        }

        private void Handle(HttpListenerContext context)
        {
            string path = context.Request.Url.AbsolutePath;

            if (context.Request.IsWebSocketRequest)
            {
                HttpListenerWebSocketContext ws = context.AcceptWebSocketAsync(null).GetAwaiter().GetResult();
                Serve(ws.WebSocket);
                return;
            }

            if (string.Equals(path, "/json/version", StringComparison.Ordinal))
            {
                Write(context, "{\"webSocketDebuggerUrl\":\"ws://127.0.0.1:"
                    + Port.ToString(CultureInfo.InvariantCulture) + "/devtools/browser/fake\"}");
                return;
            }

            if (string.Equals(path, "/json/list", StringComparison.Ordinal))
            {
                var sb = new StringBuilder("[");
                lock (_lock)
                {
                    _listCount++;
                    for (int i = 0; i < _targets.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"id\":").Append(Quote(_targets[i].Id))
                          .Append(",\"url\":").Append(Quote(_targets[i].Url))
                          .Append(",\"type\":\"page\"}");
                    }
                }
                sb.Append(']');
                Write(context, sb.ToString());
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        private static void Write(HttpListenerContext context, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        private void Serve(WebSocket socket)
        {
            var buffer = new byte[64 * 1024];
            // sessionId -> targetId, so Runtime.evaluate can answer as the target it is attached to.
            var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
            int nextSession = 0;

            try
            {
                while (socket.State == WebSocketState.Open && !_cancel.IsCancellationRequested)
                {
                    WebSocketReceiveResult received =
                        socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancel.Token).GetAwaiter().GetResult();
                    if (received.MessageType == WebSocketMessageType.Close) break;

                    string request = Encoding.UTF8.GetString(buffer, 0, received.Count);
                    int id = IntField(request, "id");
                    string method = StringField(request, "method");

                    // ALWAYS an event first. See the class note: replies are matched by id, and this is
                    // what keeps that mattering.
                    Send(socket, "{\"method\":\"Target.targetInfoChanged\",\"params\":{}}");

                    if (string.Equals(method, "Target.attachToTarget", StringComparison.Ordinal))
                    {
                        string targetId = StringField(request, "targetId");
                        Target target = Find(targetId);
                        lock (_lock) { _attachCount++; }
                        if (target == null || target.RefuseAttach)
                        {
                            Send(socket, "{\"id\":" + id + ",\"error\":{\"message\":\"No such target\"}}");
                            continue;
                        }
                        nextSession++;
                        string sessionId = "session-" + nextSession.ToString(CultureInfo.InvariantCulture);
                        sessions[sessionId] = targetId;
                        Send(socket, "{\"id\":" + id + ",\"result\":{\"sessionId\":" + Quote(sessionId) + "}}");
                        continue;
                    }

                    if (string.Equals(method, "Target.detachFromTarget", StringComparison.Ordinal))
                    {
                        sessions.Remove(StringField(request, "sessionId"));
                        Send(socket, "{\"id\":" + id + ",\"result\":{}}");
                        continue;
                    }

                    if (string.Equals(method, "Runtime.evaluate", StringComparison.Ordinal))
                    {
                        lock (_lock) { _evaluateCount++; }
                        string sessionId = StringField(request, "sessionId");
                        string targetId;
                        Target target = sessions.TryGetValue(sessionId, out targetId) ? Find(targetId) : null;

                        if (target == null)
                        {
                            Send(socket, "{\"id\":" + id + ",\"error\":{\"message\":\"No session\"}}");
                        }
                        else if (target.Throws)
                        {
                            Send(socket, "{\"id\":" + id + ",\"result\":{\"result\":{\"type\":\"object\"},"
                                + "\"exceptionDetails\":{\"text\":\"Uncaught\"}}}");
                        }
                        else if (target.ClickResult != null && IsClickTemplate(request))
                        {
                            // The press. Both click templates end in `b.click()`; the read expressions do
                            // not, so this is what tells a press apart from the read that preceded it (F048).
                            Send(socket, "{\"id\":" + id + ",\"result\":{\"result\":{\"type\":\"string\",\"value\":"
                                + Quote(target.ClickResult) + "}}}");
                        }
                        else if (target.EvaluateResult == null)
                        {
                            // A result with no value: the shape a target gives when the expression
                            // returned undefined, which Evaluate must read as "no answer" rather than "".
                            Send(socket, "{\"id\":" + id + ",\"result\":{\"result\":{\"type\":\"undefined\"}}}");
                        }
                        else
                        {
                            Send(socket, "{\"id\":" + id + ",\"result\":{\"result\":{\"type\":\"string\",\"value\":"
                                + Quote(target.EvaluateResult) + "}}}");
                        }
                        continue;
                    }

                    Send(socket, "{\"id\":" + id + ",\"result\":{}}");
                }
            }
            catch { /* the client going away is the ordinary end of a sweep */ }
            finally
            {
                try { socket.Dispose(); } catch { }
            }
        }

        private Target Find(string id)
        {
            lock (_lock)
                foreach (Target t in _targets)
                    if (string.Equals(t.Id, id, StringComparison.Ordinal)) return t;
            return null;
        }

        /// <summary>Whether a Runtime.evaluate request carries a CLICK template rather than a read: the
        /// request's `params.expression` (a JSON string, read through the same small reader as every other
        /// field) contains the `b.click()` both templates end in and neither read expression has.</summary>
        private static bool IsClickTemplate(string request)
        {
            return StringField(request, "expression").IndexOf("b.click()", StringComparison.Ordinal) >= 0;
        }

        private static void Send(WebSocket socket, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        // ---- the smallest JSON reading that will do -------------------------------------------
        // Deliberately not JsonDocument: this is a fake, and a hand-rolled reader here cannot mask a
        // defect in the real parser under test. It only ever sees strings this file's own caller wrote.

        private static string StringField(string json, string name)
        {
            string key = "\"" + name + "\":\"";
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return "";
            at += key.Length;
            var sb = new StringBuilder();
            while (at < json.Length && json[at] != '"')
            {
                if (json[at] == '\\' && at + 1 < json.Length) at++;
                sb.Append(json[at]);
                at++;
            }
            return sb.ToString();
        }

        private static int IntField(string json, string name)
        {
            string key = "\"" + name + "\":";
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return 0;
            at += key.Length;
            int end = at;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            int value;
            return int.TryParse(json.Substring(at, end - at), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        public void Dispose()
        {
            try { _cancel.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _cancel.Dispose(); } catch { }
        }
    }
}
