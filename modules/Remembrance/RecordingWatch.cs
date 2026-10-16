using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopAICompanion.MeetingApps;
using DesktopAICompanion.Modules;

// The meeting-end watch (lane feature/meeting-end, remembrance 2.4.0, the owner's decisions of 2026-10-15). The owner
// starts a recording and forgets to stop it after the meeting; until 2.4.0 nothing watched a recording once it started,
// so a forgotten one kept capturing the room, kept the machine awake, and on this machine stopped saving after about
// three hours without a word. This file decides WHEN to ask the user something and what each answer means; it draws
// nothing and touches no device. RemembranceModule.Watch.cs feeds it on a timer and puts its questions to the host's
// tray card (ITrayQuestions, host 1.6.0).
//
// PURE BY DESIGN: every input arrives as an argument (the clock, the loudness of each track, Windows' record of which
// meeting app holds the microphone, the meeting Reminder publishes, the settings), so the self-test drives every rule
// with no audio, no registry and no waiting. Every threshold is in WatchTuning, in one place, because the silence
// figures are a first guess that real meetings will tune.
//
// The questions, in priority order (a higher one withdraws a lower one that is showing; a question taken down that way
// is not spent and comes back when it still applies):
//   R3 "remembrance.limit"        the recording reached "Longest recording". Applies in every mode, Off included.
//   R1 "remembrance.meeting-over" the meeting looks over: Teams or Zoom let go of the microphone (R1a), or nobody has
//                                 talked for 5 minutes, 15 while a meeting app still holds the microphone (R1b).
//   R4 "remembrance.next-meeting" a later calendar meeting has started while this one records.
//   R2 "remembrance.past-end"     the calendar end of the meeting the recording started in has passed.
// And, with nothing recording, R5 "remembrance.offer-record" (OfferWatch, opt-in, off by default).
//
// TRIM ONLY WHEN NOBODY ANSWERS R1, in "Warn, then stop": the recording stops and keeps up to the detected end plus 30
// seconds. A "Stop now" pressed by the user keeps everything; nothing else trims.
namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// "When the meeting ends": Off, Remind only, or Warn, then stop (the default; the owner, 2026-10-15). FolderLayout's
    /// shape: a stable id is stored, the Radio row shows the owner's words, and a value that is no option reads as the
    /// default. R3, the length limit, applies whatever this says.
    /// </summary>
    internal static class MeetingEndMode
    {
        public const string Off = "off";
        public const string Remind = "remind";
        public const string Stop = "stop";
        public const string SettingKey = "meetingEndMode";
        public const string Default = Stop;

        public static bool IsKnown(string id) { return id == Off || id == Remind || id == Stop; }

        /// <summary>The three options in the owner's words, in the order they appear.</summary>
        public static string[] Displays() { return new[] { "Off", "Remind only", "Warn, then stop" }; }

        public static string ToDisplay(string id)
        {
            string[] displays = Displays();
            if (id == Off) return displays[0];
            if (id == Remind) return displays[1];
            return displays[2];
        }

        /// <summary>The id behind an option, or null for text that is none: an unmatched Radio row hands back "", and
        /// that must store nothing.</summary>
        public static string FromDisplay(string display)
        {
            string[] displays = Displays();
            if (string.Equals(display, displays[0], StringComparison.Ordinal)) return Off;
            if (string.Equals(display, displays[1], StringComparison.Ordinal)) return Remind;
            if (string.Equals(display, displays[2], StringComparison.Ordinal)) return Stop;
            return null;
        }

        /// <summary>The stored mode, or the default for anything else.</summary>
        public static string Read(string stored) { return IsKnown(stored) ? stored : Default; }
    }

    /// <summary>Every threshold the watch uses, in one place (the owner's figures of 2026-10-15 where he gave one).</summary>
    internal static class WatchTuning
    {
        // ---- what counts as talking (ActivityDetector) ----
        /// <summary>A second is active when its RMS is this far above the track's noise floor...</summary>
        internal const float ActiveAboveFloorDb = 10f;
        /// <summary>...and at least this loud: a near-silent room with a floor at -110 dB is not talking at -100.</summary>
        internal const float ActiveAtLeastDb = -55f;
        /// <summary>The floor follows a quieter second at once and a louder one at most this fast, so speech, whose quietest
        /// frames sit above the room, cannot drag the floor up to itself within a sentence.</summary>
        internal const float FloorRisePerSecondDb = 0.2f;
        internal const float FloorLowestDb = -120f;
        internal const float FloorHighestDb = -30f;
        /// <summary>"Talking" is at least TalkActiveSeconds active seconds among the last TalkWindowSeconds.</summary>
        internal const int TalkWindowSeconds = 10;
        internal const int TalkActiveSeconds = 3;
        /// <summary>A track is talking NOW when it talked within this many seconds of the clock: the meter closes a
        /// second only once it has a whole one, and the watch ticks every two.</summary>
        internal const int TalkingNowSeconds = 3;

        // ---- R1: the meeting looks over ----
        internal static readonly TimeSpan Silence = TimeSpan.FromSeconds(300);
        internal static readonly TimeSpan SilenceInCall = TimeSpan.FromSeconds(900);
        internal static readonly TimeSpan AppHeldAtLeast = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan AppReleasedFor = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan SystemQuietFor = TimeSpan.FromSeconds(20);
        /// <summary>The unattended trim keeps this much past the detected end.</summary>
        internal static readonly TimeSpan TrimAfterEnd = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan MeetingOverTimeout = TimeSpan.FromMinutes(2);

        // ---- R2: the calendar end passed ----
        internal static readonly TimeSpan PastEndTimeout = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan PastEndAskAgain = TimeSpan.FromMinutes(10);

        // ---- R3: the length limit ----
        internal const int LimitHoursDefault = 3;
        internal const int LimitHoursMin = 1;
        internal const int LimitHoursMax = 8;
        internal static readonly TimeSpan LimitTimeout = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan LimitExtension = TimeSpan.FromMinutes(30);

        // ---- R4: the next meeting ----
        internal static readonly TimeSpan NextMeetingTimeout = TimeSpan.FromMinutes(5);

        // ---- R5: the offer to record ----
        internal static readonly TimeSpan OfferAppHeldAtLeast = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan OfferStartLead = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan OfferTimeout = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan OfferMeetingWithNoEnd = TimeSpan.FromMinutes(60);
        /// <summary>The host takes a Deadline from 5 s to 24 h ahead; an offer closer to its end than this is not made.</summary>
        internal static readonly TimeSpan OfferLeastLeft = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan OfferLatestDeadline = TimeSpan.FromHours(23);

        // ---- the module's side ----
        /// <summary>R1 and R3 still open this long past their Timeout: the module withdraws them and applies the
        /// no-answer outcome itself, so a host that never answers cannot leave a forgotten recording running.</summary>
        internal static readonly TimeSpan Backstop = TimeSpan.FromSeconds(60);
        /// <summary>After the host refuses a question, the same question is not put again for this long.</summary>
        internal static readonly TimeSpan RefusedBackoff = TimeSpan.FromSeconds(60);
        /// <summary>A meeting's name inside a question is cut to this many characters (the host takes 240 in all).</summary>
        internal const int NameInQuestion = 80;
    }

    /// <summary>
    /// Whether one track has someone talking on it, from its per-second loudness (TrackMeter). The noise floor starts at
    /// the first second's quietest frame, follows a quieter one at once and a louder one at most 0.2 dB a second, and is
    /// held between -120 and -30 dB. A second is active when its RMS reaches max(floor + 10, -55) dB, and the track is
    /// talking when at least 3 of the last 10 seconds were active. A steady sound is the floor, not talking.
    /// </summary>
    internal sealed class ActivityDetector
    {
        private float _floor;
        private bool _floorSet;
        private int _lastIndex = -1;
        private readonly bool[] _active = new bool[WatchTuning.TalkWindowSeconds];
        private readonly int[] _activeIndex = new int[WatchTuning.TalkWindowSeconds];

        internal ActivityDetector()
        {
            for (int i = 0; i < _activeIndex.Length; i++) _activeIndex[i] = -1;
        }

        /// <summary>The last second (the track's own index) at which the track counted as talking, or -1.</summary>
        internal int LastTalkingIndex { get; private set; } = -1;
        internal float Floor { get { return _floor; } }

        internal void Feed(LevelSecond s)
        {
            if (s.Index <= _lastIndex) return;   // a second already seen
            float min = Clamp(s.MinFrameDb);
            if (!_floorSet) { _floor = min; _floorSet = true; }
            else if (min < _floor) _floor = min;
            else _floor = Clamp(Math.Min(min, _floor + WatchTuning.FloorRisePerSecondDb * (s.Index - _lastIndex)));
            bool active = s.RmsDb >= Math.Max(_floor + WatchTuning.ActiveAboveFloorDb, WatchTuning.ActiveAtLeastDb);
            int slot = s.Index % _active.Length;
            _active[slot] = active;
            _activeIndex[slot] = s.Index;
            _lastIndex = s.Index;
            int count = 0;
            for (int i = 0; i < _active.Length; i++)
                if (_active[i] && _activeIndex[i] > s.Index - WatchTuning.TalkWindowSeconds) count++;
            if (count >= WatchTuning.TalkActiveSeconds) LastTalkingIndex = s.Index;
        }

        private static float Clamp(float db)
        {
            if (db < WatchTuning.FloorLowestDb) return WatchTuning.FloorLowestDb;
            if (db > WatchTuning.FloorHighestDb) return WatchTuning.FloorHighestDb;
            return db;
        }
    }

    /// <summary>The questions, by priority; the numbers are the order in which one pre-empts another.</summary>
    internal enum WatchRule
    {
        None = 0,
        PastEnd = 1,       // R2
        NextMeeting = 2,   // R4
        MeetingOver = 3,   // R1
        Limit = 4,         // R3
        Offer = 5,         // R5, idle only, never competes with the others
    }

    /// <summary>A question to put, and the serial its answer comes back under.</summary>
    internal sealed class WatchAsk
    {
        internal WatchRule Rule;
        internal int Serial;
        internal TrayQuestion Question;
        /// <summary>What set it off, for the log: "Teams let go of the microphone", "no talking for 300 s". Never a meeting's name.</summary>
        internal string Note = "";
    }

    /// <summary>What a tick wants done: withdraw the question showing (WithdrawSerial non-zero), ask one, or both.</summary>
    internal sealed class WatchStep
    {
        internal int WithdrawSerial;
        internal WatchAsk Ask;
        internal bool IsEmpty { get { return WithdrawSerial == 0 && Ask == null; } }
    }

    internal enum WatchActionKind { None = 0, Stop, Split, Record }

    /// <summary>What an answer means for the recording. Reason is for the log, so it names no meeting.</summary>
    internal sealed class WatchAction
    {
        internal static readonly WatchAction Nothing = new WatchAction { Kind = WatchActionKind.None };
        internal WatchActionKind Kind;
        internal TimeSpan? KeepUntil;
        internal string Reason = "";
        internal MeetingContext Meeting;
    }

    internal static class WatchQuestions
    {
        internal const string MeetingOverKey = "remembrance.meeting-over";
        internal const string PastEndKey = "remembrance.past-end";
        internal const string LimitKey = "remembrance.limit";
        internal const string NextMeetingKey = "remembrance.next-meeting";
        internal const string OfferKey = "remembrance.offer-record";

        internal static string KeyOf(WatchRule rule)
        {
            switch (rule)
            {
                case WatchRule.MeetingOver: return MeetingOverKey;
                case WatchRule.PastEnd: return PastEndKey;
                case WatchRule.Limit: return LimitKey;
                case WatchRule.NextMeeting: return NextMeetingKey;
                case WatchRule.Offer: return OfferKey;
                default: return "";
            }
        }

        /// <summary>A meeting's name for a question: cut to WatchTuning.NameInQuestion with an ellipsis, never empty.</summary>
        internal static string NameFor(string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) return "the meeting";
            if (n.Length <= WatchTuning.NameInQuestion) return n;
            return n.Substring(0, WatchTuning.NameInQuestion - 1).TrimEnd() + "…";
        }

        internal static TrayQuestion Build(string key, string text, TimeSpan timeout, string timeoutChoice, bool runsWhileHeld,
            DateTimeOffset? deadline, params TrayChoice[] choices)
        {
            // No Title: the card already names the module, from the host's own record of it (PluginApi.cs).
            return new TrayQuestion
            {
                Key = key,
                Text = text,
                Choices = choices,
                TimeoutChoiceId = timeoutChoice,
                Timeout = timeout,
                RunsWhileHeld = runsWhileHeld,
                Deadline = deadline,
            };
        }
    }

    /// <summary>
    /// One recording's watch. Built when the recording starts, with the meeting it started in (the anchor; empty when
    /// none was current), fed each tick, and dropped at the stop. Holds which question is showing and which are spent.
    /// UI thread only.
    /// </summary>
    internal sealed class RecordingWatch
    {
        internal readonly DateTimeOffset StartedAt;
        private MeetingContext _anchor;
        private readonly Dictionary<string, ActivityDetector> _tracks = new Dictionary<string, ActivityDetector>(StringComparer.Ordinal);
        private readonly HashSet<string> _systemTracks = new HashSet<string>(StringComparer.Ordinal);
        private bool _unmeasured;

        private sealed class Asked
        {
            internal WatchRule Rule;
            internal string Trigger = "";        // R1: "app" or "silence"
            internal MeetingApp App;             // R1a
            internal long ReleaseTicks;          // R1a: the release asked about
            internal DateTimeOffset? DetectedEnd;
            internal string Mode = MeetingEndMode.Default;
            internal string TimeoutChoice = "";
            internal MeetingContext Meeting;     // R4
            internal TimeSpan Timeout;
            internal DateTimeOffset AskedAt;
        }

        private int _serial;
        private int _openSerial;
        private readonly Dictionary<int, Asked> _asked = new Dictionary<int, Asked>();

        // Spent and re-armed.
        private readonly HashSet<long> _r1SpentReleases = new HashSet<long>();
        private bool _r1SilenceSpent;
        private int _r1SilenceSpentMark = -1;
        private bool _r2Done;
        private DateTimeOffset _r2NotBefore = DateTimeOffset.MinValue;
        private DateTimeOffset? _limitExtendedUntil;
        private readonly HashSet<string> _r4Spent = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<WatchRule, DateTimeOffset> _refusedUntil = new Dictionary<WatchRule, DateTimeOffset>();

        internal RecordingWatch(DateTimeOffset startedAt, MeetingContext anchor)
        {
            StartedAt = startedAt;
            _anchor = anchor ?? new MeetingContext();
        }

        internal MeetingContext Anchor { get { return _anchor; } }

        /// <summary>The rule showing now, or None.</summary>
        internal WatchRule Open { get { Asked a; return _openSerial != 0 && _asked.TryGetValue(_openSerial, out a) ? a.Rule : WatchRule.None; } }
        internal int OpenSerial { get { return _openSerial; } }

        /// <summary>When the open question was asked and how long it waits, for the module's backstop.</summary>
        internal bool OpenDeadline(out DateTimeOffset askedAt, out TimeSpan timeout)
        {
            Asked a;
            askedAt = default(DateTimeOffset);
            timeout = TimeSpan.Zero;
            if (_openSerial == 0 || !_asked.TryGetValue(_openSerial, out a)) return false;
            askedAt = a.AskedAt;
            timeout = a.Timeout;
            return true;
        }

        internal string OpenTimeoutChoice { get { Asked a; return _openSerial != 0 && _asked.TryGetValue(_openSerial, out a) ? a.TimeoutChoice : null; } }

        /// <summary>Declare a track and whether its loudness is measured. A track that is not (written in its device's
        /// format) leaves R1b's silence undecidable, so R1b is off for this recording.</summary>
        internal void AddTrack(string name, bool loopback, bool measured)
        {
            if (string.IsNullOrEmpty(name) || _tracks.ContainsKey(name)) return;
            _tracks[name] = new ActivityDetector();
            if (loopback) _systemTracks.Add(name);
            if (!measured) _unmeasured = true;
        }

        internal void Feed(string track, IEnumerable<LevelSecond> seconds)
        {
            ActivityDetector d;
            if (seconds == null || track == null || !_tracks.TryGetValue(track, out d)) return;
            foreach (LevelSecond s in seconds) d.Feed(s);
        }

        // ---- time on the tracks' own clock ----
        // A LevelSecond's Index counts the track's own seconds from its first sample, which starts with the recording; a
        // track that stops delivering stops advancing, and the CLOCK keeps going, so its silence grows (a second a track
        // never delivered counts as silent).
        private DateTimeOffset? LastTalkingAt(ActivityDetector d)
        {
            if (d.LastTalkingIndex < 0) return null;
            return StartedAt + TimeSpan.FromSeconds(d.LastTalkingIndex + 1);
        }

        private DateTimeOffset? LastTalkingAt()
        {
            DateTimeOffset? latest = null;
            foreach (ActivityDetector d in _tracks.Values)
            {
                DateTimeOffset? at = LastTalkingAt(d);
                if (at.HasValue && (!latest.HasValue || at.Value > latest.Value)) latest = at;
            }
            return latest;
        }

        private int TalkMark()
        {
            int mark = -1;
            foreach (ActivityDetector d in _tracks.Values) mark = Math.Max(mark, d.LastTalkingIndex);
            return mark;
        }

        internal bool TalkingNow(DateTimeOffset now)
        {
            DateTimeOffset? last = LastTalkingAt();
            return last.HasValue && now - last.Value <= TimeSpan.FromSeconds(WatchTuning.TalkingNowSeconds);
        }

        private bool SystemQuiet(DateTimeOffset now, TimeSpan forAtLeast)
        {
            foreach (string name in _systemTracks)
            {
                DateTimeOffset? last = LastTalkingAt(_tracks[name]);
                if (now - (last ?? StartedAt) < forAtLeast) return false;
            }
            return true;
        }

        // ---- the rules ----

        private sealed class Candidate
        {
            internal WatchRule Rule;
            internal string Trigger = "";
            internal MeetingApp App;
            internal long ReleaseTicks;
            internal DateTimeOffset? DetectedEnd;
            internal MeetingContext Meeting;
        }

        private bool BackedOff(WatchRule rule, DateTimeOffset now)
        {
            DateTimeOffset until;
            return _refusedUntil.TryGetValue(rule, out until) && now < until;
        }

        private DateTimeOffset LimitDue(int limitHours)
        {
            int hours = Math.Max(WatchTuning.LimitHoursMin, Math.Min(WatchTuning.LimitHoursMax, limitHours));
            DateTimeOffset due = StartedAt + TimeSpan.FromHours(hours);
            if (_limitExtendedUntil.HasValue && _limitExtendedUntil.Value > due) due = _limitExtendedUntil.Value;
            return due;
        }

        private Candidate LimitCandidate(DateTimeOffset now, int limitHours)
        {
            if (now < LimitDue(limitHours) || BackedOff(WatchRule.Limit, now)) return null;
            return new Candidate { Rule = WatchRule.Limit };
        }

        private Candidate MeetingOverCandidate(DateTimeOffset now, string mode, MicUseSnapshot mic)
        {
            if (mode == MeetingEndMode.Off || BackedOff(WatchRule.MeetingOver, now)) return null;
            // R1a: Teams or Zoom let go of the microphone after holding it for at least a minute of this recording, has
            // stayed off it for 15 s, no meeting app holds it now, and the system output has been quiet for 20 s (so a
            // call that moved to the speakers' side is not over). The end is the moment it let go.
            if (mic != null && mic.Available && !mic.AnyInUse && SystemQuiet(now, WatchTuning.SystemQuietFor))
            {
                foreach (MeetingApp app in new[] { MeetingApp.Teams, MeetingApp.Zoom })
                {
                    AppMicUse use = mic.For(app);
                    if (use == null || use.InUse || !use.LastStopUtc.HasValue || !use.LastStartUtc.HasValue) continue;
                    DateTimeOffset stop = use.LastStopUtc.Value;
                    DateTimeOffset start = use.LastStartUtc.Value;
                    DateTimeOffset from = start > StartedAt ? start : StartedAt;
                    DateTimeOffset to = stop < now ? stop : now;
                    if (to - from < WatchTuning.AppHeldAtLeast) continue;
                    if (now - stop < WatchTuning.AppReleasedFor) continue;
                    if (_r1SpentReleases.Contains(stop.UtcTicks)) continue;
                    return new Candidate { Rule = WatchRule.MeetingOver, Trigger = "app", App = app, ReleaseTicks = stop.UtcTicks, DetectedEnd = stop };
                }
            }
            // R1b: nobody has talked on any track for 5 minutes, or 15 while a meeting app still holds the microphone (a
            // quiet stretch of a call that is still going); and either someone talked earlier in this recording or the
            // meeting's calendar end has passed, so a silent lobby before the meeting starts asks nothing. A track whose
            // loudness is not measured makes the silence unknowable, and an unreadable microphone record counts as in a
            // call, the longer wait.
            if (_unmeasured || _tracks.Count == 0) return null;
            DateTimeOffset? lastTalk = LastTalkingAt();
            bool inCall = mic == null || !mic.Available || mic.AnyInUse;
            TimeSpan window = inCall ? WatchTuning.SilenceInCall : WatchTuning.Silence;
            bool endPassed = _anchor.EndUtc.HasValue && now >= _anchor.EndUtc.Value;
            if (now - (lastTalk ?? StartedAt) < window) return null;
            if (!lastTalk.HasValue && !endPassed) return null;
            if (_r1SilenceSpent && TalkMark() <= _r1SilenceSpentMark) return null;
            DateTimeOffset end = lastTalk ?? (_anchor.EndUtc.Value > StartedAt ? _anchor.EndUtc.Value : StartedAt);
            return new Candidate { Rule = WatchRule.MeetingOver, Trigger = "silence", DetectedEnd = end };
        }

        private Candidate NextMeetingCandidate(DateTimeOffset now, string mode, MeetingContext current)
        {
            if (mode == MeetingEndMode.Off || BackedOff(WatchRule.NextMeeting, now)) return null;
            if (current == null || current.IsEmpty || !current.StartUtc.HasValue) return null;
            string key = current.MeetingKey;
            if (key == _anchor.MeetingKey || _r4Spent.Contains(key)) return null;
            // Later than the meeting the recording started in, or than the recording itself when it started in none; and
            // started. Reminder publishes a meeting five minutes before it starts, so this waits for the start.
            DateTimeOffset since = _anchor.StartUtc ?? StartedAt;
            if (current.StartUtc.Value <= since || now < current.StartUtc.Value) return null;
            return new Candidate { Rule = WatchRule.NextMeeting, Meeting = current };
        }

        private Candidate PastEndCandidate(DateTimeOffset now, string mode)
        {
            if (mode == MeetingEndMode.Off || BackedOff(WatchRule.PastEnd, now)) return null;
            // Only a calendar end the meeting really has: an event with none never asks, rather than one being invented.
            if (_r2Done || !_anchor.EndUtc.HasValue || now < _anchor.EndUtc.Value || now < _r2NotBefore) return null;
            return new Candidate { Rule = WatchRule.PastEnd };
        }

        private Candidate Best(DateTimeOffset now, string mode, int limitHours, MicUseSnapshot mic, MeetingContext current)
        {
            return LimitCandidate(now, limitHours)
                ?? MeetingOverCandidate(now, mode, mic)
                ?? NextMeetingCandidate(now, mode, current)
                ?? PastEndCandidate(now, mode);
        }

        // Whether the question showing still applies. One that no longer does is withdrawn, and is not spent.
        private bool StillHolds(Asked open, DateTimeOffset now, string mode, int limitHours, MicUseSnapshot mic, MeetingContext current)
        {
            switch (open.Rule)
            {
                case WatchRule.Limit:
                    return now >= LimitDue(limitHours);
                case WatchRule.MeetingOver:
                    if (mode == MeetingEndMode.Off) return false;
                    if (open.Trigger == "app")
                    {
                        // Withdrawn when the app takes the microphone again: the call went on.
                        if (mic == null || !mic.Available) return true;
                        AppMicUse use = mic.For(open.App);
                        return use == null || !use.InUse;
                    }
                    // Withdrawn when talking resumes on any track.
                    return !TalkingNow(now);
                case WatchRule.NextMeeting:
                    return mode != MeetingEndMode.Off && current != null && open.Meeting != null
                           && current.MeetingKey == open.Meeting.MeetingKey;
                case WatchRule.PastEnd:
                    return mode != MeetingEndMode.Off && !_r2Done;
                default:
                    return false;
            }
        }

        /// <summary>
        /// One tick. Adopts a meeting that was already under way when the recording started but was published only
        /// afterwards, then withdraws the question showing if it no longer applies or something more urgent does, and
        /// asks the most urgent question that applies when none is showing.
        /// </summary>
        internal WatchStep Tick(DateTimeOffset now, string mode, int limitHours, MicUseSnapshot mic, MeetingContext current)
        {
            mode = MeetingEndMode.Read(mode);
            _limitHoursSeen = Math.Max(WatchTuning.LimitHoursMin, Math.Min(WatchTuning.LimitHoursMax, limitHours));
            var step = new WatchStep();
            if (_anchor.IsEmpty && current != null && !current.IsEmpty && current.StartUtc.HasValue && current.StartUtc.Value <= StartedAt)
                _anchor = current;

            Candidate best = Best(now, mode, limitHours, mic, current);
            Asked open;
            if (_openSerial != 0 && _asked.TryGetValue(_openSerial, out open))
            {
                bool holds = StillHolds(open, now, mode, limitHours, mic, current);
                bool preempted = best != null && (int)best.Rule > (int)open.Rule;
                if (holds && !preempted) return step;
                step.WithdrawSerial = _openSerial;
                _openSerial = 0;
            }
            if (best == null) return step;
            step.Ask = Ask(best, now, mode);
            return step;
        }

        private WatchAsk Ask(Candidate c, DateTimeOffset now, string mode)
        {
            var asked = new Asked
            {
                Rule = c.Rule,
                Trigger = c.Trigger,
                App = c.App,
                ReleaseTicks = c.ReleaseTicks,
                DetectedEnd = c.DetectedEnd,
                Mode = mode,
                Meeting = c.Meeting,
                AskedAt = now,
            };
            TrayQuestion q;
            var keep = new TrayChoice("keep", "Keep recording");
            var stop = new TrayChoice("stop", "Stop now");
            switch (c.Rule)
            {
                case WatchRule.Limit:
                    q = WatchQuestions.Build(WatchQuestions.LimitKey,
                        "Recording has reached the " + _limitHoursSeen.ToString(CultureInfo.InvariantCulture) + "-hour limit.",
                        WatchTuning.LimitTimeout, "stop", true, null,
                        new TrayChoice("extend", "Keep 30 more min"), stop);
                    break;
                case WatchRule.MeetingOver:
                    bool remind = mode == MeetingEndMode.Remind;
                    q = WatchQuestions.Build(WatchQuestions.MeetingOverKey,
                        remind ? "Your meeting looks over. Still recording." : "Your meeting looks over. Keep recording?",
                        WatchTuning.MeetingOverTimeout, remind ? "keep" : "stop", true, null, keep, stop);
                    break;
                case WatchRule.NextMeeting:
                    q = WatchQuestions.Build(WatchQuestions.NextMeetingKey,
                        "\"" + WatchQuestions.NameFor(c.Meeting.Name) + "\" has started.",
                        WatchTuning.NextMeetingTimeout, "one", false, null,
                        new TrayChoice("split", "Start new recording"), new TrayChoice("one", "Keep as one"), new TrayChoice("stop", "Stop"));
                    break;
                default:   // PastEnd
                    q = WatchQuestions.Build(WatchQuestions.PastEndKey,
                        "The meeting was due to end at " +
                        _anchor.EndUtc.Value.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture) + ". Still recording.",
                        WatchTuning.PastEndTimeout, "later", false, null,
                        keep, stop, new TrayChoice("later", "Ask in 10 min"));
                    break;
            }
            asked.TimeoutChoice = q.TimeoutChoiceId;
            asked.Timeout = q.Timeout;
            int serial = ++_serial;
            _asked[serial] = asked;
            _openSerial = serial;
            string note = "";
            if (c.Rule == WatchRule.MeetingOver)
            {
                DateTimeOffset? lastTalk = LastTalkingAt();
                note = c.Trigger == "app"
                    ? MeetingAppMicrophone.DisplayName(c.App) + " let go of the microphone"
                    : lastTalk.HasValue
                        ? "no talking for " + ((long)(now - lastTalk.Value).TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s"
                        : "nobody has talked, and the calendar end has passed";
            }
            return new WatchAsk { Rule = c.Rule, Serial = serial, Question = q, Note = note };
        }

        // "Longest recording" as the last tick read it, for R3's text and its log line; after an extension it is still the
        // setting's hours that were reached.
        private int _limitHoursSeen = WatchTuning.LimitHoursDefault;

        /// <summary>
        /// What an answer means. An answer for the question showing is applied, a no-answer outcome (TimedOut, Expired)
        /// as its TimeoutChoiceId; a question the watch itself took down (pre-empted, or its trigger went away) still
        /// honours a button the user pressed in the same instant, the race the ABI warns of, and nothing else. Withdrawn
        /// and Cancelled change nothing, so a question withdrawn is not spent; Refused holds that question back for a
        /// minute rather than putting it again every tick. Each serial is applied at most once.
        /// </summary>
        internal WatchAction Answer(int serial, TrayQuestionOutcome outcome, string choiceId, DateTimeOffset now)
        {
            Asked a;
            if (!_asked.TryGetValue(serial, out a)) return WatchAction.Nothing;
            bool open = serial == _openSerial;
            if (open) _openSerial = 0;
            bool chosen = outcome == TrayQuestionOutcome.Chosen;
            bool defaulted = outcome == TrayQuestionOutcome.TimedOut || outcome == TrayQuestionOutcome.Expired;
            if (outcome == TrayQuestionOutcome.Refused)
            {
                _asked.Remove(serial);
                _refusedUntil[a.Rule] = now + WatchTuning.RefusedBackoff;
                return WatchAction.Nothing;
            }
            if (!chosen && !(open && defaulted)) return WatchAction.Nothing;
            _asked.Remove(serial);
            string choice = chosen ? choiceId : (string.IsNullOrEmpty(choiceId) ? a.TimeoutChoice : choiceId);
            switch (a.Rule)
            {
                case WatchRule.Limit:
                    if (choice == "extend")
                    {
                        _limitExtendedUntil = now + WatchTuning.LimitExtension;
                        return WatchAction.Nothing;
                    }
                    if (choice != "stop") return WatchAction.Nothing;
                    return new WatchAction
                    {
                        Kind = WatchActionKind.Stop,
                        Reason = "the " + _limitHoursSeen.ToString(CultureInfo.InvariantCulture) + "-hour limit was reached" +
                                 (chosen ? " and you chose Stop now" : " and nobody answered"),
                    };
                case WatchRule.MeetingOver:
                    if (choice == "stop")
                    {
                        // THE ONE TRIM: no answer, in "Warn, then stop". A "Stop now" the user pressed keeps everything.
                        TimeSpan? keepUntil = null;
                        if (!chosen && a.Mode == MeetingEndMode.Stop && a.DetectedEnd.HasValue)
                            keepUntil = a.DetectedEnd.Value + WatchTuning.TrimAfterEnd - StartedAt;
                        string why = a.Trigger == "app"
                            ? MeetingAppMicrophone.DisplayName(a.App) + " let go of the microphone"
                            : "nobody had talked for a while";
                        return new WatchAction
                        {
                            Kind = WatchActionKind.Stop,
                            KeepUntil = keepUntil,
                            Reason = "the meeting looked over (" + why + ")" + (chosen ? " and you chose Stop now" : " and nobody answered"),
                        };
                    }
                    if (a.Trigger == "app") _r1SpentReleases.Add(a.ReleaseTicks);
                    else
                    {
                        _r1SilenceSpent = true;
                        _r1SilenceSpentMark = TalkMark();
                    }
                    // A user who has just pressed Keep recording on "your meeting looks over" past the calendar end has
                    // answered R2's question too; asking it on the next tick would be the same question twice.
                    if (chosen && _anchor.EndUtc.HasValue && now >= _anchor.EndUtc.Value) _r2Done = true;
                    return WatchAction.Nothing;
                case WatchRule.NextMeeting:
                    if (a.Meeting != null) _r4Spent.Add(a.Meeting.MeetingKey);
                    if (choice == "split")
                        return new WatchAction { Kind = WatchActionKind.Split, Meeting = a.Meeting, Reason = "a new meeting started and you chose a new recording" };
                    if (choice == "stop")
                        return new WatchAction { Kind = WatchActionKind.Stop, Reason = "a new meeting started and you chose Stop" };
                    // Keep as one: the recording now runs into the new meeting, so its calendar end is the one R2 waits
                    // for and the one R1's silent-lobby rule reads. The file keeps its name.
                    if (a.Meeting != null)
                    {
                        _anchor = a.Meeting;
                        _r2Done = false;
                        _r2NotBefore = DateTimeOffset.MinValue;
                    }
                    return WatchAction.Nothing;
                default:   // PastEnd
                    if (choice == "keep") { _r2Done = true; return WatchAction.Nothing; }
                    if (choice == "stop")
                        return new WatchAction { Kind = WatchActionKind.Stop, Reason = "the meeting's calendar end passed and you chose Stop now" };
                    _r2NotBefore = now + WatchTuning.PastEndAskAgain;
                    return WatchAction.Nothing;
            }
        }
    }

    /// <summary>
    /// R5, the offer to record (opt-in, off by default: the owner, 2026-10-15). With nothing recording, the setting on and
    /// a calendar meeting current, once Teams or Zoom has held the microphone for 20 seconds -- having taken it no earlier
    /// than five minutes before the meeting's start, so a call left open from an earlier meeting does not count -- it asks
    /// once per meeting whether to record. Withdrawn when the app lets go of the microphone (not spent: rejoining asks
    /// again) or a recording starts. The question's Deadline is the meeting's end, or its start plus an hour when the
    /// calendar gives none. Module lifetime, UI thread only.
    /// </summary>
    internal sealed class OfferWatch
    {
        private readonly HashSet<string> _spent = new HashSet<string>(StringComparer.Ordinal);
        private int _serial;
        private int _openSerial;
        private string _openKey;
        private MeetingApp _openApp;
        private readonly Dictionary<int, MeetingContext> _meetings = new Dictionary<int, MeetingContext>();
        private DateTimeOffset _refusedUntil = DateTimeOffset.MinValue;

        internal int OpenSerial { get { return _openSerial; } }

        internal WatchStep Tick(DateTimeOffset now, bool enabled, bool recording, MeetingContext current, MicUseSnapshot mic)
        {
            var step = new WatchStep();
            if (_openSerial != 0)
            {
                AppMicUse openUse = mic != null && mic.Available ? mic.For(_openApp) : null;
                bool holds = enabled && !recording && current != null && current.MeetingKey == _openKey
                             && (mic == null || !mic.Available || (openUse != null && openUse.InUse));
                if (holds) return step;
                step.WithdrawSerial = _openSerial;
                if (recording && _openKey != null) _spent.Add(_openKey);
                _openSerial = 0;
            }
            if (!enabled || recording || now < _refusedUntil) return step;
            if (current == null || current.IsEmpty || !current.StartUtc.HasValue) return step;
            if (mic == null || !mic.Available) return step;
            string key = current.MeetingKey;
            if (_spent.Contains(key)) return step;
            MeetingApp app = mic.InUseApp;
            if (app == MeetingApp.None) return step;
            AppMicUse use = mic.For(app);
            if (use == null || !use.LastStartUtc.HasValue) return step;
            if (now - use.LastStartUtc.Value < WatchTuning.OfferAppHeldAtLeast) return step;
            if (use.LastStartUtc.Value < current.StartUtc.Value - WatchTuning.OfferStartLead) return step;
            DateTimeOffset deadline = current.EndUtc ?? current.StartUtc.Value + WatchTuning.OfferMeetingWithNoEnd;
            if (deadline - now < WatchTuning.OfferLeastLeft) return step;
            if (deadline - now > WatchTuning.OfferLatestDeadline) deadline = now + WatchTuning.OfferLatestDeadline;
            TrayQuestion q = WatchQuestions.Build(WatchQuestions.OfferKey,
                MeetingAppMicrophone.DisplayName(app) + " is using the microphone for \"" + WatchQuestions.NameFor(current.Name) + "\". Record it?",
                WatchTuning.OfferTimeout, "skip", false, deadline,
                new TrayChoice("record", "Record"), new TrayChoice("skip", "Not this one"));
            int serial = ++_serial;
            _openSerial = serial;
            _openKey = key;
            _openApp = app;
            _meetings[serial] = current;
            step.Ask = new WatchAsk { Rule = WatchRule.Offer, Serial = serial, Question = q };
            return step;
        }

        /// <summary>A recording started by hand: the offer showing is withdrawn by the next tick and spent then.</summary>
        internal void NoteRecordingStarted()
        {
            if (_openSerial != 0 && _openKey != null) _spent.Add(_openKey);
        }

        /// <summary>
        /// What an answer means: Record (the meeting to name the recording after) or nothing. Any answer to the offer
        /// showing spends it for that meeting; a Record pressed on an offer the watch had just withdrawn still counts.
        /// </summary>
        internal MeetingContext Answer(int serial, TrayQuestionOutcome outcome, string choiceId, DateTimeOffset now)
        {
            MeetingContext meeting;
            if (!_meetings.TryGetValue(serial, out meeting)) return null;
            bool open = serial == _openSerial;
            if (open) _openSerial = 0;
            if (outcome == TrayQuestionOutcome.Refused)
            {
                _meetings.Remove(serial);
                _refusedUntil = now + WatchTuning.RefusedBackoff;
                return null;
            }
            bool chosen = outcome == TrayQuestionOutcome.Chosen;
            bool defaulted = outcome == TrayQuestionOutcome.TimedOut || outcome == TrayQuestionOutcome.Expired;
            if (!chosen && !(open && defaulted)) return null;
            _meetings.Remove(serial);
            _spent.Add(meeting.MeetingKey);
            return chosen && choiceId == "record" ? meeting : null;
        }
    }
}
