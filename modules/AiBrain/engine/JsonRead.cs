using System.Text.Json.Nodes;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Lenient System.Text.Json reader that mirrors Newtonsoft's null-tolerant <c>(string)JToken</c> cast:
    /// a missing key or a value of the wrong kind yields "" instead of throwing, so one malformed field
    /// never aborts a whole parse. Module-local (distinct from the base's DesktopAICompanion.JsonRead, a different
    /// assembly) and used by the backend response readers and the {text,emotion} reply parse.
    /// </summary>
    internal static class JsonRead
    {
        /// <summary>The string value of a node, or "" when absent or not a JSON string.</summary>
        public static string Str(JsonNode node)
        {
            return node is JsonValue value && value.TryGetValue(out string text) && text != null ? text : "";
        }

        /// <summary>The int64 value of a node, or null when absent or not a JSON number. Ollama's model
        /// "size" field is multi-gigabyte, well past Int32 range.</summary>
        public static long? Int64OrNull(JsonNode node)
        {
            return node is JsonValue value && value.TryGetValue(out long number) ? number : (long?)null;
        }
    }
}
