using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// A composite <see cref="ICompanionBrainBackend"/> that runs a PRIMARY backend (a cloud OpenAI-compatible
    /// provider) and, on a retryable failure, fails over to a LOCAL backend (Ollama). Built by
    /// <c>AiBrainModule.CreateBrain</c> when a cloud provider is primary and "use local as fallback" is on;
    /// the composite is handed to the otherwise-unchanged <see cref="AiBrain"/>, which sees one backend.
    ///
    /// Failure classification is shared with the brain's own retry via <see cref="AiEndpointPolicy.IsRetryable"/>:
    /// a timeout / transient HTTP (408/429/5xx) / transport failure fails over; a DETERMINISTIC failure (a
    /// non-transient 4xx/redirect, e.g. a bad API key) rethrows immediately with no fallback. The primary is
    /// called with the model the brain chose (a cloud model); on fallover the LOCAL model is chosen by the
    /// REQUEST: a message carrying an image runs on the local vision model, anything else on the local text
    /// model (F104). The id mapping in <see cref="LocalModelFor"/> serves only the callers that hold an id and
    /// no request, the id-only warm-up and the release; the brain's warm-up names the PATH instead
    /// (<see cref="WarmUpAsync(string, bool, CancellationToken)"/>), because with one cloud id in both slots the
    /// id says nothing (RA-087, R-021).
    /// </summary>
    internal sealed class FallbackBackend : ICompanionBrainBackend, IModelLister
    {
        private readonly ICompanionBrainBackend _primary;
        private readonly ICompanionBrainBackend _local;
        private readonly string _primaryVisionModel;
        private readonly string _localTextModel;
        private readonly string _localVisionModel;

        public FallbackBackend(
            ICompanionBrainBackend primary,
            ICompanionBrainBackend local,
            string primaryVisionModel,
            string localTextModel,
            string localVisionModel)
        {
            if (primary == null) throw new ArgumentNullException("primary");
            if (local == null) throw new ArgumentNullException("local");
            _primary = primary;
            _local = local;
            _primaryVisionModel = primaryVisionModel ?? "";
            _localTextModel = localTextModel ?? "";
            _localVisionModel = localVisionModel ?? "";
        }

        public async Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            try
            {
                return await _primary.ChatAsync(model, messages, jsonFormat, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (AiEndpointPolicy.IsRetryable(ex, ct))
            {
                ct.ThrowIfCancellationRequested();
                // Held back while Remembrance runs a local model (LocalFallbackAllowed): the cloud's own failure is then
                // the turn's, exactly as it would be with the fallback switched off.
                if (!LocalLegAllowed()) throw;
                // The path is decided by the REQUEST, not by the id: the brain attaches an image only on the vision
                // path. Comparing the id against the primary's vision model sent every fallover to the local vision
                // model once the user had chosen one multimodal cloud model for both slots, so an OCR text remark
                // loaded local llava:13b (F104). Remembered, so UnloadAsync can release what actually ran.
                string localModel = HasImage(messages) ? _localVisionModel : _localTextModel;
                _lastLocalModel = localModel;
                return await _local.ChatAsync(localModel, messages, jsonFormat, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Asked before each fallover; null, the default, always allows one. AiBrainModule sets it to answer "no" while
        /// Remembrance runs a local model (lane feature/aibrain-standdown, Addendum 1 of its brief): that stand-down
        /// protects the LOCAL slot only, so a cloud request still goes ahead, but a retryable cloud failure must not
        /// become a chat to the local model Remembrance may be using on the same server. It is called on the pool thread
        /// the failure arrives on, so the module answers from what its UI thread last read rather than reading the
        /// shared context here.
        /// </summary>
        internal Func<bool> LocalFallbackAllowed { get; set; }

        /// <summary>Whether a fallover may run now; when it may not, the log says why, since the turn then fails with
        /// the cloud's own error and the reason would otherwise be invisible.</summary>
        private bool LocalLegAllowed()
        {
            Func<bool> allowed = LocalFallbackAllowed;
            if (allowed == null || allowed()) return true;
            Action<string> sink = AiBrain.LogSink;
            if (sink != null)
            {
                try { sink("fallback held back: Remembrance is using the local model"); } catch { }
            }
            return false;
        }

        /// <summary>The local model this composite most recently LOADED: the one a fallover ran on (F104) or the one
        /// the warm-up pinned (RA-087). Null before either. Read by <see cref="UnloadAsync"/>, so the release covers
        /// what was resident rather than only what the id mapping names.</summary>
        private volatile string _lastLocalModel;

        private static bool HasImage(IList<ChatMessage> messages)
        {
            if (messages == null) return false;
            foreach (ChatMessage m in messages)
                if (m != null && m.ImagesBase64 != null && m.ImagesBase64.Length > 0) return true;
            return false;
        }

        // The brain picks the primary (cloud) text or vision model; map it to the matching local model. Only
        // treat it as the vision path when the primary actually has a distinct vision model. Used where only an
        // id is known (the id-only warm-up, the release); ChatAsync decides from the request itself (F104) and
        // the brain's warm-up names the path (RA-087).
        private string LocalModelFor(string primaryModel)
        {
            if (!string.IsNullOrEmpty(_primaryVisionModel) &&
                string.Equals(primaryModel, _primaryVisionModel, StringComparison.Ordinal))
                return _localVisionModel;
            return _localTextModel;
        }

        /// <summary>
        /// Available if EITHER backend is reachable, so a down cloud still lets the local fallback run. BOTH legs
        /// are probed at once and the first "up" wins: probed one after the other, a cloud whose traffic is silently
        /// dropped made every ask wait the whole probe deadline before the local leg was even asked (F105). The
        /// abandoned probe's outcome is observed so it can never become an unobserved fault, and the primary's own
        /// answer is remembered whichever leg won (RA-062, see <see cref="ListModelsAsync"/>).
        /// </summary>
        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            Task<bool> primary = _primary.IsAvailableAsync(ct);
            Task<bool> local = _local.IsAvailableAsync(ct);
            RememberPrimaryProbe(primary);
            return await FirstUpAsync(primary, local).ConfigureAwait(false);
        }

        private static async Task<bool> FirstUpAsync(Task<bool> first, Task<bool> second)
        {
            Task<bool> done = await Task.WhenAny(first, second).ConfigureAwait(false);
            Task<bool> other = done == first ? second : first;
            if (await done.ConfigureAwait(false))
            {
                // The engine's one observe-fault helper (F064). This class carried a byte-equivalent private copy of
                // it until 2026-09-30, beside the one OllamaClient's starter kept (RA-086).
                AiEndpointPolicy.ObserveTaskFailure(other);
                return true;
            }
            return await other.ConfigureAwait(false);
        }

        /// <summary>
        /// What the primary's own probe last answered: <see cref="PrimaryUnknown"/> before any, then up or down.
        /// The composite is "up" through whichever leg answers, so a cloud primary that comes back while local
        /// Ollama stayed up is never a transition of the whole, and the brain's transition re-list (F071) never saw
        /// it: a retired cloud id then failed every turn with http-400, deterministic, no fallover, until the next
        /// Apply or pane Refresh. The brain now re-lists while its inventory is unknown, and this record is what lets
        /// <see cref="ListModelsAsync"/> answer "cannot enumerate" without a request while the primary is away, so
        /// the inventory stays unknown instead of becoming an empty list (RA-062).
        /// </summary>
        private volatile int _primaryProbe;
        private const int PrimaryUnknown = 0;
        private const int PrimaryUp = 1;
        private const int PrimaryDown = 2;
        private volatile Task _primaryProbeRecorded;

        private void RememberPrimaryProbe(Task<bool> probe)
        {
            _primaryProbeRecorded = probe.ContinueWith(
                delegate(Task<bool> finished)
                {
                    _primaryProbe = finished.Status == TaskStatus.RanToCompletion && finished.Result
                        ? PrimaryUp
                        : PrimaryDown;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>Completes once the most recent primary probe's answer has been recorded, for the self-test,
        /// which otherwise could only sleep and hope; <see cref="Task.CompletedTask"/> before any probe.</summary>
        internal Task PrimaryProbeRecordedForDiagnostics
        {
            get { return _primaryProbeRecorded ?? Task.CompletedTask; }
        }

        /// <summary>Ready both legs AT ONCE: the local auto-start used to wait behind the cloud probe, which under
        /// a silently dropped route took the whole probe deadline to fail (F105). The cloud's EnsureServer is its
        /// reachability probe, so its answer is remembered here too (RA-062); the local one may launch
        /// `ollama serve`.</summary>
        public async Task<bool> EnsureServerAsync(CancellationToken ct)
        {
            Task<bool> primary = _primary.EnsureServerAsync(ct);
            Task<bool> local = _local.EnsureServerAsync(ct);
            RememberPrimaryProbe(primary);
            await Task.WhenAll(primary, local).ConfigureAwait(false);
            return primary.Result || local.Result;
        }

        /// <summary>Warm both (best-effort) from an id alone: the primary with its model, the local with the model
        /// that id maps to. The brain does not call this one; it names the path (below).</summary>
        public Task WarmUpAsync(string model, CancellationToken ct)
        {
            return WarmLocalAsync(model, LocalModelFor(model), ct);
        }

        /// <summary>
        /// Warm both for a PATH: the primary with its model, the local with the model a fallover on that path will
        /// run, the vision one when the requests will carry an image, the text one otherwise. Until 2026-09-30 the
        /// brain's "keep" warm-up went through the id mapping, and with one cloud id in both slots and vision OFF
        /// that mapping named the local VISION model, so the launch pinned (keep_alive -1) a model no text fallover
        /// would use, and the text model then loaded beside it on the first fallover: two local models resident
        /// until the next release (RA-087).
        /// </summary>
        public Task WarmUpAsync(string model, bool visionPath, CancellationToken ct)
        {
            return WarmLocalAsync(model, visionPath ? _localVisionModel : _localTextModel, ct);
        }

        private async Task WarmLocalAsync(string primaryModel, string localModel, CancellationToken ct)
        {
            try { await _primary.WarmUpAsync(primaryModel, ct).ConfigureAwait(false); } catch { }
            // Remembered BEFORE the request: what the warm-up loads is what the release has to name, whatever the id
            // mapping says (RA-087), the way a fallover records the model it ran (F104).
            _lastLocalModel = localModel;
            try { await _local.WarmUpAsync(localModel, ct).ConfigureAwait(false); } catch { }
        }

        /// <summary>Release both. The local leg maps the id through <see cref="LocalModelFor"/>, and then also
        /// releases the model this composite last LOADED when that is a different one: the fallover's (F104) or the
        /// warm-up's (RA-087). Hard-coding the text model here once meant that after a cloud-primary fallback had
        /// loaded local llava:13b (~8 GB), the fullscreen release unloaded a text model that may never have been
        /// resident while the vision model held its VRAM for the whole game -- the exact outcome the setting's own
        /// comment calls "a crash guard, not a courtesy".</summary>
        public async Task UnloadAsync(string model, CancellationToken ct)
        {
            try { await _primary.UnloadAsync(model, ct).ConfigureAwait(false); } catch { }
            string mapped = LocalModelFor(model);
            try { await _local.UnloadAsync(mapped, ct).ConfigureAwait(false); } catch { }
            string loaded = _lastLocalModel;
            if (!string.IsNullOrEmpty(loaded) && !string.Equals(loaded, mapped, StringComparison.Ordinal))
                try { await _local.UnloadAsync(loaded, ct).ConfigureAwait(false); } catch { }
        }

        /// <summary>
        /// The PRIMARY's list: those are the ids the brain is configured with, so they are the ones BUG-002's
        /// re-validation has to check. Null when the primary cannot enumerate, which ChooseModel treats as
        /// "unknown, do not complain". The local leg's models are mapped by name on fallover and stay
        /// unvalidated, which is acceptable because they run only after the primary has already failed (F103).
        /// Null as well while the primary's own probe last answered "down" (RA-062): the composite is up through
        /// its local leg, so the brain keeps asking while its inventory is unknown, and a request against a
        /// primary that is away would only tie the ask up on a listing that fails; "cannot enumerate" costs no
        /// request and keeps the inventory unknown until the primary is back.
        /// </summary>
        public Task<IReadOnlyList<ModelListing>> ListModelsAsync(CancellationToken ct)
        {
            IModelLister lister = _primary as IModelLister;
            if (lister == null || _primaryProbe == PrimaryDown)
                return Task.FromResult<IReadOnlyList<ModelListing>>(null);
            return lister.ListModelsAsync(ct);
        }

        public void Dispose()
        {
            try { _primary.Dispose(); } catch { }
            try { _local.Dispose(); } catch { }
        }
    }
}
