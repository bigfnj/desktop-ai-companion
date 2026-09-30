using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// The "corporate" source: reads a normalized JSON feed a work-side process writes (see
    /// CALENDAR-FEED.md). That process holds the calendar credentials and does the hard parts (recurrence
    /// expansion, timezone resolution), so this source just deserializes concrete instances and never touches
    /// a network or a calendar API. The path is read live (via the supplied getter) so a settings change takes
    /// effect on the next tick with no restart.
    ///
    /// Format: { "updated": "&lt;ISO8601&gt;", "events": [ { "id", "title", "start" (ISO8601 with offset),
    /// "end"?, "location"? } ] }. Malformed events are skipped, not fatal, so one bad row never blanks the feed.
    ///
    /// Derives from CachingCalendarSource like its two siblings. It used to implement ICalendarSource
    /// directly, on the reasoning that a local file is fast -- but CALENDAR-FEED.md describes this path as a
    /// work-side exporter's output, which in practice means a share. When the VPN drops, File.ReadAllText
    /// blocks on the SMB timeout, and it was doing that on the UI thread every 20 seconds with the pets
    /// frozen behind it. "Local" describes the API being used, not where the bytes are.
    ///
    /// The refresh interval sits clearly BELOW the module's 20 s tick, so every tick kicks a re-read. It used
    /// to equal the tick, and this comment claimed freshness was "exactly what it was". Neither held: the
    /// interval is measured from when the previous read LANDED, and the tick arrives 20 s plus timer lateness
    /// after the previous tick, so whether the next tick counted as stale came down to lateness versus read
    /// latency -- a coin flip for a local file, false for a share, where the file was re-read every OTHER
    /// tick (F191). The honest freshness figure is this: a read kicked at tick N is served at tick N+1
    /// (Fetch captures the cache before it kicks), so the worst-case staleness is two ticks, 40 s, double the
    /// synchronous read this replaced. That is the price of the read leaving the UI thread, and 10 s makes
    /// it deterministic rather than 40-or-60 depending on the disk.
    /// One other follows from the base: the very first tick answers "reading…" instead of the events,
    /// because the read has been kicked and has not landed yet.
    /// </summary>
    public sealed class LocalJsonSource : CachingCalendarSource
    {
        private const long MaximumBytes = 2 * 1024 * 1024;   // a few days of events is tiny; bound a runaway file
        private readonly Func<string> _pathGetter;

        public LocalJsonSource(Func<string> pathGetter) : base(TimeSpan.FromSeconds(10))
        {
            _pathGetter = pathGetter ?? throw new ArgumentNullException(nameof(pathGetter));
        }

        public override string Name { get { return "Local file"; } }

        protected override string LoadingMessage { get { return "Reading the reminder file…"; } }

        // The path is the config that forces an early refresh, exactly as the URL does for IcsUrlSource:
        // point the setting at a different file and the next tick re-reads rather than waiting out the
        // interval.
        protected override string RefreshKey() { return (_pathGetter() ?? "").Trim(); }

        protected override CalendarSnapshot FetchCore(string key, DateTimeOffset now)
        {
            var empty = new CalendarSnapshot { Events = Array.Empty<CalendarEvent>() };
            // The path the caller's thread read from the settings; not re-read here, this is a pool thread (F192).
            string path = key ?? "";
            if (path.Length == 0) { empty.Error = "No reminder file is configured."; return empty; }

            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception ex) { empty.Error = "Bad path: " + ex.Message; return empty; }
            if (!info.Exists) { empty.Error = "Reminder file not found: " + path; return empty; }
            if (info.Length > MaximumBytes) { empty.Error = "Reminder file is too large (over 2 MiB)."; return empty; }

            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex) { empty.Error = "Could not read the reminder file: " + ex.Message; return empty; }

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    DateTimeOffset? updated = ReadOffset(root, "updated");

                    var events = new List<CalendarEvent>();
                    JsonElement arr;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("events", out arr)
                        && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement e in arr.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.Object) continue;
                            string id = ReadString(e, "id");
                            DateTimeOffset? start = ReadOffset(e, "start");
                            if (string.IsNullOrEmpty(id) || start == null) continue;   // skip a malformed row
                            events.Add(new CalendarEvent
                            {
                                Id = id,
                                Title = ReadString(e, "title"),
                                Start = start.Value,
                                End = ReadOffset(e, "end"),
                                Location = ReadString(e, "location"),
                                Description = ReadString(e, "description"),
                                AllDay = ReadBool(e, "allDay"),
                                ResponseStatus = ReadString(e, "status"),
                                Attendees = ReadAttendees(e),
                            });
                        }
                    }
                    return new CalendarSnapshot { Events = events, Updated = updated, Error = null };
                }
            }
            catch (JsonException ex)
            {
                empty.Error = "Reminder file is not valid JSON: " + ex.Message;
                return empty;
            }
        }

        private static string ReadString(JsonElement obj, string name)
        {
            JsonElement v;
            return obj.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        // attendees: an array of {"name","status"} objects, or bare "Name" strings. Null when absent/empty.
        private static List<Attendee> ReadAttendees(JsonElement obj)
        {
            JsonElement arr;
            if (!obj.TryGetProperty("attendees", out arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Attendee>();
            foreach (JsonElement a in arr.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String)
                {
                    string n = a.GetString();
                    if (!string.IsNullOrWhiteSpace(n)) list.Add(new Attendee { Name = n.Trim(), Status = "" });
                }
                else if (a.ValueKind == JsonValueKind.Object)
                {
                    string n = ReadString(a, "name");
                    if (!string.IsNullOrWhiteSpace(n)) list.Add(new Attendee { Name = n.Trim(), Status = ReadString(a, "status") ?? "" });
                }
            }
            return list.Count > 0 ? list : null;
        }

        private static bool ReadBool(JsonElement obj, string name)
        {
            JsonElement v;
            if (!obj.TryGetProperty(name, out v)) return false;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            bool b;
            return v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out b) && b;
        }

        private static DateTimeOffset? ReadOffset(JsonElement obj, string name)
        {
            string s = ReadString(obj, name);
            if (string.IsNullOrWhiteSpace(s)) return null;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out dto))
                return dto;
            return null;
        }
    }
}
