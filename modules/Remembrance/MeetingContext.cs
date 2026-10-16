using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// The current meeting the Reminder module publishes to the host shared-context channel under
    /// "meeting.current" (a JSON {name, startUtc, endUtc, location, attendees:[{name,status}]}). Parsed
    /// leniently: an absent key or an unpublished channel just yields an empty context, and the recording
    /// falls back to a timestamp-only name with no roster.
    ///
    /// Since 2.4.0 (lane feature/meeting-end) the two times are read too, for the meeting-end watch: when the
    /// meeting the recording started in was due to end (R2 asks once that has passed), and whether a LATER
    /// meeting has become current (R4). Reminder writes both with the round-trip "o" format, and endUtc as JSON
    /// null for an event with no end; a value that is absent, null or does not parse is null here, and nothing
    /// downstream invents one.
    /// </summary>
    internal sealed class MeetingContext
    {
        public const string Key = "meeting.current";

        public string Name = "";
        public string Location = "";
        public List<string> Attendees = new List<string>();   // "Name" or "Name (status)"
        public DateTimeOffset? StartUtc;
        public DateTimeOffset? EndUtc;

        /// <summary>Nothing published, or nothing usable: no name and no start.</summary>
        public bool IsEmpty { get { return string.IsNullOrWhiteSpace(Name) && !StartUtc.HasValue; } }

        /// <summary>
        /// Which meeting this is, "name|startUtc", so the same meeting republished (an attendee's answer changed, so
        /// Reminder's JSON changed) is recognised as the same one and a back-to-back meeting is not. "" when empty.
        /// Named MeetingKey because <see cref="Key"/> was already the channel's key.
        /// </summary>
        public string MeetingKey
        {
            get
            {
                if (IsEmpty) return "";
                return (Name ?? "") + "|" + (StartUtc.HasValue
                    ? StartUtc.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) : "");
            }
        }

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
                    m.StartUtc = Time(root, "startUtc");
                    m.EndUtc = Time(root, "endUtc");
                    // An end at or before the start is no end: an all-day item's zero-length shape, or a feed's mistake,
                    // would otherwise read as a meeting that ended the moment it began.
                    if (m.StartUtc.HasValue && m.EndUtc.HasValue && m.EndUtc.Value <= m.StartUtc.Value) m.EndUtc = null;
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

        // A round-trip time as UTC, or null for anything else: absent, JSON null, a number, or text that is not a time.
        private static DateTimeOffset? Time(JsonElement o, string name)
        {
            JsonElement v;
            if (!o.TryGetProperty(name, out v) || v.ValueKind != JsonValueKind.String) return null;
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                return parsed.ToUniversalTime();
            return null;
        }
    }
}
