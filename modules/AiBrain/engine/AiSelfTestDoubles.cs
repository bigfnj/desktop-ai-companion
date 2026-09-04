using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;

namespace DesktopAICompanion.AiBrainModule
{
    // Test doubles for the relocated AI security probes (AiEngineProbe.Security.cs). These are the
    // module's own copies of the HTTP-handler and backend fakes the base uses in SecuritySelfTest.cs,
    // brought over so the assertions exercise the SHIPPING module engine (DesktopAICompanion.Ai.*) rather than
    // the base's about-to-be-deleted duplicate. None of them touch the network or a live LLM.

    /// <summary>Returns a fixed 200 OK + a given JSON body for any request. Drives the offline
    /// ListModelsAsync parse tests (OllamaClient's /api/tags, OpenAiCompatBackend's /models).</summary>
    internal sealed class FixedJsonResponseHandler : HttpMessageHandler
    {
        private readonly string _json;
        public FixedJsonResponseHandler(string json) { _json = json; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Answers with fixed JSON and KEEPS the request body and path, so a test can assert what was actually
    /// sent. Needed because the interesting property of the keep_alive setting is a field in the outgoing
    /// payload -- something a response-only double cannot see.
    /// </summary>
    internal sealed class CapturingJsonHandler : HttpMessageHandler
    {
        private readonly string _json;
        public string LastBody { get; private set; }
        public string LastPath { get; private set; }
        public CapturingJsonHandler(string json) { _json = json; LastBody = ""; LastPath = ""; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri != null ? request.RequestUri.AbsolutePath : "";
            LastBody = request.Content != null
                ? await request.Content.ReadAsStringAsync().ConfigureAwait(false)
                : "";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Blocks on response headers until the supplied token is canceled.</summary>
    internal sealed class BlockingHeadersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken)
                .ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Reports the server unavailable on the first probe, then blocks (honoring cancellation) on every
    /// subsequent request. Drives the Ollama startup-deadline probes.
    /// </summary>
    internal sealed class FirstUnavailableThenBlockingHandler : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount
        {
            get { return Volatile.Read(ref requestCount); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requestCount) == 1)
                return new HttpResponseMessage(
                    HttpStatusCode.ServiceUnavailable);

            await Task.Delay(Timeout.Infinite, cancellationToken)
                .ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>Returns headers immediately but a body stream whose reads never complete.</summary>
    internal sealed class BlockingBodyHandler : HttpMessageHandler
    {
        private BlockingReadStream _stream;

        public bool StreamDisposed
        {
            get { return _stream != null && _stream.IsDisposed; }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _stream = new BlockingReadStream();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(_stream)
            });
        }
    }

    /// <summary>Returns a content whose read-stream acquisition never completes.</summary>
    internal sealed class BlockingReadAsStreamHandler : HttpMessageHandler
    {
        private BlockingReadAsStreamContent _content;

        public bool ContentDisposed
        {
            get { return _content != null && _content.IsDisposed; }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _content = new BlockingReadAsStreamContent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = _content
            });
        }
    }

    internal sealed class BlockingReadAsStreamContent : HttpContent
    {
        private readonly TaskCompletionSource<Stream> _pending =
            new TaskCompletionSource<Stream>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            // .NET Framework exposes no cancellation token for this operation.
            return _pending.Task;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext context)
        {
            return Task.FromException(
                new NotSupportedException("Serialization is not used by this test."));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            if (disposing)
                _pending.TrySetException(
                    new ObjectDisposedException("BlockingReadAsStreamContent"));
            base.Dispose(disposing);
        }
    }

    internal sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _pending =
            new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            // Deliberately ignore cancellation to reproduce .NET Framework transport streams
            // that leave ReadAsync pending after the supplied token has been canceled.
            return _pending.Task;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            if (disposing)
                _pending.TrySetException(
                    new ObjectDisposedException("BlockingReadStream"));
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Records unload/dispose so retirement drains can be observed.</summary>
    internal sealed class RetirementTrackingBackend : ICompanionBrainBackend
    {
        private int unloadCalls;
        private int disposeCount;

        public int UnloadCalls
        {
            get { return Volatile.Read(ref unloadCalls); }
        }

        public int DisposeCount
        {
            get { return Volatile.Read(ref disposeCount); }
        }

        public Task<string> ChatAsync(
            string model,
            IList<ChatMessage> messages,
            bool jsonFormat,
            CancellationToken cancellationToken)
        {
            return Task.FromResult("");
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<bool> EnsureServerAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task WarmUpAsync(
            string model,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UnloadAsync(
            string model,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref unloadCalls);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
        }
    }

    /// <summary>An unload that never completes, to bound cancellation-ignoring retirement.</summary>
    internal sealed class CancellationIgnoringBackend : ICompanionBrainBackend
    {
        private readonly TaskCompletionSource<bool> never =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> ChatAsync(
            string model,
            IList<ChatMessage> messages,
            bool jsonFormat,
            CancellationToken cancellationToken)
        {
            return Task.FromResult("");
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<bool> EnsureServerAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task WarmUpAsync(
            string model,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UnloadAsync(
            string model,
            CancellationToken cancellationToken)
        {
            return never.Task;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Fails every chat with a non-transient (redirect) status to prove no retry occurs.</summary>
    internal sealed class DeterministicFailureBackend : ICompanionBrainBackend
    {
        public int ChatCalls { get; private set; }

        public Task<string> ChatAsync(
            string model,
            IList<ChatMessage> messages,
            bool jsonFormat,
            CancellationToken cancellationToken)
        {
            ChatCalls++;
            throw new AiBackendHttpException(302, false);
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<bool> EnsureServerAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task WarmUpAsync(string model, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UnloadAsync(string model, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Fails every chat with a TRANSIENT status (503), to drive the fallback path.</summary>
    internal sealed class TransientFailBackend : ICompanionBrainBackend
    {
        public int ChatCalls { get; private set; }
        public Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            ChatCalls++;
            throw new AiBackendHttpException(503, true);
        }
        public Task<bool> IsAvailableAsync(CancellationToken ct) { return Task.FromResult(false); }
        public Task<bool> EnsureServerAsync(CancellationToken ct) { return Task.FromResult(false); }
        public Task WarmUpAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public void Dispose() { }
    }

    /// <summary>Records the model it was last asked for and returns a canned reply; availability is configurable.
    /// Used as the LOCAL leg of a FallbackBackend to observe whether (and with which model) it was invoked.</summary>
    internal sealed class RecordingBackend : ICompanionBrainBackend
    {
        private readonly string _reply;
        private readonly bool _available;
        public RecordingBackend(string reply, bool available) { _reply = reply; _available = available; }
        public int ChatCalls { get; private set; }
        public string LastModel { get; private set; }
        public Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            ChatCalls++;
            LastModel = model;
            return Task.FromResult(_reply);
        }
        public Task<bool> IsAvailableAsync(CancellationToken ct) { return Task.FromResult(_available); }
        public Task<bool> EnsureServerAsync(CancellationToken ct) { return Task.FromResult(_available); }
        public Task WarmUpAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public void Dispose() { }
    }
}
