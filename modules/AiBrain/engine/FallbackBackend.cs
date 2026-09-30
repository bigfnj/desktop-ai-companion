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
    /// called with the model the brain chose (a cloud model); on fallback the corresponding LOCAL model is
    /// used (vision vs text is decided by comparing the incoming model to the primary's vision model).
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
                // The path is decided by the REQUEST, not by the id: the brain attaches an image only on the vision
                // path. Comparing the id against the primary's vision model sent every fallover to the local vision
                // model once the user had chosen one multimodal cloud model for both slots, so an OCR text remark
                // loaded local llava:13b (F104). Remembered, so UnloadAsync can release what actually ran.
                string localModel = HasImage(messages) ? _localVisionModel : _localTextModel;
                _lastLocalModel = localModel;
                return await _local.ChatAsync(localModel, messages, jsonFormat, ct).ConfigureAwait(false);
            }
        }

        /// <summary>The local model the most recent fallover ran on, or null before any (F104).</summary>
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
        // id is known (warm-up, unload); ChatAsync decides from the request itself (F104).
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
        /// abandoned probe's outcome is observed so it can never become an unobserved fault.
        /// </summary>
        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            Task<bool> primary = _primary.IsAvailableAsync(ct);
            Task<bool> local = _local.IsAvailableAsync(ct);
            return await FirstUpAsync(primary, local).ConfigureAwait(false);
        }

        private static async Task<bool> FirstUpAsync(Task<bool> first, Task<bool> second)
        {
            Task<bool> done = await Task.WhenAny(first, second).ConfigureAwait(false);
            Task<bool> other = done == first ? second : first;
            if (await done.ConfigureAwait(false))
            {
                ObserveOutcome(other);
                return true;
            }
            return await other.ConfigureAwait(false);
        }

        private static void ObserveOutcome(Task task)
        {
            task.ContinueWith(
                delegate(Task finished) { var ignored = finished.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>Ready both legs AT ONCE: the local auto-start used to wait behind the cloud probe, which under
        /// a silently dropped route took the whole probe deadline to fail (F105). The cloud's EnsureServer is its
        /// reachability probe; the local one may launch `ollama serve`.</summary>
        public async Task<bool> EnsureServerAsync(CancellationToken ct)
        {
            Task<bool> primary = _primary.EnsureServerAsync(ct);
            Task<bool> local = _local.EnsureServerAsync(ct);
            await Task.WhenAll(primary, local).ConfigureAwait(false);
            return primary.Result || local.Result;
        }

        /// <summary>Warm both (best-effort): the primary with its model, the local with the model that
        /// primary one maps to -- the same mapping ChatAsync uses, so the pair that gets warmed is the
        /// pair that will actually run.</summary>
        public async Task WarmUpAsync(string model, CancellationToken ct)
        {
            try { await _primary.WarmUpAsync(model, ct).ConfigureAwait(false); } catch { }
            try { await _local.WarmUpAsync(LocalModelFor(model), ct).ConfigureAwait(false); } catch { }
        }

        /// <summary>Release both. The local leg maps through <see cref="LocalModelFor"/> for the same
        /// reason ChatAsync does: hard-coding the text model here meant that after a cloud-primary
        /// fallback had loaded local llava:13b (~8 GB), the fullscreen release unloaded a text model
        /// that may never have been resident while the vision model held its VRAM for the whole game --
        /// the exact outcome the setting's own comment calls "a crash guard, not a courtesy".</summary>
        public async Task UnloadAsync(string model, CancellationToken ct)
        {
            try { await _primary.UnloadAsync(model, ct).ConfigureAwait(false); } catch { }
            string mapped = LocalModelFor(model);
            try { await _local.UnloadAsync(mapped, ct).ConfigureAwait(false); } catch { }
            // ...and the model a fallover actually loaded, when the id mapping named a different one (F104).
            string ran = _lastLocalModel;
            if (!string.IsNullOrEmpty(ran) && !string.Equals(ran, mapped, StringComparison.Ordinal))
                try { await _local.UnloadAsync(ran, ct).ConfigureAwait(false); } catch { }
        }

        /// <summary>
        /// The PRIMARY's list: those are the ids the brain is configured with, so they are the ones BUG-002's
        /// re-validation has to check. Null when the primary cannot enumerate, which ChooseModel treats as
        /// "unknown, do not complain". The local leg's models are mapped by name on fallover and stay
        /// unvalidated, which is acceptable because they run only after the primary has already failed (F103).
        /// </summary>
        public Task<IReadOnlyList<ModelListing>> ListModelsAsync(CancellationToken ct)
        {
            IModelLister lister = _primary as IModelLister;
            return lister != null
                ? lister.ListModelsAsync(ct)
                : Task.FromResult<IReadOnlyList<ModelListing>>(null);
        }

        public void Dispose()
        {
            try { _primary.Dispose(); } catch { }
            try { _local.Dispose(); } catch { }
        }
    }
}
