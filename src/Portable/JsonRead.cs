using System.Text.Json.Nodes;

namespace DesktopAICompanion
{
    /// <summary>
    /// Lenient System.Text.Json readers that mirror Newtonsoft's null-tolerant JToken casts: a missing key
    /// or a value of the wrong kind yields the fallback ("" / null) instead of throwing, so one malformed
    /// field never aborts a whole parse. Used by the catalog / collections / legacy-settings DOM readers.
    /// </summary>
    internal static class JsonRead
    {
        /// <summary>The string value of a node, or "" when absent or not a JSON string.</summary>
        public static string Str(JsonNode node)
        {
            return node is JsonValue value && value.TryGetValue(out string text) && text != null ? text : "";
        }

        /// <summary>The int value of a node, or null when absent or not a JSON integer.</summary>
        public static int? IntOrNull(JsonNode node)
        {
            return node is JsonValue value && value.TryGetValue(out int number) ? number : (int?)null;
        }

        /// <summary>The bool value of a node, or null when absent or not a JSON boolean.</summary>
        public static bool? BoolOrNull(JsonNode node)
        {
            return node is JsonValue value && value.TryGetValue(out bool flag) ? flag : (bool?)null;
        }
    }
}
