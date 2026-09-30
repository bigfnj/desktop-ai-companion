using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// A loopback HTTP/1.1 server for the module self-test that answers one GET the way a misbehaving feed
    /// host does: 200 OK, a chunked body that starts arriving, and then either silence with the connection
    /// held open (<see cref="Mode.Stall"/>) or chunks past any sane size before the same silence
    /// (<see cref="Mode.Flood"/>). Never a terminating chunk, never a close, until the test lets go.
    ///
    /// A raw socket rather than HttpListener, because the point is control over the bytes on the wire -- a
    /// body that never ends is not something HttpListener offers -- and because a TcpListener on the loopback
    /// address needs no URL reservation. Same ask-the-OS free-port idiom as AgentFlow's FakeCdpServer. It
    /// exists for F189 (IcsUrlSource.Download's deadline and size cap) and contacts nothing real.
    /// </summary>
    internal sealed class StallingFeedServer : IDisposable
    {
        internal enum Mode { Stall, Flood }

        private readonly TcpListener _listener;
        private readonly ManualResetEventSlim _released = new ManualResetEventSlim(false);
        private readonly Mode _mode;
        private readonly long _floodBytes;
        private volatile bool _headersSent;

        internal StallingFeedServer(Mode mode, long floodBytes)
        {
            _mode = mode;
            _floodBytes = floodBytes;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/feed.ics";
            Task.Run(new Action(Serve));
        }

        internal string Url { get; private set; }

        /// <summary>True once the status line, the headers and the first chunk went out, so a test can tell
        /// "the client never got the headers" from "the BODY stalled", which is the case under test.</summary>
        internal bool HeadersSent { get { return _headersSent; } }

        private void Serve()
        {
            TcpClient client = null;
            try
            {
                client = _listener.AcceptTcpClient();
                NetworkStream stream = client.GetStream();
                // Consume the request head, so the client is never blocked on a send buffer it cannot drain.
                var head = new StringBuilder();
                byte[] one = new byte[1];
                while (!EndsWithBlankLine(head))
                {
                    if (stream.Read(one, 0, 1) <= 0) return;
                    head.Append((char)one[0]);
                }
                byte[] headers = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/calendar\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
                stream.Write(headers, 0, headers.Length);
                WriteChunk(stream, Encoding.ASCII.GetBytes("BEGIN:VCALENDAR\r\n"));
                _headersSent = true;
                if (_mode == Mode.Flood)
                {
                    byte[] filler = new byte[16 * 1024];
                    for (int i = 0; i < filler.Length; i++) filler[i] = (byte)'X';
                    long sent = 0;
                    while (sent < _floodBytes && !_released.IsSet)
                    {
                        WriteChunk(stream, filler);
                        sent += filler.Length;
                    }
                }
                // Then nothing, until the test disposes this.
                _released.Wait();
            }
            catch
            {
                // The client gave up (the behaviour under test) or Dispose closed the listener: both are the
                // test ending, and a fake server that throws must not take the self-test process with it.
            }
            finally
            {
                try { if (client != null) client.Close(); } catch { }
            }
        }

        private static bool EndsWithBlankLine(StringBuilder head)
        {
            int n = head.Length;
            return n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n';
        }

        private static void WriteChunk(Stream stream, byte[] data)
        {
            byte[] size = Encoding.ASCII.GetBytes(data.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
            stream.Write(size, 0, size.Length);
            stream.Write(data, 0, data.Length);
            stream.Write(new byte[] { 13, 10 }, 0, 2);
            stream.Flush();
        }

        public void Dispose()
        {
            _released.Set();
            try { _listener.Stop(); } catch { }
        }
    }
}
