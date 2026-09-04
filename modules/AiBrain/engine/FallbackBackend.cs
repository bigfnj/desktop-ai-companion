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
    internal sealed class FallbackBackend : ICompanionBrainBackend
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
                return await _local.ChatAsync(LocalModelFor(model), messages, jsonFormat, ct).ConfigureAwait(false);
            }
        }

        // The brain picks the primary (cloud) text or vision model; map it to the matching local model on
        // fallback. Only treat it as the vision path when the primary actually has a distinct vision model.
        private string LocalModelFor(string primaryModel)
        {
            if (!string.IsNullOrEmpty(_primaryVisionModel) &&
                string.Equals(primaryModel, _primaryVisionModel, StringComparison.Ordinal))
                return _localVisionModel;
            return _localTextModel;
        }

        /// <summary>Available if EITHER backend is reachable (so a down cloud still lets the local fallback run).</summary>
        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            if (await _primary.IsAvailableAsync(ct).ConfigureAwait(false)) return true;
            return await _local.IsAvailableAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Ready the local server too (it's the fallback); the cloud primary's EnsureServer is a no-op.</summary>
        public async Task<bool> EnsureServerAsync(CancellationToken ct)
        {
            bool primaryReady = await _primary.EnsureServerAsync(ct).ConfigureAwait(false);
            bool localReady = await _local.EnsureServerAsync(ct).ConfigureAwait(false);
            return primaryReady || localReady;
        }

        /// <summary>Warm both (best-effort): the primary with its model, the local with its text model.</summary>
        public async Task WarmUpAsync(string model, CancellationToken ct)
        {
            try { await _primary.WarmUpAsync(model, ct).ConfigureAwait(false); } catch { }
            try { await _local.WarmUpAsync(_localTextModel, ct).ConfigureAwait(false); } catch { }
        }

        public async Task UnloadAsync(string model, CancellationToken ct)
        {
            try { await _primary.UnloadAsync(model, ct).ConfigureAwait(false); } catch { }
            try { await _local.UnloadAsync(_localTextModel, ct).ConfigureAwait(false); } catch { }
        }

        public void Dispose()
        {
            try { _primary.Dispose(); } catch { }
            try { _local.Dispose(); } catch { }
        }
    }
}
