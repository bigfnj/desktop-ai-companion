using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// The one HTTP path the Zoom slot uses (1.3.0): the token endpoint and the two meetings calls. Bounded the way
    /// <see cref="IcsUrlSource.Download"/> is, and for the same reason (F189): ONE deadline over the headers and the
    /// body together, and a size cap checked on every chunk, so a server that answers 200 and stalls mid-body cannot
    /// hold the slot's refresh, and a body with no Content-Length cannot be buffered without limit. Unlike the .ics
    /// download it hands back every status with its body rather than throwing on a non-2xx, because Zoom's 4xx body
    /// says WHY (invalid_grant, a missing scope) and the callers need that to word the status line. Throws only for
    /// a transport failure, a deadline or the cap; callers turn those into an error snapshot.
    /// </summary>
    internal static class ZoomHttp
    {
        internal sealed class Response
        {
            public int Status;
            public string Body;
        }

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // Timeout as a backstop only: each call carries its own token cut from its deadline, which also covers
            // the body read that HttpClient.Timeout stops guarding once the headers are in (ResponseHeadersRead).
            // No redirects: every endpoint here is a fixed Zoom https address, and a redirect off one is either a
            // fault or someone else's server, which must not receive a bearer token or a token form.
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopAICompanion-Reminder/1.3");
            return http;
        }

        /// <summary>A GET with a bearer token. The token never reaches a log or an exception message.</summary>
        internal static Response Get(string url, string bearer, TimeSpan deadline, long maximumBytes)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? "");
                request.Headers.Accept.ParseAdd("application/json");
                return Send(request, deadline, maximumBytes);
            }
        }

        internal static Response Send(HttpRequestMessage request, TimeSpan deadline, long maximumBytes)
        {
            Uri uri = request.RequestUri;
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Zoom is only reached over https.");
            bool headersReceived = false;
            try
            {
                using (var cts = new CancellationTokenSource(deadline))
                using (HttpResponseMessage response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                           .GetAwaiter().GetResult())
                {
                    headersReceived = true;
                    if (response.Content.Headers.ContentLength.HasValue &&
                        response.Content.Headers.ContentLength.Value > maximumBytes)
                        throw new InvalidOperationException("Zoom's answer is too large.");
                    using (Stream body = response.Content.ReadAsStreamAsync(cts.Token).GetAwaiter().GetResult())
                    using (var buffer = new MemoryStream())
                    {
                        byte[] chunk = new byte[16 * 1024];
                        while (true)
                        {
                            int read = body.ReadAsync(chunk, 0, chunk.Length, cts.Token).GetAwaiter().GetResult();
                            if (read <= 0) break;
                            if (buffer.Length + read > maximumBytes)
                                throw new InvalidOperationException("Zoom's answer is too large.");
                            buffer.Write(chunk, 0, read);
                        }
                        return new Response
                        {
                            Status = (int)response.StatusCode,
                            Body = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length),
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                string seconds = deadline.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                throw new TimeoutException(headersReceived
                    ? "Zoom's answer did not finish arriving within " + seconds + " seconds."
                    : "Zoom did not respond within " + seconds + " seconds.");
            }
        }
    }
}
