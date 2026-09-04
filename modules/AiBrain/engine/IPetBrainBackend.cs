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
}
