using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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

        public static void EnsureSuccess(HttpResponseMessage response)
        {
            EnsureNotRedirect(response);
            if (response.IsSuccessStatusCode) return;

            int statusCode = (int)response.StatusCode;
            bool transient =
                statusCode == 408 ||
                statusCode == 429 ||
                statusCode >= 500;
            throw new AiBackendHttpException(statusCode, transient);
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
                delegate(HttpResponseMessage response, CancellationToken boundedToken)
                {
                    EnsureSuccess(response);
                    return Task.FromResult(true);
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
                    EnsureSuccess(response);
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

        private static async Task<TResult> SendWithDeadlineAsync<TResult>(
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
                        boundedToken.ThrowIfCancellationRequested();
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

        private static void ObserveTaskFailure(Task task)
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

        public AiBackendHttpException(int statusCode, bool isTransient)
            : base("AI backend returned HTTP " + statusCode + ".")
        {
            StatusCode = statusCode;
            IsTransient = isTransient;
        }
    }
}
