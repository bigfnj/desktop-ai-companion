namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// One entry from a backend's installed/available model list (see
    /// <see cref="OllamaClient.ListModelsAsync"/>, <see cref="OpenAiCompatBackend.ListModelsAsync"/>).
    /// <see cref="Vision"/> is the backend's OWN reported capability when it has one (Ollama's
    /// <c>/api/tags</c> "capabilities" array is a real signal); it is null when the backend has no such
    /// signal (an older Ollama server, or any generic OpenAI-compatible <c>/v1/models</c> response), in
    /// which case the caller should fall back to <see cref="AiModelPolicy.LooksVisionCapable"/>.
    /// <see cref="SizeBytes"/> is the model's on-disk size — a solid proxy for its VRAM/weight footprint
    /// when loaded — from Ollama's own <c>"size"</c> field; null when the backend reports none (the
    /// generic OpenAI-compatible <c>/v1/models</c> response carries no size metadata at all).
    /// </summary>
    internal sealed class ModelListing
    {
        public ModelListing(string id, bool? vision) : this(id, vision, null)
        {
        }

        public ModelListing(string id, bool? vision, long? sizeBytes)
        {
            Id = id;
            Vision = vision;
            SizeBytes = sizeBytes;
        }

        public string Id { get; private set; }
        public bool? Vision { get; private set; }
        public long? SizeBytes { get; private set; }
    }

    /// <summary>
    /// The outcome of <see cref="AiModelPolicy.ChooseModel"/>: which model an ask should use, and
    /// whether the user needs telling about it.
    ///
    /// <see cref="Advisory"/> is user-facing text and is null when nothing needs saying -- which is the
    /// common case, and also the case when the backend gave no model list at all (absence of evidence is
    /// not evidence of absence, so an unknown list must not produce a complaint). <see cref="Reason"/> is
    /// for the diagnostic log and is always set.
    /// </summary>
    internal sealed class ModelChoice
    {
        public ModelChoice(string model, string advisory, string reason)
        {
            Model = model;
            Advisory = advisory;
            Reason = reason;
        }

        /// <summary>The id to send, or null when the backend has nothing that can do the job.</summary>
        public string Model { get; private set; }

        /// <summary>A short line the companion can say, or null when all is well.</summary>
        public string Advisory { get; private set; }

        /// <summary>One of: configured, substituted, none-usable, model-list-unknown.</summary>
        public string Reason { get; private set; }

        public bool Usable { get { return !string.IsNullOrWhiteSpace(Model); } }
    }
}
