using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// A local (or remote) LLM backend the pet's brain talks to. Implemented by
    /// <see cref="OllamaClient"/> (native, with keep-alive VRAM control) and
    /// <see cref="OpenAiCompatBackend"/> (any OpenAI-compatible /v1 endpoint: LM Studio,
    /// llama.cpp, OpenRouter, OpenAI, custom). This seam keeps <see cref="AiBrain"/> backend-agnostic.
    /// </summary>
    internal interface ICompanionBrainBackend : IDisposable
    {
        /// <summary>
        /// Send a chat completion and return the raw assistant text. When <paramref name="jsonFormat"/>
        /// is true the backend is asked to constrain output to JSON.
        /// </summary>
        Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct);

        /// <summary>True when the backend server is reachable. Lets the pet stay silent when it isn't.</summary>
        Task<bool> IsAvailableAsync(CancellationToken ct);

        /// <summary>
        /// Ensure the backend server is running, starting it if necessary, and return true once it
        /// responds. Best-effort: returns false (never throws) if it can't be started.
        /// </summary>
        Task<bool> EnsureServerAsync(CancellationToken ct);

        /// <summary>Preload a model into memory so the first real request is fast. Never throws.</summary>
        Task WarmUpAsync(string model, CancellationToken ct);

        /// <summary>
        /// Request provider-specific model unloading. Backends without memory-control semantics
        /// intentionally implement this as a no-op. Best-effort.
        /// </summary>
        Task UnloadAsync(string model, CancellationToken ct);
    }

    /// <summary>
    /// A backend that can say what models it offers. Optional, and separate from
    /// <see cref="ICompanionBrainBackend"/> on purpose: the composite <see cref="FallbackBackend"/> answers for
    /// its PRIMARY (those are the ids the brain is configured with), and a double that lists nothing is not
    /// made to lie. Until 2026-09-29 the module reached the listing through two type tests, so the composite,
    /// which every cloud user with the default fallback runs, could never be enumerated and BUG-002's
    /// re-validation silently did not apply to the configuration it was written for (F103).
    /// </summary>
    internal interface IModelLister
    {
        /// <summary>The backend's list, or null when it cannot enumerate. Never throws except on cancellation.</summary>
        Task<IReadOnlyList<ModelListing>> ListModelsAsync(CancellationToken ct);
    }
}
