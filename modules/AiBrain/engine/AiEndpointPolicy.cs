using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Central validation for every endpoint that can receive screen text, screenshots, or API keys.
    /// HTTPS is accepted for remote providers; plaintext HTTP is limited to the local loopback host.
    /// </summary>
    internal static class AiEndpointPolicy
    {
        public const int MaximumResponseBytes = 1024 * 1024;
        /// <summary>
        /// The cap for a MODEL LISTING, which is a catalogue rather than a reply. OpenRouter's /models answered
        /// 754-760 KB for 460 models on 2026-09-29, 72.5% of <see cref="MaximumResponseBytes"/>; the list only
        /// grows, and crossing the reply cap turned the whole cloud catalogue into a silent empty list reported
        /// as "No models found" (F078). Eight times the reply cap leaves years of growth; a listing is read once
        /// per refresh and once per PrepareAsync, never per remark.
        /// </summary>
        public const int MaximumListingResponseBytes = 8 * 1024 * 1024;
        /// <summary>How much of a FAILING answer's body is read for the provider's own error text (F080).</summary>
        public const int MaximumProviderErrorBytes = 4096;
        /// <summary>The longest provider error text carried to the pane.</summary>
        public const int MaximumProviderMessageCharacters = 200;
        /// <summary>
        /// How long a REACHABILITY probe may take. It used to borrow the chat deadline (120 s by default), so a
        /// cloud endpoint that accepted the connection and then said nothing cost every ask two minutes of probe
        /// before the local leg was even asked, and the launch auto-start waited behind it (F105). Ten seconds is
        /// far beyond any answering server (a running Ollama answers /api/tags in 5-56 ms, measured 2026-09-27)
        /// and short enough that a hung cloud is a delay, not a silence. The chat's own deadline is untouched: that
        /// is the user's timeout setting.
        /// </summary>
        public static readonly TimeSpan AvailabilityProbeDeadline = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The bound on a MODEL LISTING (GET /models, GET /api/tags), sized for the 8 MiB listing cap over a slow
        /// link rather than for a reply: OpenRouter's 760 KB catalogue lists in seconds. Until 2026-09-30 a listing
        /// ran under the chat deadline, and the transition re-list F071 put on the ask path then waited out a hung
        /// cloud primary for the user's whole timeout (120 s by default) before the remark went on (R-014). A bound
        /// that trips yields an empty list, which the brain reads as "unknown" and never lets replace a good inventory.
        /// </summary>
        public static readonly TimeSpan ListingDeadline = TimeSpan.FromSeconds(30);

        public static TimeSpan Shorter(TimeSpan a, TimeSpan b)
        {
            return a < b ? a : b;
        }
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        public static bool TryNormalize(string value, out string normalized, out string error)
        {
            normalized = null;
            error = null;

            Uri uri;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri))
            {
                error = "Enter an absolute HTTP or HTTPS endpoint.";
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = "Only HTTP and HTTPS endpoints are supported.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                error = "The endpoint must have a host and must not contain credentials.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "The endpoint must not contain a query string or fragment.";
                return false;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !IsLoopback(uri))
            {
                error = "Plaintext HTTP is allowed only for localhost. Use HTTPS for remote providers.";
                return false;
            }

            normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return true;
        }

        public static string NormalizeOrThrow(string value, string parameterName)
        {
            string normalized;
            string error;
            if (!TryNormalize(value, out normalized, out error))
                throw new ArgumentException(error, parameterName);
            return normalized;
        }

        /// <summary>
        /// The transport every production client is built on: no automatic redirect, so a credential is never
        /// forwarded to a host the provider (or an interception proxy) pointed at, and no cookie jar. The only
        /// place the module sets these, reached by the two PUBLIC constructors (OllamaClient, OpenAiCompatBackend);
        /// the probes inject their own handlers, so the module self-test asserts this factory's two flags directly
        /// (RA-068) rather than trusting that a redirect double exercised them.
        /// </summary>
        public static HttpClientHandler CreateNoRedirectHandler()
        {
            return new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            };
        }

        public static void EnsureNotRedirect(HttpResponseMessage response)
        {
            if (response == null) throw new ArgumentNullException("response");
            int code = (int)response.StatusCode;
            if (code >= 300 && code <= 399)
                throw new AiBackendHttpException(code, false);
        }

        private static bool IsTransientStatus(int statusCode)
        {
            return statusCode == 408 || statusCode == 429 || statusCode >= 500;
        }

        /// <summary>
        /// Throw for anything but a success: a redirect first (<see cref="EnsureNotRedirect"/>), then a failing
        /// status as an <see cref="AiBackendHttpException"/> whose <c>IsTransient</c> is the classification the
        /// retry and the fallover act on (408, 429 and 5xx are transient; every other 4xx is deterministic). A
        /// failing answer's body is read first (bounded, best-effort) so the exception can carry the provider's
        /// own words for the PANE: OpenAI-compatible providers answer every 4xx with
        /// <c>{"error":{"message":...}}</c> and Ollama with <c>{"error":"..."}</c>, and dropping that turned
        /// "Insufficient credits" and "not a valid model ID" into "HTTP 402." and "HTTP 400." (F080). The
        /// diagnostic log never sees the text: <see cref="AiBrain.DescribeError"/> is category-only by contract,
        /// and a provider's message is the one string here that could echo something from the request. The only
        /// classifier: a synchronous twin without the body read stood beside it until 2026-09-30, reached by one
        /// probe and no production path, so the classification the probe asserted was not the one that ran
        /// (R-016).
        /// </summary>
        public static async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            EnsureNotRedirect(response);
            if (response.IsSuccessStatusCode) return;
            int statusCode = (int)response.StatusCode;
            string body;
            try
            {
                body = await ReadResponseStringAsync(
                    response.Content,
                    cancellationToken,
                    MaximumProviderErrorBytes).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { body = ""; }   // oversized, malformed or gone: the status alone is still the answer
            throw new AiBackendHttpException(statusCode, IsTransientStatus(statusCode), ExtractProviderErrorMessage(body));
        }

        /// <summary>
        /// The human-readable part of a provider's error body, or "" when there is none: <c>error.message</c>
        /// (OpenAI, OpenRouter, LM Studio) or a bare <c>error</c> string (Ollama). Control characters are
        /// dropped, whitespace collapsed and the result capped at <see cref="MaximumProviderMessageCharacters"/>,
        /// so a provider cannot put a paragraph, or a line break, into a status line.
        /// </summary>
        public static string ExtractProviderErrorMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            string message = null;
            try
            {
                JsonNode root = JsonNode.Parse(body);
                JsonNode error = root == null ? null : root["error"];
                if (error is JsonObject) message = JsonRead.Str(error["message"]);
                else if (error != null) message = JsonRead.Str(error);
            }
            catch { return ""; }
            if (string.IsNullOrWhiteSpace(message)) return "";

            var clean = new StringBuilder(Math.Min(message.Length, MaximumProviderMessageCharacters));
            bool pendingSpace = false;
            foreach (char c in message)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = clean.Length > 0;
                    continue;
                }
                if (clean.Length + (pendingSpace ? 2 : 1) > MaximumProviderMessageCharacters) break;
                if (pendingSpace)
                {
                    clean.Append(' ');
                    pendingSpace = false;
                }
                clean.Append(c);
            }
            return clean.ToString();
        }

        /// <summary>
        /// Whether a failed chat attempt should be re-tried (or failed over to a fallback backend): a
        /// transport/timeout/transient-HTTP failure is retryable; a deterministic one (a non-transient
        /// <see cref="AiBackendHttpException"/> such as 400/401/redirect, or a bad-request
        /// <see cref="ArgumentException"/>) is not. A caller-requested cancellation is never retryable.
        /// Shared by <see cref="AiBrain.ChatWithRetryForDiagnosticsAsync"/> and the fallback backend so both
        /// classify failures identically. Order matters: <see cref="AiBackendHttpException"/> derives from
        /// <see cref="HttpRequestException"/>, so it is tested first.
        /// </summary>
        public static bool IsRetryable(Exception ex, CancellationToken ct)
        {
            if (ex == null) return false;
            if (ct.IsCancellationRequested) return false;
            if (ex is TaskCanceledException) return true;   // HttpClient self-timeout (caller-cancel excluded above)
            if (ex is TimeoutException) return true;         // AiEndpointPolicy end-to-end deadline
            AiBackendHttpException http = ex as AiBackendHttpException;
            if (http != null) return http.IsTransient;
            if (ex is HttpRequestException) return true;     // transport-level (non-AiBackendHttpException)
            return false;
        }

        public static bool IsLoopbackEndpoint(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && IsLoopback(uri);
        }

        public static TimeSpan ValidateDeadline(TimeSpan deadline, string parameterName)
        {
            if (deadline <= TimeSpan.Zero ||
                deadline.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName);
            return deadline;
        }

        public static Task<bool> SendAndCheckSuccessAsync(
            HttpClient client,
            HttpRequestMessage request,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            return SendWithDeadlineAsync(
                client,
                request,
                deadline,
                cancellationToken,
                delegate(HttpResponseMessage response, CancellationToken boundedToken)
                {
                    EnsureNotRedirect(response);
                    return Task.FromResult(response.IsSuccessStatusCode);
                });
        }

        /// <summary>
        /// True when the endpoint ANSWERED with anything but a redirect, whatever the status: a 401 or a 404 is
        /// a server that is there and says no. For a reachability probe whose caller separates "is it there"
        /// from "did it accept the request" (<see cref="OpenAiCompatBackend.IsAvailableAsync"/>, F107). Not for
        /// OllamaClient, whose EnsureServerAsync uses a SUCCESS probe to decide whether to launch the server.
        /// </summary>
        public static Task<bool> SendAndCheckAnsweredAsync(
            HttpClient client,
            HttpRequestMessage request,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            return SendWithDeadlineAsync(
                client,
                request,
                deadline,
                cancellationToken,
                delegate(HttpResponseMessage response, CancellationToken boundedToken)
                {
                    EnsureNotRedirect(response);
                    return Task.FromResult(true);
                });
        }

        public static Task<bool> SendAndEnsureSuccessAsync(
            HttpClient client,
            HttpRequestMessage request,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            return SendWithDeadlineAsync(
                client,
                request,
                deadline,
                cancellationToken,
                async delegate(HttpResponseMessage response, CancellationToken boundedToken)
                {
                    await EnsureSuccessAsync(response, boundedToken).ConfigureAwait(false);
                    return true;
                });
        }

        public static Task<string> SendAndReadResponseStringAsync(
            HttpClient client,
            HttpRequestMessage request,
            TimeSpan deadline,
            CancellationToken cancellationToken,
            int maximumBytes = MaximumResponseBytes)
        {
            if (maximumBytes < 1)
                throw new ArgumentOutOfRangeException("maximumBytes");

            return SendWithDeadlineAsync(
                client,
                request,
                deadline,
                cancellationToken,
                async delegate(
                    HttpResponseMessage response,
                    CancellationToken boundedToken)
                {
                    await EnsureSuccessAsync(response, boundedToken).ConfigureAwait(false);
                    return await ReadResponseStringAsync(
                        response.Content,
                        boundedToken,
                        maximumBytes).ConfigureAwait(false);
                });
        }

        public static async Task<string> ReadResponseStringAsync(
            HttpContent content,
            CancellationToken cancellationToken,
            int maximumBytes = MaximumResponseBytes)
        {
            if (content == null) return "";
            if (maximumBytes < 1) throw new ArgumentOutOfRangeException("maximumBytes");
            if (content.Headers.ContentLength.HasValue &&
                content.Headers.ContentLength.Value > maximumBytes)
                throw new InvalidDataException("AI response exceeds its size limit.");

            Task<Stream> sourceTask = content.ReadAsStreamAsync();
            using (Stream source = await AwaitStreamWithCancellationRaceAsync(
                sourceTask,
                cancellationToken).ConfigureAwait(false))
            using (var destination = new MemoryStream(
                content.Headers.ContentLength.HasValue
                    ? (int)Math.Min(maximumBytes, content.Headers.ContentLength.Value)
                    : 8192))
            {
                byte[] buffer = new byte[8192];
                int total = 0;
                while (true)
                {
                    int read = await ReadWithCancellationRaceAsync(
                        source,
                        buffer,
                        0,
                        buffer.Length,
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    total = checked(total + read);
                    if (total > maximumBytes)
                        throw new InvalidDataException("AI response exceeds its size limit.");
                    destination.Write(buffer, 0, read);
                }
                return StrictUtf8.GetString(destination.ToArray());
            }
        }

        private static async Task<Stream> AwaitStreamWithCancellationRaceAsync(
            Task<Stream> stream,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.IsCompleted)
                return await stream.ConfigureAwait(false);

            var cancellation = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                delegate { cancellation.TrySetResult(true); }))
            {
                Task completed = await Task.WhenAny(
                    stream,
                    cancellation.Task).ConfigureAwait(false);
                if (completed == stream)
                    return await stream.ConfigureAwait(false);

                DisposeLateStreamAndObserveFailure(stream);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        // internal, not private: the self-test drives it with a consumer that finishes after the deadline (F081).
        internal static async Task<TResult> SendWithDeadlineAsync<TResult>(
            HttpClient client,
            HttpRequestMessage request,
            TimeSpan deadline,
            CancellationToken cancellationToken,
            Func<HttpResponseMessage, CancellationToken, Task<TResult>> consumeResponse)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (request == null) throw new ArgumentNullException("request");
            if (consumeResponse == null)
                throw new ArgumentNullException("consumeResponse");
            ValidateDeadline(deadline, "deadline");

            using (var deadlineCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadlineCancellation.CancelAfter(deadline);
                CancellationToken boundedToken = deadlineCancellation.Token;
                try
                {
                    Task<HttpResponseMessage> send = client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        boundedToken);
                    using (HttpResponseMessage response =
                        await AwaitResponseWithCancellationRaceAsync(
                            send,
                            boundedToken).ConfigureAwait(false))
                    {
                        TResult result = await consumeResponse(
                            response,
                            boundedToken).ConfigureAwait(false);
                        // No cancellation check AFTER the consumer. Every consumer observes boundedToken while it
                        // reads, so a result it hands back is complete; the check that used to sit here could only
                        // discard a finished answer, when the deadline fired between the last read and the return,
                        // turning it into a TimeoutException the retry then paid a second full generation for (F081).
                        return result;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!cancellationToken.IsCancellationRequested &&
                        deadlineCancellation.IsCancellationRequested)
                        throw new TimeoutException(
                            "AI request exceeded its end-to-end deadline.");
                    throw;
                }
            }
        }

        private static async Task<HttpResponseMessage>
            AwaitResponseWithCancellationRaceAsync(
                Task<HttpResponseMessage> response,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (response.IsCompleted)
                return await response.ConfigureAwait(false);

            var cancellation = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                delegate { cancellation.TrySetResult(true); }))
            {
                Task completed = await Task.WhenAny(
                    response,
                    cancellation.Task).ConfigureAwait(false);
                if (completed == response)
                    return await response.ConfigureAwait(false);

                DisposeLateResponseAndObserveFailure(response);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private static async Task<int> ReadWithCancellationRaceAsync(
            Stream source,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<int> read = source.ReadAsync(
                buffer,
                offset,
                count,
                cancellationToken);
            if (read.IsCompleted)
                return await read.ConfigureAwait(false);

            var cancellation = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                delegate { cancellation.TrySetResult(true); }))
            {
                Task completed = await Task.WhenAny(
                    read,
                    cancellation.Task).ConfigureAwait(false);
                if (completed == read)
                    return await read.ConfigureAwait(false);

                // The enclosing response/stream scopes dispose the transport while this abandoned
                // read is pending. Observe its later disposal/network fault so it cannot become an
                // unobserved task exception.
                ObserveTaskFailure(read);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private static void DisposeLateResponseAndObserveFailure(
            Task<HttpResponseMessage> response)
        {
            if (response == null) return;
            response.ContinueWith(
                task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                        task.Result.Dispose();
                    else if (task.IsFaulted)
                    {
                        var ignored = task.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void DisposeLateStreamAndObserveFailure(Task<Stream> stream)
        {
            if (stream == null) return;
            stream.ContinueWith(
                task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                        task.Result.Dispose();
                    else if (task.IsFaulted)
                    {
                        var ignored = task.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>Observe a task's fault so an abandoned task never surfaces as an unobserved-task exception.
        /// The engine's ONE copy: AiBrain (the tesseract pipe reads) and AiSessionManager (a timed-out unload)
        /// carried private copies until F064 consolidated them here (2026-09-30), and FallbackBackend (an
        /// abandoned probe) and OllamaClient (an abandoned server starter) two more until RA-086 the same day. A
        /// source invariant (tests/runtime-hardening-selftest.ps1) counts the OnlyOnFaulted continuation once in
        /// this directory, so the next private copy fails the gate by name.</summary>
        internal static void ObserveTaskFailure(Task task)
        {
            if (task == null) return;
            task.ContinueWith(
                completed =>
                {
                    var ignored = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static bool IsLoopback(Uri uri)
        {
            if (uri.IsLoopback) return true;

            IPAddress address;
            return IPAddress.TryParse(uri.Host, out address) && IPAddress.IsLoopback(address);
        }
    }

    /// <summary>
    /// Preserves an HTTP status code as an int. (net10's HttpRequestException exposes its own
    /// nullable HttpStatusCode; this keeps the app's existing int-based retry logic, so the member
    /// intentionally shadows the base one.) AiBrain uses this to retry only transient failures
    /// instead of repeating deterministic 4xx requests.
    /// </summary>
    internal sealed class AiBackendHttpException : HttpRequestException
    {
        public new int StatusCode { get; private set; }
        public bool IsTransient { get; private set; }

        /// <summary>The provider's own error text, bounded and sanitised, or "" when it sent none. For the
        /// PANE only: the diagnostic log records the status category (F080, F107).</summary>
        public string ProviderMessage { get; private set; }

        public AiBackendHttpException(int statusCode, bool isTransient)
            : this(statusCode, isTransient, "")
        {
        }

        public AiBackendHttpException(int statusCode, bool isTransient, string providerMessage)
            : base("AI backend returned HTTP " + statusCode + ".")
        {
            StatusCode = statusCode;
            IsTransient = isTransient;
            ProviderMessage = providerMessage ?? "";
        }
    }
}
