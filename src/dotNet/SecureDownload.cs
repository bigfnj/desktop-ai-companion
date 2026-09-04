using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion
{
    /// <summary>Bounded, hash-verified download primitives shared by pet and fortune catalogs.</summary>
    internal static class SecureDownload
    {
        private static readonly Regex SafeIdPattern =
            new Regex(@"\A[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9])?\z",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex CommitPattern =
            new Regex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex RefPattern =
            new Regex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant);

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly TimeSpan DownloadDeadline = TimeSpan.FromSeconds(60);

        public static bool TryValidatePinnedRawGitHubUrl(
            string value,
            string owner,
            string repository,
            out Uri uri,
            out string error)
        {
            uri = null;
            error = null;

            Uri candidate;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out candidate))
            {
                error = "Catalog URL is not an absolute URI.";
                return false;
            }

            if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                !candidate.IsDefaultPort ||
                !string.IsNullOrEmpty(candidate.UserInfo) ||
                !string.IsNullOrEmpty(candidate.Query) ||
                !string.IsNullOrEmpty(candidate.Fragment))
            {
                error = "Catalog assets must use a direct HTTPS raw.githubusercontent.com URL.";
                return false;
            }

            string[] parts = candidate.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 4 ||
                !string.Equals(parts[0], owner, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parts[1], repository, StringComparison.OrdinalIgnoreCase) ||
                !CommitPattern.IsMatch(parts[2]))
            {
                error = "Catalog assets must be pinned to a full 40-character commit.";
                return false;
            }

            uri = candidate;
            return true;
        }

        /// <summary>
        /// Validate a direct raw.githubusercontent.com URL whose ref is a branch or tag (or a commit)
        /// rather than a pinned commit. Used for the runtime-fetched catalog and the assets it lists,
        /// whose integrity is anchored by a separate SHA-256 rather than by an immutable commit pin.
        /// </summary>
        public static bool TryValidateBranchRawGitHubUrl(
            string value,
            string owner,
            string repository,
            out Uri uri,
            out string error)
        {
            uri = null;
            error = null;

            Uri candidate;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out candidate))
            {
                error = "Catalog URL is not an absolute URI.";
                return false;
            }

            if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                !candidate.IsDefaultPort ||
                !string.IsNullOrEmpty(candidate.UserInfo) ||
                !string.IsNullOrEmpty(candidate.Query) ||
                !string.IsNullOrEmpty(candidate.Fragment))
            {
                error = "Catalog assets must use a direct HTTPS raw.githubusercontent.com URL.";
                return false;
            }

            string[] parts = candidate.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 4 ||
                !string.Equals(parts[0], owner, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parts[1], repository, StringComparison.OrdinalIgnoreCase) ||
                !RefPattern.IsMatch(parts[2]))
            {
                error = "Catalog assets must be hosted at owner/repo/<branch>/... on raw.githubusercontent.com.";
                return false;
            }

            foreach (string segment in parts)
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    error = "Catalog asset URL contains an invalid path segment.";
                    return false;
                }

            uri = candidate;
            return true;
        }

        public static bool IsSafeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !SafeIdPattern.IsMatch(value)) return false;
            string trimmed = value.Trim();
            if (trimmed == "." || trimmed == "..") return false;

            string name = trimmed.Split('.')[0].ToUpperInvariant();
            switch (name)
            {
                case "CON":
                case "PRN":
                case "AUX":
                case "NUL":
                case "COM1":
                case "COM2":
                case "COM3":
                case "COM4":
                case "COM5":
                case "COM6":
                case "COM7":
                case "COM8":
                case "COM9":
                case "LPT1":
                case "LPT2":
                case "LPT3":
                case "LPT4":
                case "LPT5":
                case "LPT6":
                case "LPT7":
                case "LPT8":
                case "LPT9":
                    return false;
                default:
                    return true;
            }
        }

        public static string ResolveContainedFile(string root, string id, string extension)
        {
            if (!IsSafeId(id)) throw new InvalidDataException("Unsafe catalog item id.");
            if (string.IsNullOrEmpty(extension) || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Unsafe catalog file extension.");

            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, id + extension));
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Catalog destination escapes the data directory.");
            return fullPath;
        }

        public static async Task<byte[]> DownloadBytesAsync(Uri uri, int maximumBytes, CancellationToken ct)
        {
            if (uri == null) throw new ArgumentNullException("uri");
            if (maximumBytes < 1) throw new ArgumentOutOfRangeException("maximumBytes");

            using (var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                return await DownloadBytesAsync(
                    handler,
                    request,
                    maximumBytes,
                    DownloadDeadline,
                    ct).ConfigureAwait(false);
            }
        }

        internal static async Task<byte[]> DownloadBytesAsync(
            HttpMessageHandler handler,
            HttpRequestMessage request,
            int maximumBytes,
            TimeSpan deadline,
            CancellationToken ct)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (request == null) throw new ArgumentNullException("request");
            if (maximumBytes < 1) throw new ArgumentOutOfRangeException("maximumBytes");
            if (deadline <= TimeSpan.Zero ||
                deadline > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException("deadline");

            using (var deadlineCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var client = new HttpClient(handler, false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            })
            {
                deadlineCancellation.CancelAfter(deadline);
                CancellationToken boundedToken = deadlineCancellation.Token;
                if (!request.Headers.Contains("User-Agent"))
                    request.Headers.Add("User-Agent", "DesktopAICompanion");
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
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status <= 399)
                            throw new HttpRequestException("Catalog redirects are not permitted.");
                        response.EnsureSuccessStatusCode();

                        if (response.Content.Headers.ContentLength.HasValue &&
                            response.Content.Headers.ContentLength.Value > maximumBytes)
                            throw new InvalidDataException(
                                "Catalog response exceeds its size limit.");

                        Task<Stream> stream = response.Content.ReadAsStreamAsync();
                        using (Stream source =
                            await AwaitStreamWithCancellationRaceAsync(
                                stream,
                                boundedToken).ConfigureAwait(false))
                        using (var destination = new MemoryStream(
                            Math.Min(maximumBytes, response.Content.Headers.ContentLength.HasValue
                                ? (int)Math.Min(
                                    maximumBytes,
                                    response.Content.Headers.ContentLength.Value)
                                : 8192)))
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
                                    boundedToken).ConfigureAwait(false);
                                if (read == 0) break;
                                total = checked(total + read);
                                if (total > maximumBytes)
                                    throw new InvalidDataException(
                                        "Catalog response exceeds its size limit.");
                                destination.Write(buffer, 0, read);
                            }
                            return destination.ToArray();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!ct.IsCancellationRequested &&
                        deadlineCancellation.IsCancellationRequested)
                        throw new TimeoutException(
                            "Catalog download exceeded its end-to-end deadline.");
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

                // The enclosing response/stream using scopes dispose the transport while this
                // abandoned read is still pending. Observe a later disposal/network fault so it
                // cannot become an unobserved task exception.
                ObserveReadFailure(read);
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

        private static void ObserveReadFailure(Task read)
        {
            if (read == null) return;
            read.ContinueWith(
                task =>
                {
                    var ignored = task.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public static string DecodeUtf8(byte[] bytes)
        {
            return StrictUtf8.GetString(bytes ?? new byte[0]);
        }

        public static void RequireSha256(byte[] bytes, string expectedHex)
        {
            if (string.IsNullOrWhiteSpace(expectedHex) ||
                expectedHex.Length != 64 ||
                !FixedTimeEquals(Sha256Hex(bytes), expectedHex.Trim().ToLowerInvariant()))
                throw new InvalidDataException("Downloaded content failed SHA-256 verification.");
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes ?? new byte[0]);
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        public static void WriteAllBytesAtomic(string destination, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(destination));
            Directory.CreateDirectory(directory);

            string temporary = Path.Combine(directory, "." + Path.GetFileName(destination) +
                "." + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = temporary + ".bak";
            try
            {
                File.WriteAllBytes(temporary, bytes ?? new byte[0]);
                if (File.Exists(destination))
                {
                    File.Replace(temporary, destination, backup, true);
                    try { File.Delete(backup); } catch { }
                }
                else
                {
                    File.Move(temporary, destination);
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            }
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
