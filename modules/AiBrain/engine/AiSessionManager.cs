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
        /// <summary>
        /// A SEAM, not a production feature (RA-078): the actions queued through ReconfigureAsync's
        /// afterRetireForDiagnostics parameter run once the retiring brain is gone, under the operation gate. The
        /// one production caller passes none; the four after-retire checks (F089, F091) observe retire order and
        /// serialization through it, a property they could not otherwise reach, which is why it stays under the
        /// register's rule for test-only members and is named for what it is.
        /// </summary>
        private readonly Queue<Action> _pendingAfterRetire = new Queue<Action>();

        private CancellationTokenSource _generationCancellation = new CancellationTokenSource();
        private Func<AiBrain> _factory;
        /// <summary>What the current <see cref="_factory"/> builds for: the fingerprint (AiBrainModule.BackendFingerprint)
        /// and whether the residency keeps the model across an Apply. Copied to <see cref="_liveFingerprint"/> when a
        /// brain is actually built from the factory, and read by a superseded build to decide its own retirement.</summary>
        private string _factoryFingerprint;
        private bool _factoryKeepResident;
        private AiBrain _brain;
        /// <summary>
        /// The fingerprint of the brain in <see cref="_brain"/>, or null when there is none. Owned HERE, beside the
        /// brain, and written when a brain is BUILT, never when an Apply is issued: the module used to record the
        /// fingerprint of the brain the session WOULD build, so an Apply cancelled while queued behind an ask left
        /// its fingerprint behind and the next same-fingerprint Apply retired the OLDER brain without eviction;
        /// under "keep" that model carried keep_alive -1 and outlived the process (R-012, RA-061). The eviction
        /// decision is taken at retire time against this field (<see cref="ReconfigureCoreAsync"/>).
        /// </summary>
        private string _liveFingerprint;
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

        /// <summary>
        /// Replace the brain, or retire it when <paramref name="enabled"/> is false or the factory is null. The
        /// eviction on retire is decided by <paramref name="releaseModel"/> alone. The production entry point is
        /// <see cref="ReconfigureForBackendAsync"/>, which decides it from the fingerprints; this overload serves the
        /// probes, whose retirement checks pin both outcomes without a fingerprint and whose after-retire checks
        /// observe the retire order through the callback (a seam, RA-078; see <see cref="_pendingAfterRetire"/>).
        /// </summary>
        /// <param name="afterRetireForDiagnostics">Run once the retiring brain is gone, under the gate. Null in
        /// production (RA-078).</param>
        /// <param name="releaseModel">False when the caller knows the replacement targets the same backend and
        /// models: the retiring brain is still disposed, but its model is not evicted (F095). Default true.</param>
        public Task<bool> ReconfigureAsync(
            Func<AiBrain> factory,
            bool enabled,
            bool prepare,
            CancellationToken externalCancellation,
            Action afterRetireForDiagnostics = null,
            bool releaseModel = true)
        {
            return ReconfigureCoreAsync(
                factory, enabled, prepare, externalCancellation, afterRetireForDiagnostics, null, false, releaseModel, true);
        }

        /// <summary>
        /// The production reconfigure (AiBrainModule.ApplyState). <paramref name="backendFingerprint"/> names what
        /// decides which model is resident where (AiBrainModule.BackendFingerprint; null when disabled) and
        /// <paramref name="keepResident"/> whether the residency keeps the model across an Apply ("keep" or
        /// "server"). Whether retiring the live brain EVICTS its model is decided here, at retire time, by comparing
        /// the new fingerprint with the one the live brain was BUILT for: the same backend under a residency that
        /// keeps means dispose without eviction (F095), anything else evicts. Decided at issue time by the module,
        /// it modelled the brain the session WOULD build rather than the one it HAD (R-012).
        /// </summary>
        /// <param name="leaveModelsAlone">True while another process may be using the same model on the same server
        /// (Remembrance, while its busy flag is set: AiBrainModule.ApplyState). The preparation then warms nothing, and
        /// a brain this call retires is disposed without its eviction, whatever the fingerprints say, so the model it
        /// kept resident is left to its own keep_alive. Decided when the Apply is issued (lane feature/aibrain-standdown,
        /// Addendum 1).</param>
        public Task<bool> ReconfigureForBackendAsync(
            Func<AiBrain> factory,
            bool enabled,
            bool prepare,
            CancellationToken externalCancellation,
            string backendFingerprint,
            bool keepResident,
            bool leaveModelsAlone = false)
        {
            return ReconfigureCoreAsync(
                factory, enabled, prepare, externalCancellation, null, backendFingerprint, keepResident,
                leaveModelsAlone ? (bool?)false : null, !leaveModelsAlone);
        }

        /// <param name="warmUp">False: the preparation still starts the server and checks it, and warms no model
        /// (AiBrain.PrepareAsync's own switch, F063).</param>
        private async Task<bool> ReconfigureCoreAsync(
            Func<AiBrain> factory,
            bool enabled,
            bool prepare,
            CancellationToken externalCancellation,
            Action afterRetire,
            string backendFingerprint,
            bool keepResident,
            bool? releaseModelOverride,
            bool warmUp)
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
                _factoryFingerprint = backendFingerprint;
                _factoryKeepResident = keepResident;
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

                    // Retire WITHOUT evicting when the replacement targets the same backend and models under a
                    // residency that keeps them resident (F095); decided against the brain that is live NOW, under
                    // the gate, so an Apply cancelled while queued never leaves a fingerprint behind for the next one
                    // to match (R-012). Under "unload" the model is gone after each remark anyway, so the eviction on
                    // retire stays (it is free there). Disable and shutdown keep the eviction: no fingerprint.
                    bool releaseModel = releaseModelOverride ??
                        !(keepResident && backendFingerprint != null &&
                          string.Equals(backendFingerprint, _liveFingerprint, StringComparison.Ordinal));
                    await RetireBrainAsync(_brain, releaseModel).ConfigureAwait(false);
                    _brain = null;
                    _liveFingerprint = null;
                    RunPendingAfterRetire();

                    if (!IsCurrent(generation, enabled)) return false;
                    if (!enabled || factory == null) return false;

                    // A factory that throws used to fault this task, which ApplyState discards, so the brain stayed
                    // null with nothing in the log until the next Apply (F100). CanUse gates the reasons CreateBrain
                    // throws on purpose; what reaches here is a torn read or a bug, and either deserves a line.
                    try { _brain = factory(); }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        AiBrain.LogBuildFailure(ex);
                        return false;
                    }
                    _liveFingerprint = backendFingerprint;
                    if (!IsCurrent(generation, true))
                    {
                        // Superseded between the factory and this check. Nothing was awaited in between, so the fresh
                        // brain has sent no request and loaded nothing of its own; what may be resident is the model
                        // the previous brain kept under the same fingerprint. So this retirement is decided the way
                        // every other one is, against what comes NEXT: the superseding generation's fingerprint and
                        // residency. Through the default overload it evicted unconditionally, which cost a same-
                        // fingerprint "keep" Apply the cold reload F095 had just removed (RA-079).
                        string nextFingerprint;
                        bool nextKeeps;
                        lock (_stateLock)
                        {
                            nextFingerprint = _factoryFingerprint;
                            nextKeeps = _factoryKeepResident;
                        }
                        bool keptForNext = nextKeeps && nextFingerprint != null &&
                            string.Equals(nextFingerprint, backendFingerprint, StringComparison.Ordinal);
                        await RetireBrainAsync(_brain, releaseModelOverride ?? !keptForNext).ConfigureAwait(false);
                        _brain = null;
                        _liveFingerprint = null;
                        return false;
                    }
                    if (!prepare) return true;

                    bool ready = await _brain.PrepareAsync(linked.Token, warmUp).ConfigureAwait(false);
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

        /// <summary>
        /// Ask the live brain to re-list what its backend offers, without the gate and best-effort, for the same
        /// reasons as <see cref="ReleaseModelAsync"/>. The pane's "Refresh models" calls it so the brain's idea
        /// of what is installed does not lag the dropdowns until the next Apply (F071).
        /// </summary>
        public Task RefreshInventoryAsync(CancellationToken ct)
        {
            AiBrain brain = _brain;
            if (brain == null) return Task.CompletedTask;
            try { return brain.RefreshInventoryAsync(ct); }
            catch { return Task.CompletedTask; }
        }

        /// <summary>
        /// Make the live brain resolve its OCR engine afresh on its next read, with the path now configured;
        /// gate-free and best-effort like <see cref="RefreshInventoryAsync"/>. "Test OCR" and "Choose OCR
        /// engine..." call it: until 2026-09-30 they reset the cache of a throwaway brain and the live one kept
        /// the engine it had resolved when it was built (R-015).
        /// </summary>
        public void ForgetOcrResolution(string configuredTesseractPath)
        {
            AiBrain brain = _brain;
            if (brain == null) return;
            try { brain.ForgetTesseractResolution(configuredTesseractPath); } catch { }
        }

        /// <summary>The brain the session holds right now, for the self-test only (it asserts what a pane action
        /// does to it). A reference read, no gate.</summary>
        internal AiBrain LiveBrainForDiagnostics { get { return _brain; } }

        /// <summary>Why the live brain's last ask produced nothing, a category, for the pane's Status card (lane
        /// feature/cli-backend). A reference read, no gate, best-effort like the two members above.</summary>
        internal string LastAskFailure
        {
            get
            {
                AiBrain brain = _brain;
                return brain == null ? null : brain.LastFailure;
            }
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
                            string fingerprint;
                            lock (_stateLock)
                            {
                                factory = _factory;
                                fingerprint = _factoryFingerprint;
                            }
                            if (factory == null) return null;
                            try { _brain = factory(); }
                            catch (Exception ex) when (!(ex is OperationCanceledException))
                            {
                                AiBrain.LogBuildFailure(ex);   // as in ReconfigureAsync (F100)
                                return null;
                            }
                            _liveFingerprint = fingerprint;   // the lazy build is a build (R-012)
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
        // when something on screen actually changed" option would be built on. The keep is recorded in
        // docs/DESIGN-REGISTER.md under `#### burn/aibrain` (RA-066).

        public void Dispose()
        {
            DisposeCore(TimeSpan.FromSeconds(3));
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
                            stopwatch.Elapsed),
                        true).GetAwaiter().GetResult();
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
                    // The serialized operation did not unwind in time: a holder that ignores cancellation for the
                    // whole budget (a hung transport, an OCR child that survived its kill). Release the model FIRST,
                    // bounded, and dispose the backend only then. The deferred cleanup used to do it the other way
                    // round, so its unload landed on a disposed HttpClient and was swallowed, and under "keep"
                    // residency the model stayed resident after the app had gone (F091). HttpClient accepts a
                    // concurrent request, so the unload does not queue behind the hung one, and a request the budget
                    // cuts off is aborted by the Dispose that follows.
                    AiBrain active = _brain;
                    if (active != null)
                    {
                        try
                        {
                            using (var unloadBudget = new CancellationTokenSource(NotEnteredUnloadBudget))
                            {
                                Task unload = active.UnloadAsync(unloadBudget.Token);
                                if (!unload.Wait(NotEnteredUnloadBudget)) AiEndpointPolicy.ObserveTaskFailure(unload);
                            }
                        }
                        catch { }
                        try { active.Dispose(); } catch { }
                    }
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

        /// <summary>How long the not-entered dispose path waits for its unload before disposing anyway (F091).</summary>
        private static readonly TimeSpan NotEnteredUnloadBudget = TimeSpan.FromSeconds(1);

        private static Task RetireBrainAsync(AiBrain brain)
        {
            return RetireBrainAsync(brain, TimeSpan.FromSeconds(2), true);
        }

        private static Task RetireBrainAsync(AiBrain brain, bool releaseModel)
        {
            return RetireBrainAsync(brain, TimeSpan.FromSeconds(2), releaseModel);
        }

        private static async Task RetireBrainAsync(
            AiBrain brain,
            TimeSpan waitTimeout,
            bool releaseModel)
        {
            if (brain == null) return;
            // Retire WITHOUT evicting when the caller says the replacement targets the same backend and models (a
            // same-backend Apply under "keep" or "server" residency): the brain still has to go, it owns the
            // HttpClient and the persona clone, but the model it loaded is the model the next brain will use, and
            // evicting it here cost a cold reload on every Apply (F095). Disable and shutdown keep the eviction.
            if (!releaseModel)
            {
                try { brain.Dispose(); } catch { }
                return;
            }
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
                            AiEndpointPolicy.ObserveTaskFailure(unload);
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
                    // The model was released and the backend disposed by DisposeCore before this was queued; what is
                    // left is the reference, the after-retire actions and the primitives. Dispose is idempotent, so
                    // a brain the not-entered branch already handled is not touched twice (F091).
                    AiBrain brain = _brain;
                    _brain = null;
                    try { if (brain != null) brain.Dispose(); } catch { }
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

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException("AiSessionManager");
        }
    }
}
