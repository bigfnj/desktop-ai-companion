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
            // The parameterless override is the one that runs: the engine reads through the parameterless
            // content.ReadAsStreamAsync() (AiEndpointPolicy), and the token overload's default delegates here.
            // That call shape is what this double pins, not a runtime-era quirk (F090).
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
            // Deliberately ignores cancellation, standing in for a transport (a third-party or misbehaving
            // HttpMessageHandler) whose ReadAsync stays pending after the token is cancelled: the WhenAny races
            // and late-disposal helpers in AiEndpointPolicy exist for exactly that transport, whatever runtime
            // the process runs on (F090).
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
            // Counted, not thrown. The shipping OllamaClient would answer an unload after Dispose with
            // ObjectDisposedException from its disposed HttpClient (swallowed by its own catch-all, so
            // nothing is sent); this double stayed silent about the ordering and let a check certify an
            // eviction the real backend cannot perform (F089). Recording it lets the check say what
            // actually happened; throwing here would pre-empt the manager-ordering fix (F091).
            if (Volatile.Read(ref disposeCount) > 0) Interlocked.Increment(ref unloadCallsAfterDispose);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
        }

        private int unloadCallsAfterDispose;
        /// <summary>Unloads that arrived AFTER Dispose, which the real backend would drop.</summary>
        public int UnloadCallsAfterDispose { get { return Volatile.Read(ref unloadCallsAfterDispose); } }
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

    /// <summary>
    /// Answers the SAME thing every time, whatever it is asked.
    ///
    /// Stands in for the failure the repeat guard exists for: a model that has decided what it thinks
    /// about a screen and will say it again however firmly the prompt asks for something new. Proves the
    /// guard retries once, stops, and still lets the companion speak rather than going silent.
    /// </summary>
    internal sealed class FixedReplyBackend : ICompanionBrainBackend
    {
        private readonly string _reply;
        public FixedReplyBackend(string reply) { _reply = reply; }
        public int ChatCalls { get; private set; }
        /// <summary>The user turn of the most recent request, for asserting what the retry was told.</summary>
        public string LastUserContent { get; private set; }

        public Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            ChatCalls++;
            LastUserContent = "";
            if (messages != null)
                foreach (ChatMessage m in messages)
                    if (m != null && m.Role == "user") LastUserContent = m.Content ?? "";
            return Task.FromResult(_reply);
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct) { return Task.FromResult(true); }
        public Task<bool> EnsureServerAsync(CancellationToken ct) { return Task.FromResult(true); }
        public Task WarmUpAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public void Dispose() { }
    }

    /// <summary>
    /// Records the USER content of every chat it is asked for, and answers with a distinct remark each
    /// time.
    ///
    /// Exists for the audition's anti-repetition check. RecordingBackend keeps the model and a call
    /// count, which cannot show whether the previous remarks were fed back into the next prompt -- and
    /// that is the whole mechanism, because the system prompt's "do not repeat anything you have said
    /// recently" is inert unless something in the context says what was said.
    /// </summary>
    internal sealed class MessageRecordingBackend : ICompanionBrainBackend
    {
        private readonly List<string> _userContent = new List<string>();
        public int ChatCalls { get; private set; }
        /// <summary>The user turn of each request, in order.</summary>
        public IReadOnlyList<string> UserContent { get { return _userContent; } }
        /// <summary>True when a request carried at least one image.</summary>
        public bool SawImage { get; private set; }

        public Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            ChatCalls++;
            string user = "";
            if (messages != null)
                foreach (ChatMessage m in messages)
                {
                    if (m == null) continue;
                    if (m.ImagesBase64 != null && m.ImagesBase64.Length > 0) SawImage = true;
                    if (m.Role == "user") user = m.Content ?? "";
                }
            _userContent.Add(user);
            // A distinct, recognisable remark per call, so a later prompt containing an earlier one is
            // unambiguous rather than a coincidence of shared wording.
            return Task.FromResult(
                "{\"text\":\"REMARK-NUMBER-" + ChatCalls + "\",\"emotion\":\"neutral\"}");
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct) { return Task.FromResult(true); }
        public Task<bool> EnsureServerAsync(CancellationToken ct) { return Task.FromResult(true); }
        public Task WarmUpAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public void Dispose() { }
    }

    /// <summary>
    /// Honours the cancellation token: throws <see cref="OperationCanceledException"/> from the chat.
    ///
    /// Needed because <see cref="CancellationIgnoringBackend"/> deliberately does the opposite (it
    /// returns a reply regardless) and so cannot exercise the cancel path at all. The distinction
    /// matters for the request-outcome diagnostics: AiEndpointPolicy.IsRetryable returns false once the
    /// token is cancelled, so a cancel takes the same branch a deterministic failure does, and only an
    /// explicit rethrow keeps it from being recorded as a request failure.
    /// </summary>
    internal sealed class CancellationHonouringBackend : ICompanionBrainBackend
    {
        public int ChatCalls { get; private set; }

        public Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            ChatCalls++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("");
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct) { return Task.FromResult(true); }
        public Task<bool> EnsureServerAsync(CancellationToken ct) { return Task.FromResult(true); }
        public Task WarmUpAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public void Dispose() { }
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
    /// Used as the LOCAL leg of a FallbackBackend to observe whether (and with which model) it was invoked.
    /// Also counts warm-ups and records unloads, so a check can say what a retirement or a preparation
    /// actually asked of the backend.</summary>
    internal sealed class RecordingBackend : ICompanionBrainBackend
    {
        private readonly string _reply;
        private bool _available;
        public RecordingBackend(string reply, bool available) { _reply = reply; _available = available; }
        /// <summary>Flip reachability mid-check: the F071 probe takes the backend down and brings it back.</summary>
        public bool Available { get { return _available; } set { _available = value; } }
        public int ChatCalls { get; private set; }
        public string LastModel { get; private set; }
        public int WarmUpCalls { get; private set; }
        /// <summary>The model the most recent WarmUpAsync named, so a check can say WHICH local model a composite
        /// pinned, not only that it pinned one (RA-087).</summary>
        public string LastWarmUpModel { get; private set; }
        private int _ensureServerCalls;
        /// <summary>How many times EnsureServerAsync was asked; read from another thread by the F105 check.</summary>
        public int EnsureServerCalls { get { return Volatile.Read(ref _ensureServerCalls); } }
        /// <summary>Every model an UnloadAsync named, in order.</summary>
        public List<string> UnloadedModels { get; } = new List<string>();
        public Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            ChatCalls++;
            LastModel = model;
            return Task.FromResult(_reply);
        }
        public Task<bool> IsAvailableAsync(CancellationToken ct) { return Task.FromResult(_available); }
        public Task<bool> EnsureServerAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _ensureServerCalls);
            return Task.FromResult(_available);
        }
        public Task WarmUpAsync(string model, CancellationToken ct) { WarmUpCalls++; LastWarmUpModel = model; return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { UnloadedModels.Add(model ?? ""); return Task.CompletedTask; }
        public void Dispose() { }
    }

    /// <summary>Answers every request with one fixed status and body: a server that is REACHABLE and says no,
    /// which is the 401/402/404 shape F107 and F080 are about.</summary>
    internal sealed class FixedStatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int Requests { get; private set; }
        public FixedStatusHandler(HttpStatusCode status, string body) { _status = status; _body = body ?? ""; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>The transport failing before any answer: connection refused, no such host, a dropped socket.</summary>
    internal sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("connection refused (double)");
        }
    }

    /// <summary>
    /// Counts every call the brain makes on it, for the Remembrance stand-down checks (AiEngineProbe.Module.cs): what
    /// reaches a backend while Remembrance is busy, and that nothing is warmed or evicted then. Interlocked counters,
    /// because the session calls it from pool threads while the check reads on its own. Answers NOT reachable unless
    /// <see cref="Available"/> is set, so an ask that does reach it ends at the probe, before any capture or OCR:
    /// that is what keeps those checks free of a screen. A preparation warms only a backend that answered up, so the
    /// warm-up check sets it.
    /// </summary>
    internal sealed class StandDownProbeBackend : ICompanionBrainBackend
    {
        private int _availabilityCalls;
        private int _ensureServerCalls;
        private int _warmUpCalls;
        private int _chatCalls;
        private int _unloadCalls;
        private volatile bool _available;

        /// <summary>What the reachability probe and the server start answer. False by default.</summary>
        public bool Available { get { return _available; } set { _available = value; } }

        public int AvailabilityCalls { get { return Volatile.Read(ref _availabilityCalls); } }
        public int EnsureServerCalls { get { return Volatile.Read(ref _ensureServerCalls); } }
        public int WarmUpCalls { get { return Volatile.Read(ref _warmUpCalls); } }
        public int ChatCalls { get { return Volatile.Read(ref _chatCalls); } }
        public int UnloadCalls { get { return Volatile.Read(ref _unloadCalls); } }

        /// <summary>Every call that could load a model or wake a server. An unload is an eviction and is not one.</summary>
        public int Requests { get { return AvailabilityCalls + EnsureServerCalls + WarmUpCalls + ChatCalls; } }

        public Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _availabilityCalls);
            return Task.FromResult(_available);
        }

        public Task<bool> EnsureServerAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _ensureServerCalls);
            return Task.FromResult(_available);
        }

        public Task WarmUpAsync(string model, CancellationToken ct)
        {
            Interlocked.Increment(ref _warmUpCalls);
            return Task.CompletedTask;
        }

        public Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            Interlocked.Increment(ref _chatCalls);
            return Task.FromResult("");
        }

        public Task UnloadAsync(string model, CancellationToken ct)
        {
            Interlocked.Increment(ref _unloadCalls);
            return Task.CompletedTask;
        }

        // Shared by every brain a check's module builds (an Apply rebuilds one), so disposing a retired brain must
        // not end the double's life.
        public void Dispose() { }
    }

    /// <summary>A leg whose reachability and readiness probes hang until <see cref="Release"/>: the cloud whose
    /// traffic is silently dropped, which F105 is about. Chats answer at once so nothing else blocks.</summary>
    internal sealed class HangingBackend : ICompanionBrainBackend
    {
        private readonly TaskCompletionSource<bool> _gate =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ensureServerCalls;
        public int EnsureServerCalls { get { return Volatile.Read(ref _ensureServerCalls); } }
        public Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct) { return Task.FromResult(""); }
        public Task<bool> IsAvailableAsync(CancellationToken ct) { return _gate.Task; }
        public Task<bool> EnsureServerAsync(CancellationToken ct) { Interlocked.Increment(ref _ensureServerCalls); return _gate.Task; }
        public Task WarmUpAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        /// <summary>Let every pending probe complete with this answer.</summary>
        public void Release(bool up) { _gate.TrySetResult(up); }
        public void Dispose() { }
    }
}
