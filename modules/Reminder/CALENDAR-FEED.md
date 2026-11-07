# Reminder feed contract (Local file source)

The Reminder module reads a single JSON file that an external process (a work-side script, an automation,
a calendar exporter) refreshes on a schedule. That process holds the calendar credentials and does the two
hard parts — expanding recurring events into concrete instances and resolving timezones — so the companion only
ever reads unambiguous, already-expanded times. The companion never touches a network or a calendar API.

Point the module at the file in **Options → Reminders → Reminder feed file (JSON)**.

## Format

```json
{
  "updated": "2026-08-25T20:00:00Z",
  "events": [
    { "id": "evt-abc123", "title": "Standup",  "start": "2026-08-26T10:00:00-07:00", "end": "2026-08-26T10:15:00-07:00", "location": "Teams" },
    { "id": "evt-def456", "title": "Dentist",  "start": "2026-08-26T15:30:00-07:00" }
  ]
}
```

### Fields

| Field       | Required | Notes |
|-------------|:--------:|-------|
| `updated`   | no       | ISO 8601 timestamp of when the feed was last written. Shown as a staleness hint. |
| `events[]`  | yes      | The upcoming event instances (write, say, the next few days). |
| `.id`       | **yes**  | A **stable** per-instance key. Same meeting instance = same id across every refresh, so a reminder fires exactly once and a restart does not re-nag. A row without an `id` is skipped. |
| `.title`    | no       | Display text. Missing → announced as "an event". |
| `.start`    | **yes**  | ISO 8601 **with an offset** (`...-07:00` or `...Z`). No offset is treated as local time. A row without a parseable `start` is skipped. |
| `.end`      | no       | ISO 8601, read like `start`. Until then the event is the meeting under way (published to other modules as `meeting.current`) and the tray's Join keeps offering its link. Missing → an hour after `start`. |
| `.location` | no       | Spoken after the reminder line, in brackets, and searched for a meeting link for Join. |
| `.description` | no    | Searched for a meeting link (Teams, Zoom, Google Meet and the like) for Join and the question asked at the meeting's start. Never spoken. |
| `.allDay`   | no       | `true` (or the string `"true"`) marks an all-day event. **Skip all-day events** in Options leaves it out; it never asks to join and is never the meeting under way. Missing → `false`. |
| `.status`   | no       | Your answer to the invite: `accepted`, `tentative` or `declined`. A `declined` event is skipped while **Skip meetings I've declined** is on (the default), whatever that option's label says about Outlook. |
| `.attendees` | no      | The invited roster: an array of `{ "name": "...", "status": "..." }` objects or bare name strings. Published with `meeting.current`, where the Remembrance module seeds a recording's attendance from it. |

## Two asks for the writer

1. **Write atomically.** Write a temp file, then rename it over the real path, so the companion never reads a
   half-written file. (A parse failure is non-fatal — the companion keeps the last good feed and logs the error —
   but a rename avoids the flicker entirely.)
2. **Keep `id`s stable.** The same meeting instance must carry the same `id` every refresh. If ids churn
   (e.g. a fresh GUID each run), the same meeting looks like a brand-new event every hour and could re-fire.

## What the companion does with it

On a short interval it reads the file, and for each event it announces a reminder once, in the window from
`start − lead` to `start + 1 minute` (lead is your "remind me N minutes before" setting). Events already
well past when the app was closed are not announced. The reminder is spoken through the companion's speech bubble.
