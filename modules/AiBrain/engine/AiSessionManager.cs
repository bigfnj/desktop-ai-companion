using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;   // ABI ScreenContext (replaces the base ScreenCaptureContext)

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Owns one AI configuration generation. Preparation, requests, reload, unload, and disposal are
    /// serialized so a response from an obsolete configuration can never be applied to the UI.
    /// </summary>
    internal sealed class AiSessionManager : IDisposable
    {
        private readonly object _stateLock = new object();
        private readonly SemaphoreSlim _operation = new SemaphoreSlim(1, 1);
        private readonly Queue<Action> _pendingAfterRetire = new Queue<Action>();

        private CancellationTokenSource _generationCancellation = new CancellationTokenSource();
        private Func<AiBrain> _factory;
        private AiBrain _brain;
        private int _generation;
        private int _askActive;
        private int _cleanupStarted;
        private bool _enabled;
        private bool _disposed;

        internal Action ReconfigureAdmittedForDiagnostics
        {
            get;
            set;
        }

        public bool Enabled
        {
            get { lock (_stateLock) return _enabled && !_disposed; }
        }

        public bool RequestInProgress
        {
            get { return Volatile.Read(ref _askActive) != 0; }
        }

        public async Task<bool> ReconfigureAsync(
            Func<AiBrain> factory,
            bool enabled,
            bool prepare,
            CancellationToken externalCancellation,
            Action afterRetire = null)
        {
            int generation;
            CancellationTokenSource previous;
            CancellationTokenSource linked;

            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (afterRetire != null)
                    _pendingAfterRetire.Enqueue(afterRetire);
                previous = _generationCancellation;
                _generationCancellation = new CancellationTokenSource();
                generation = ++_generation;
                _factory = factory;
                _enabled = enabled;
                linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _generationCancellation.Token,
                    externalCancellation);
                previous.Cancel();
            }

            Action diagnostic = ReconfigureAdmittedForDiagnostics;
            if (diagnostic != null) diagnostic();

            using (previous)
            using (linked)
            {
                try
                {
                    await _operation.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                try
                {
                    if (!IsCurrent(generation, enabled)) return false;

                    await RetireBrainAsync(_brain).ConfigureAwait(false);
                    _brain = null;
                    RunPendingAfterRetire();

                    if (!IsCurrent(generation, enabled)) return false;
                    if (!enabled || factory == null) return false;

                    _brain = factory();
                    if (!IsCurrent(generation, true))
                    {
                        await RetireBrainAsync(_brain).ConfigureAwait(false);
                        _brain = null;
                        return false;
                    }
                    if (!prepare) return true;

                    bool ready = await _brain.PrepareAsync(linked.Token).ConfigureAwait(false);
                    return ready && IsCurrent(generation, true);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                finally
                {
                    _operation.Release();
                }
            }
        }

        /// <summary>
        /// Ask the live brain to release its model from VRAM, WITHOUT retiring the session.
        ///
        /// Distinct from <c>RetireBrainAsync</c> on purpose: the session stays configured and the next ask
        /// works normally, it just pays a load. Used when a fullscreen app appears -- the point is to hand the
        /// VRAM back, not to tear the brain down.
        ///
        /// Reading <c>_brain</c> without the gate is deliberate: reference reads are atomic, this is
        /// best-effort, and taking the gate here would let a game-start stall behind an in-flight ask -- the
        /// one moment we least want to wait.
        /// </summary>
        public Task ReleaseModelAsync(CancellationToken ct)
        {
            AiBrain brain = _brain;
            if (brain == null) return Task.CompletedTask;
            try { return brain.UnloadAsync(ct); }
            catch { return Task.CompletedTask; }
        }

        public async Task<BrainResponse> AskAsync(
            ScreenContext captureContext,
            string petZone,
            bool allowVision,
            CancellationToken externalCancellation)
        {
            if (Interlocked.CompareExchange(ref _askActive, 1, 0) != 0)
                return null;

            try
            {
                int generation;
                CancellationTokenSource linked;
                lock (_stateLock)
                {
                    if (_disposed || !_enabled) return null;
                    generation = _generation;
                    linked = CancellationTokenSource.CreateLinkedTokenSource(
                        _generationCancellation.Token,
                        externalCancellation);
                }
                using (linked)
                {
                    try
                    {
                        await _operation.WaitAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                    catch (ObjectDisposedException)
                    {
                        return null;
                    }

                    try
                    {
                        if (!IsCurrent(generation, true)) return null;
                        if (_brain == null)
                        {
                            Func<AiBrain> factory;
                            lock (_stateLock) factory = _factory;
                            if (factory == null) return null;
                            _brain = factory();
                        }

                        BrainResponse response = await _brain.AskAboutScreenAsync(
                            captureContext,
                            petZone,
                            allowVision,
                            linked.Token).ConfigureAwait(false);
                        return IsCurrent(generation, true) ? response : null;
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                    finally
                    {
                        _operation.Release();
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _askActive, 0);
            }
        }

        // The generation-guarded ScreenChangedAsync wrapper lived here. Its only caller was the module's own
        // idle timer, which is gone: unprompted commentary rides the host's global drop schedule, and the
        // drop responder must answer SYNCHRONOUSLY (it returns whether it handled the tick, so Fortunes can
        // take it otherwise), which an async screen comparison cannot do without a background sampler. The
        // underlying primitive, AiBrain.ScreenChanged, is deliberately kept: it is what a future "only speak
        // when something on screen actually changed" option would be built on.

        public void Dispose()
        {
            DisposeCore(TimeSpan.FromSeconds(3));
        }

        internal void DisposeWithin(TimeSpan waitTimeout)
        {
            DisposeCore(waitTimeout);
        }

        /// <summary>
        /// Exercises the same disposal path with a bounded diagnostic wait. This keeps the
        /// deferred-cleanup regression deterministic without weakening the production timeout.
        /// </summary>
        internal void DisposeForDiagnostics(TimeSpan waitTimeout)
        {
            DisposeCore(waitTimeout);
        }

        private void DisposeCore(TimeSpan waitTimeout)
        {
            if (waitTimeout < TimeSpan.Zero)
                waitTimeout = TimeSpan.Zero;
            Stopwatch stopwatch = Stopwatch.StartNew();
            CancellationTokenSource cancellation;
            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;
                _enabled = false;
                cancellation = _generationCancellation;
                cancellation.Cancel();
            }

            bool entered = false;
            try
            {
                entered = _operation.Wait(waitTimeout);
                if (entered)
                {
                    RetireBrainAsync(
                        _brain,
                        Remaining(
                            waitTimeout,
                            stopwatch.Elapsed)).GetAwaiter().GetResult();
                    _brain = null;
                    RunPendingAfterRetire();
                    cancellation.Dispose();
                    _operation.Dispose();
                }
            }
            catch { }
            finally
            {
                if (!entered)
                {
                    // Dispose the active backend immediately to break HttpClient/process waits, then
                    // finish primitive cleanup once the serialized operation eventually unwinds.
                    AiBrain active = _brain;
                    try { if (active != null) active.Dispose(); } catch { }
                    QueueDeferredCleanup(cancellation);
                }
            }
        }

        private bool IsCurrent(int generation, bool mustBeEnabled)
        {
            lock (_stateLock)
            {
                return !_disposed &&
                       generation == _generation &&
                       (!mustBeEnabled || _enabled);
            }
        }

        private static async Task RetireBrainAsync(AiBrain brain)
        {
            await RetireBrainAsync(
                brain,
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        private static async Task RetireBrainAsync(
            AiBrain brain,
            TimeSpan waitTimeout)
        {
            if (brain == null) return;
            try
            {
                TimeSpan boundedWait = waitTimeout <= TimeSpan.Zero
                    ? TimeSpan.Zero
                    : (waitTimeout < TimeSpan.FromSeconds(2)
                        ? waitTimeout
                        : TimeSpan.FromSeconds(2));
                if (boundedWait <= TimeSpan.Zero) return;
                using (var timeout = new CancellationTokenSource(boundedWait))
                {
                    Task unload;
                    try
                    {
                        unload = brain.UnloadAsync(timeout.Token);
                    }
                    catch
                    {
                        unload = null;
                    }
                    if (unload != null)
                    {
                        Task completed = await Task.WhenAny(
                            unload,
                            Task.Delay(boundedWait)).ConfigureAwait(false);
                        if (completed == unload)
                        {
                            try { await unload.ConfigureAwait(false); } catch { }
                        }
                        else
                        {
                            ObserveFailure(unload);
                        }
                    }
                }
            }
            finally
            {
                try { brain.Dispose(); } catch { }
            }
        }

        private static TimeSpan Remaining(
            TimeSpan budget,
            TimeSpan elapsed)
        {
            if (budget <= TimeSpan.Zero || elapsed >= budget)
                return TimeSpan.Zero;
            if (elapsed <= TimeSpan.Zero) return budget;
            return budget - elapsed;
        }

        private void QueueDeferredCleanup(CancellationTokenSource cancellation)
        {
            if (Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) != 0)
                return;
            Task.Run(async delegate
            {
                try
                {
                    await _operation.WaitAsync().ConfigureAwait(false);
                    await RetireBrainAsync(_brain).ConfigureAwait(false);
                    _brain = null;
                    RunPendingAfterRetire();
                }
                catch { }
                finally
                {
                    try { cancellation.Dispose(); } catch { }
                    try { _operation.Dispose(); } catch { }
                }
            });
        }

        private void RunPendingAfterRetire()
        {
            Action[] pending;
            lock (_stateLock)
            {
                if (_pendingAfterRetire.Count == 0) return;
                pending = _pendingAfterRetire.ToArray();
                _pendingAfterRetire.Clear();
            }

            foreach (Action action in pending)
            {
                try { action(); }
                catch { }
            }
        }

        private static void ObserveFailure(Task task)
        {
            task.ContinueWith(
                delegate(Task failed)
                {
                    if (failed.Exception != null)
                        failed.Exception.Handle(delegate(Exception ignored) { return true; });
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException("AiSessionManager");
        }
    }
}
