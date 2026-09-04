using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// The current meeting the Reminder module publishes to the host shared-context channel under
    /// "meeting.current" (a JSON {name, startUtc, endUtc, location, attendees:[{name,status}]}). Parsed
    /// leniently: an absent key or an unpublished channel just yields an empty context, and the recording
    /// falls back to a timestamp-only name with no roster.
    /// </summary>
    internal sealed class MeetingContext
    {
        public const string Key = "meeting.current";

        public string Name = "";
        public string Location = "";
        public List<string> Attendees = new List<string>();   // "Name" or "Name (status)"

        public static MeetingContext Parse(string json)
        {
            var m = new MeetingContext();
            if (string.IsNullOrWhiteSpace(json)) return m;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return m;
                    m.Name = Str(root, "name");
                    m.Location = Str(root, "location");
                    JsonElement arr;
                    if (root.TryGetProperty("attendees", out arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement a in arr.EnumerateArray())
                        {
                            if (a.ValueKind != JsonValueKind.Object) continue;
                            string name = Str(a, "name");
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            string status = Str(a, "status");
                            m.Attendees.Add(string.IsNullOrEmpty(status) ? name : name + " (" + status + ")");
                        }
                    }
                }
            }
            catch { }
            return m;
        }

        private static string Str(JsonElement o, string name)
        {
            JsonElement v;
            return o.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        }
    }
}
