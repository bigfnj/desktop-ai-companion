using System;
using System.Globalization;
using System.Text.Json;

namespace DesktopAICompanion.MeetingApps
{
    /// <summary>
    /// Join &amp; record (Reminder 1.2.0, Remembrance 2.5.0; the owner's request of 2026-10-16): Reminder's join card offers
    /// a third button that joins the meeting AND starts a Remembrance recording. Modules load in separate contexts and
    /// cannot call each other, so the two talk over the host's shared context (PluginApi.cs, PublishContext and
    /// ReadContext), which needs no host change:
    ///
    ///   <see cref="ReadyKey"/>, published by Remembrance: {"accepts":true,"recording":false}. At Init, at every start and
    ///   stop, and when its "Let Reminder's Join &amp; record start a recording" setting changes; "" at Shutdown. Reminder
    ///   offers the button only when this says accepts and not recording, so the button is never shown for a recording
    ///   that cannot start.
    ///
    ///   <see cref="RequestKey"/>, published by Reminder when the button is pressed: {"id":"…","at":"…","meeting":{…}},
    ///   the meeting in the very shape Reminder publishes "meeting.current", so Remembrance names the recording with its
    ///   own MeetingContext.Parse. Remembrance reads it at use, on its idle watch tick (every 5 s), acts on an id it has
    ///   not seen and no older than <see cref="RequestFreshFor"/>, and never on a request that arrives while it records.
    ///
    /// Source-linked into both modules, beside MeetingAppMicrophone, so the key names and the JSON cannot drift apart.
    /// </summary>
    internal static class RecordingHandshake
    {
        internal const string ReadyKey = "remembrance.recorder";
        internal const string RequestKey = "meeting.record-request";

        /// <summary>A request older than this is ignored: the meeting it was pressed for has moved on, and starting a
        /// recording a minute after the user asked is not what they asked for.</summary>
        internal static readonly TimeSpan RequestFreshFor = TimeSpan.FromSeconds(60);

        internal static string ReadyJson(bool accepts, bool recording)
        {
            return "{\"accepts\":" + (accepts ? "true" : "false") + ",\"recording\":" + (recording ? "true" : "false") + "}";
        }

        /// <summary>Whether Remembrance can take a request right now. False for "" (not installed, or shut down) and for
        /// anything malformed: a button that might not work is not offered.</summary>
        internal static bool CanRecordNow(string readyJson)
        {
            if (string.IsNullOrEmpty(readyJson)) return false;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(readyJson))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return false;
                    JsonElement accepts, recording;
                    return root.TryGetProperty("accepts", out accepts) && accepts.ValueKind == JsonValueKind.True
                           && root.TryGetProperty("recording", out recording) && recording.ValueKind == JsonValueKind.False;
                }
            }
            catch (JsonException) { return false; }
        }

        /// <summary>The request. <paramref name="meetingJson"/> must be a JSON object (Reminder's SerializeMeeting); anything
        /// else is sent as an empty meeting rather than spliced in.</summary>
        internal static string RequestJson(string id, DateTimeOffset at, string meetingJson)
        {
            string meeting = "{}";
            if (!string.IsNullOrEmpty(meetingJson))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(meetingJson))
                        if (document.RootElement.ValueKind == JsonValueKind.Object) meeting = document.RootElement.GetRawText();
                }
                catch (JsonException) { }
            }
            return "{\"id\":" + JsonSerializer.Serialize(id ?? "") + ",\"at\":"
                   + JsonSerializer.Serialize(at.ToString("o", CultureInfo.InvariantCulture)) + ",\"meeting\":" + meeting + "}";
        }

        /// <summary>A request's parts, or false when it is not one (unset, malformed, no id, no time).</summary>
        internal static bool TryParseRequest(string json, out string id, out DateTimeOffset at, out string meetingJson)
        {
            id = ""; at = default(DateTimeOffset); meetingJson = "";
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return false;
                    JsonElement idElement, atElement, meeting;
                    if (!root.TryGetProperty("id", out idElement) || idElement.ValueKind != JsonValueKind.String) return false;
                    if (!root.TryGetProperty("at", out atElement) || atElement.ValueKind != JsonValueKind.String) return false;
                    id = idElement.GetString() ?? "";
                    if (id.Length == 0) return false;
                    if (!DateTimeOffset.TryParse(atElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at))
                        return false;
                    if (root.TryGetProperty("meeting", out meeting) && meeting.ValueKind == JsonValueKind.Object)
                        meetingJson = meeting.GetRawText();
                    return true;
                }
            }
            catch (JsonException) { return false; }
        }

        /// <summary>The handshake's own checks, run by both modules' self-tests.</summary>
        internal static void SelfCheck(Action<string, bool> check)
        {
            check("join & record: Remembrance ready and idle reads as able to record",
                CanRecordNow(ReadyJson(true, false)));
            check("join & record: recording already, switched off, absent or malformed reads as NOT able",
                !CanRecordNow(ReadyJson(true, true)) && !CanRecordNow(ReadyJson(false, false))
                && !CanRecordNow("") && !CanRecordNow(null) && !CanRecordNow("{not json") && !CanRecordNow("[true]"));
            var at = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.FromHours(-5));
            string meeting = "{\"name\":\"Design \\\"review\\\"\",\"startUtc\":\"2026-10-16T14:00:00Z\"}";
            string id; DateTimeOffset back; string meetingBack;
            bool parsed = TryParseRequest(RequestJson("abc123", at, meeting), out id, out back, out meetingBack);
            check("join & record: a request round-trips its id, its moment and the meeting object",
                parsed && id == "abc123" && back == at && meetingBack.IndexOf("Design \\\"review\\\"", StringComparison.Ordinal) > 0);
            check("join & record: a meeting that is not a JSON object travels as an empty one, never spliced in",
                TryParseRequest(RequestJson("x", at, "},{\"evil\":1"), out id, out back, out meetingBack) && meetingBack == "{}");
            check("join & record: no id or no time is not a request",
                !TryParseRequest("{\"at\":\"2026-10-16T14:00:00Z\"}", out id, out back, out meetingBack)
                && !TryParseRequest("{\"id\":\"x\"}", out id, out back, out meetingBack)
                && !TryParseRequest("", out id, out back, out meetingBack));
        }
    }
}
