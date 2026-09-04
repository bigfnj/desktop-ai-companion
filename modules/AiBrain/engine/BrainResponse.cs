using System;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// One turn of output from the pet's brain: a short line to speak plus an emotion hint.
    /// The emotion is a loose lowercase string (not an enum) so new emotions can be added
    /// without recompiling — see BACKLOG "Decisions locked in".
    /// </summary>
    internal sealed class BrainResponse
    {
        public string Text { get; private set; }
        public string Emotion { get; private set; }

        public BrainResponse(string text, string emotion)
        {
            Text = text ?? "";
            Emotion = string.IsNullOrWhiteSpace(emotion) ? "neutral" : emotion.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// A single chat message for the backend. <see cref="ImagesBase64"/> carries base64 PNGs for
    /// vision models (Ollama's <c>/api/chat</c> "images" array); null/empty for text-only turns.
    /// </summary>
    internal sealed class ChatMessage
    {
        public string Role { get; private set; }
        public string Content { get; private set; }
        public string[] ImagesBase64 { get; private set; }

        public ChatMessage(string role, string content, string[] imagesBase64)
        {
            Role = role;
            Content = content;
            ImagesBase64 = imagesBase64;
        }

        public static ChatMessage System(string content)
        {
            return new ChatMessage("system", content, null);
        }

        public static ChatMessage User(string content, string[] imagesBase64)
        {
            return new ChatMessage("user", content, imagesBase64);
        }

        public static ChatMessage Assistant(string content)
        {
            return new ChatMessage("assistant", content, null);
        }
    }
}
