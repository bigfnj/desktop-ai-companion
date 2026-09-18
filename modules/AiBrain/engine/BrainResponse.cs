using System;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// One turn of output from the pet's brain: a short line to speak plus an emotion hint.
    /// The emotion is a lowercase string rather than an enum because the emotion -> animation-name
    /// mapping belongs to the CALLER and not to this type, which is what the ABI states at
    /// IHost.PlayAnimationAll ("the caller owns the emotion->animation-name mapping").
    /// The accepted vocabulary is NOT open-ended: AiBrain.NormalizeEmotion is a closed allowlist of
    /// six names and folds anything else to "neutral", so adding one is a code change there.
    /// Corrected 2026-09-17: this comment cited a BACKLOG "Decisions locked in" section that no
    /// longer exists anywhere, and claimed new emotions could be added without recompiling, which
    /// NormalizeEmotion refutes.
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
