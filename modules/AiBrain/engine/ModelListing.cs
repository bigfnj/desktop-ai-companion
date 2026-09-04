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
}
