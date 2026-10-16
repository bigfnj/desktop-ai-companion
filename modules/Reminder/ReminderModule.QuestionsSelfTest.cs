using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.MeetingApps;
using DesktopAICompanion.ModuleKit.Testing;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.ReminderModule
{
    // The self-test half of ReminderModule.Questions.cs (1.1.0): the join question at a meeting's start (M3), Done / Snooze
    // on personal reminders (M4, M5), the lead-time line they let go of its tray sentence (M1/M2), and the personal line's
    // two new fields. Everything runs on the module's own tick with the clock stepped through ReminderClock, on a
    // RecordingHost that also answers ITrayQuestions -- the pattern AI Brain's StandDownHost set for ICompanionStandDown;
    // ModuleKit carries no double for it, on purpose (PluginApi.cs).
    public sealed partial class ReminderModule
    {
        /// <summary>
        /// A RecordingHost that answers ITrayQuestions. It keeps a copy of every question asked, as the host does, and
        /// REFUSES one outside the ABI's rules with the host's kind of reason, so a question this module could not
        /// actually put on screen fails the self-test rather than a user's afternoon. Answers are given by the test, on
        /// the test's thread, which stands for the UI thread the host completes on.
        /// </summary>
        internal sealed class QuestionsHost : RecordingHost, ITrayQuestions
        {
            internal sealed class Asked
            {
                internal string ModuleId;
                internal TrayQuestion Question;      // the copy taken at the ask
                internal string Refused;             // the rule it broke, or null
                internal bool Withdrawn;
                internal TaskCompletionSource<TrayAnswer> Answer;
                internal bool Open { get { return Answer != null && !Answer.Task.IsCompleted; } }
            }

            internal readonly List<Asked> Asks = new List<Asked>();

            /// <summary>"Now" for the Deadline rule: the module's clock, so a stepped test clock and the rule agree.</summary>
            internal Func<DateTimeOffset> Clock = () => DateTimeOffset.Now;

            private static readonly Regex KeyRule = new Regex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);
            private static readonly Regex ChoiceIdRule = new Regex("^[A-Za-z0-9._-]{1,32}$", RegexOptions.CultureInvariant);

            public Task<TrayAnswer> AskAsync(string moduleId, TrayQuestion question, CancellationToken withdraw)
            {
                var asked = new Asked { ModuleId = moduleId, Question = Copy(question) };
                asked.Refused = Rule(moduleId, asked.Question);
                Asks.Add(asked);
                if (asked.Refused != null)
                    return Task.FromResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Refused, Reason = asked.Refused });
                asked.Answer = new TaskCompletionSource<TrayAnswer>();
                withdraw.Register(delegate
                {
                    if (asked.Answer.Task.IsCompleted) return;
                    asked.Withdrawn = true;
                    asked.Answer.TrySetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Withdrawn });
                });
                return asked.Answer.Task;
            }

            /// <summary>The rules PluginApi.cs gives TrayQuestion, plus the host's duplicate-key and four-pending limits.
            /// Null when the question would be taken.</summary>
            private string Rule(string moduleId, TrayQuestion q)
            {
                if (!string.Equals(moduleId, Id, StringComparison.Ordinal)) return "unknown-module";
                if (q == null) return "no-question";
                if (q.Key == null || !KeyRule.IsMatch(q.Key)) return "bad-key";
                if (q.Title != null && (q.Title.Length > 64 || q.Title.Any(char.IsControl))) return "bad-title";
                if (string.IsNullOrWhiteSpace(q.Text)) return "no-text";
                if (q.Text.Length > 240 || q.Text.Any(char.IsControl)) return "bad-text";
                if (q.Choices == null || q.Choices.Count < 2) return "too-few-choices";
                if (q.Choices.Count > 4) return "too-many-choices";
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TrayChoice c in q.Choices)
                {
                    if (c == null || c.Id == null || !ChoiceIdRule.IsMatch(c.Id) || !ids.Add(c.Id)) return "bad-choice-id";
                    if (string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > 24 || !labels.Add(c.Label)) return "bad-choice-label";
                }
                if (q.TimeoutChoiceId == null || !ids.Contains(q.TimeoutChoiceId)) return "bad-timeout-choice";
                if (q.Timeout < TimeSpan.FromSeconds(15) || q.Timeout > TimeSpan.FromMinutes(60)) return "bad-timeout";
                if (q.Deadline.HasValue)
                {
                    TimeSpan ahead = q.Deadline.Value - Clock();
                    if (ahead < TimeSpan.FromSeconds(5) || ahead > TimeSpan.FromHours(24)) return "bad-deadline";
                }
                if (Asks.Any(a => a.Open && a.ModuleId == moduleId && a.Question.Key == q.Key)) return "duplicate-key";
                if (Asks.Count(a => a.Open && a.ModuleId == moduleId) >= 4) return "too-many-pending";
                return null;
            }

            private static TrayQuestion Copy(TrayQuestion q)
            {
                if (q == null) return null;
                return new TrayQuestion
                {
                    Key = q.Key,
                    Title = q.Title,
                    Text = q.Text,
                    Choices = q.Choices == null ? null
                        : q.Choices.Select(c => c == null ? null : new TrayChoice(c.Id, c.Label)).ToArray(),
                    TimeoutChoiceId = q.TimeoutChoiceId,
                    Timeout = q.Timeout,
                    Deadline = q.Deadline,
                    RunsWhileHeld = q.RunsWhileHeld,
                };
            }

            /// <summary>Every question of one kind ("join", "personal"), in the order asked.</summary>
            internal List<Asked> Of(string kind)
            {
                return Asks.Where(a => a.Question != null && a.Question.Key != null
                    && a.Question.Key.StartsWith(kind + "-", StringComparison.Ordinal)).ToList();
            }

            /// <summary>The latest question about one thing, or null.</summary>
            internal Asked For(string kind, string id)
            {
                string key = QuestionKey(kind, id);
                return Asks.LastOrDefault(a => a.Question != null && a.Question.Key == key);
            }

            internal List<string> Refusals
            {
                get { return Asks.Where(a => a.Refused != null).Select(a => a.Question.Key + ": " + a.Refused).ToList(); }
            }

            internal void Choose(Asked a, string choiceId)
            {
                if (a != null && a.Answer != null)
                    a.Answer.TrySetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Chosen, ChoiceId = choiceId, WasShown = true });
            }

            /// <summary>Seen and not answered: the host answers TimedOut carrying the question's TimeoutChoiceId.</summary>
            internal void NoAnswer(Asked a)
            {
                if (a != null && a.Answer != null)
                    a.Answer.TrySetResult(new TrayAnswer
                    {
                        Outcome = TrayQuestionOutcome.TimedOut, ChoiceId = a.Question.TimeoutChoiceId, WasShown = true,
                    });
            }

            /// <summary>Ended by its Deadline while held: Expired, carrying TimeoutChoiceId, never seen.</summary>
            internal void Expire(Asked a)
            {
                if (a != null && a.Answer != null)
                    a.Answer.TrySetResult(new TrayAnswer
                    {
                        Outcome = TrayQuestionOutcome.Expired, ChoiceId = a.Question.TimeoutChoiceId, WasShown = false,
                    });
            }
        }

        /// <summary>A SynchronizationContext that queues what is posted to it until the test runs the queue: the stand-in
        /// for a UI thread's message loop, to show an answer completed elsewhere is applied on the thread that asked.</summary>
        private sealed class QueueContext : SynchronizationContext
        {
            private readonly Queue<KeyValuePair<SendOrPostCallback, object>> _posted =
                new Queue<KeyValuePair<SendOrPostCallback, object>>();
            private readonly object _gate = new object();

            public override void Post(SendOrPostCallback d, object state)
            {
                lock (_gate) _posted.Enqueue(new KeyValuePair<SendOrPostCallback, object>(d, state));
            }

            internal int Pending { get { lock (_gate) return _posted.Count; } }

            internal void RunAll()
            {
                while (true)
                {
                    KeyValuePair<SendOrPostCallback, object> next;
                    lock (_gate)
                    {
                        if (_posted.Count == 0) return;
                        next = _posted.Dequeue();
                    }
                    next.Key(next.Value);
                }
            }
        }

        private sealed class TestClock { internal DateTimeOffset Now; }

        /// <summary>
        /// One module on a QuestionsHost, with slot 1 a "Local file" with no path: that slot never loads, so the fired-set
        /// prune on each Init's own tick keeps cal1's ids (the rule for a slot that cannot vouch for its calendar), which
        /// is what lets a restart find the join question's asked mark. Each test then feeds the module a one-slot stub.
        /// </summary>
        private sealed class QuestionRig : IDisposable
        {
            internal readonly QuestionsHost Host = new QuestionsHost();
            internal readonly FakeModuleSettings Settings;
            internal readonly FakeConsentStore Mic = new FakeConsentStore();
            internal readonly List<string> Opened = new List<string>();
            internal ReminderModule Module;

            internal QuestionRig(Action<FakeModuleSettings> seed)
            {
                Settings = Host.SettingsFor(Id);
                Settings.Set("hushPresenting", "false");
                Settings.Set("leads", "5");
                Settings.Set(SlotKey(1, "type"), SourceLocalFile);
                Settings.Set(SlotKey(1, "label"), "Work");
                if (seed != null) seed(Settings);
                Settings.Save();
                Host.Clock = () => ReminderClock();
                Start();
            }

            /// <summary>Init a fresh module on the same host and settings: what the app does at its next start.</summary>
            internal void Start()
            {
                Module = new ReminderModule();
                Module.Init(Host);
                Module.Microphone = new MeetingAppMicrophone(Mic);
                Module.OpenUrlForSelfTest = delegate(string url) { Opened.Add(url); };
            }

            internal void Restart() { Module.Shutdown(); Start(); }

            internal void Feed(params CalendarEvent[] events)
            {
                Module._source = new AggregateCalendarSource(new[]
                {
                    new AggregateCalendarSource.Slot { Id = "cal1", Label = "Work", Source = new AggregateCalendarSource.StubSource(events, null) },
                });
            }

            internal void Tick() { Module.CheckDue(); }

            internal PersonalReminder Personal(string id)
            {
                foreach (string line in Settings.Get("personal", "").Split('\n'))
                {
                    PersonalReminder r = PersonalReminder.Decode(line.Trim());
                    if (r != null && r.Id == id) return r;
                }
                return null;
            }

            internal int Logged(string fragment) { return Host.LoggedLines.Count(l => l.Contains(fragment)); }

            public void Dispose() { Module.Shutdown(); }
        }

        private static PersonalReminder OneOff(string id, string text, DateTimeOffset when)
        {
            return new PersonalReminder
            {
                Id = id, Text = text, Kind = PersonalReminder.KindOnce, When = when, Anchor = when.AddHours(-1),
                Enabled = true, LastFired = "",
            };
        }

        private static string Lines(params PersonalReminder[] reminders)
        {
            return string.Join("\n", reminders.Select(PersonalReminder.Encode));
        }

        private const string TeamsLink = "https://teams.microsoft.com/l/meetup-join/19%3ameeting_selftest%40thread.v2/0?context=x";

        private static CalendarEvent MeetingAt(string id, DateTimeOffset start)
        {
            return new CalendarEvent
            {
                Id = id, Title = "Standup", Start = start, End = start.AddMinutes(30), Location = "Room 4",
                Description = "Join the meeting: " + TeamsLink,
            };
        }

        internal static void CheckQuestions(Action<string, bool> check)
        {
            var t0 = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
            var clock = new TestClock { Now = t0 };
            ReminderClock = () => clock.Now;

            CheckQuestionBasics(check, t0);
            CheckJoinQuestion(check, clock, t0);
            CheckOneOffQuestion(check, clock, t0);
            CheckRecurringQuestion(check, clock, t0);
        }

        // ---- what does not need a tick: the declarations, the lead line, the card text, the personal line ----
        private static void CheckQuestionBasics(Action<string, bool> check, DateTimeOffset t0)
        {
            ModuleInfo info = new ReminderModule().Info;
            Version floor;
            check("questions: MinHostVersion is at least 1.6.0, the host whose Contracts carry ITrayQuestions",
                Version.TryParse(info.MinHostVersion, out floor) && floor >= new Version(1, 6, 0));
            check("questions: MicrophoneUsage is declared, so the consent list names the read the join question makes",
                (info.Permissions & ModulePermissions.MicrophoneUsage) == ModulePermissions.MicrophoneUsage);

            // M1/M2: a lead-time line about a meeting WITH a join link says what it said before, less the tray sentence.
            CalendarEvent lead = MeetingAt("cal1|lead", t0);
            string leadUrl, leadKind;
            check("WITNESS the lead-time sample meeting carries a link the detector finds",
                MeetingLinkDetector.TryFind(lead, out leadUrl, out leadKind));
            string leadLine = FormatReminder(lead, t0.AddMinutes(-5), "Work");
            check("lead line: a meeting with a join link no longer says it is in the tray (" + leadLine + ")",
                leadLine == "Work: Standup starts in 5 minutes. (Room 4)");

            // The card's text.
            string longTitle = JoinText(new CalendarEvent { Title = new string('x', 300) });
            check("join text: a long title is cut to fit the card's 240 characters and still says what is happening",
                longTitle.Length <= 240 && longTitle.EndsWith("… is starting.", StringComparison.Ordinal));
            check("join text: a title over several lines is one line on the card, and no title reads as your meeting",
                JoinText(new CalendarEvent { Title = "Design\r\n\treview " }) == "Design review is starting."
                && JoinText(new CalendarEvent { Title = "  " }) == "Your meeting is starting.");
            check("question keys: a hash of what the question is about, inside the Key alphabet and stable",
                Regex.IsMatch(QuestionKey("join", "cal1|uid@example.com"), "^join-[0-9a-f]{16}$")
                && QuestionKey("join", "cal1|a") == QuestionKey("join", "cal1|a")
                && QuestionKey("join", "cal1|a") != QuestionKey("join", "cal1|b"));

            // The double refuses what the host would, or a pass below would prove nothing about the ABI's limits.
            var strict = new QuestionsHost { Clock = () => t0 };
            Func<TrayQuestion> valid = delegate
            {
                return new TrayQuestion
                {
                    Key = "k", Text = "Text", TimeoutChoiceId = "a", Timeout = TimeSpan.FromMinutes(1),
                    Choices = new[] { new TrayChoice("a", "Yes"), new TrayChoice("b", "No") },
                };
            };
            Func<TrayQuestion, string> refusal = delegate(TrayQuestion q)
            {
                // A question it takes stays open (never .Result it); a refusal is answered at once.
                Task<TrayAnswer> asked = strict.AskAsync(Id, q, CancellationToken.None);
                if (!asked.IsCompleted) return null;
                return asked.Result.Outcome == TrayQuestionOutcome.Refused ? asked.Result.Reason : null;
            };
            TrayQuestion badKey = valid(); badKey.Key = "cal1|m1@join";
            TrayQuestion longText = valid(); longText.Key = "k2"; longText.Text = new string('x', 241);
            TrayQuestion sameLabels = valid(); sameLabels.Key = "k3";
            sameLabels.Choices = new[] { new TrayChoice("a", "Done"), new TrayChoice("b", "DONE") };
            TrayQuestion nearDeadline = valid(); nearDeadline.Key = "k4"; nearDeadline.Deadline = t0.AddSeconds(2);
            TrayQuestion first = valid(); first.Key = "dup";
            TrayQuestion second = valid(); second.Key = "dup";
            check("WITNESS the questions double refuses a Key outside the alphabet, Text over 240, labels equal but for case, a Deadline under 5 s, and a duplicate pending Key",
                refusal(badKey) == "bad-key" && refusal(longText) == "bad-text" && refusal(sameLabels) == "bad-choice-label"
                && refusal(nearDeadline) == "bad-deadline" && refusal(first) == null && refusal(second) == "duplicate-key");

            // The personal line's two new fields.
            var full = new PersonalReminder
            {
                Id = "e1", Text = "call | me\nback", Kind = PersonalReminder.KindOnce, When = t0, Anchor = t0, Enabled = true,
                LastFired = "once", SnoozedUntil = new DateTimeOffset(2026, 10, 15, 12, 10, 0, TimeSpan.FromHours(2)), Unanswered = 1,
            };
            string encoded = PersonalReminder.Encode(full);
            string[] fields = encoded.Split('|');
            PersonalReminder back = PersonalReminder.Decode(encoded);
            check("personal line: eleven fields with the text still ninth, so a 1.0.x build reads the same text",
                fields.Length == 11 && fields[8] == "call / me back");
            check("personal line: the snooze and the unanswered count round-trip, offset and all",
                back != null && back.SnoozedUntil.HasValue && back.SnoozedUntil.Value == full.SnoozedUntil.Value
                && back.SnoozedUntil.Value.Offset == TimeSpan.FromHours(2) && back.Unanswered == 1);
            check("personal line: a '|' in the text round-trips as the '/' it always became, and shifts nothing after it",
                back != null && back.Text == "call / me back" && back.LastFired == "once" && back.Enabled && back.Id == "e1");
            PersonalReminder unsnoozed = PersonalReminder.Decode(PersonalReminder.Encode(OneOff("e2", "plain", t0)));
            check("personal line: no snooze round-trips as none",
                unsnoozed != null && !unsnoozed.SnoozedUntil.HasValue && unsnoozed.Unanswered == 0);
            PersonalReminder legacy = PersonalReminder.Decode(string.Join("|", fields.Take(9)));
            check("personal line: a 1.0.x nine-field line still reads, with no snooze and nothing unanswered",
                legacy != null && legacy.Id == "e1" && legacy.Text == "call / me back" && !legacy.SnoozedUntil.HasValue
                && legacy.Unanswered == 0);
            PersonalReminder garbled = PersonalReminder.Decode(string.Join("|", fields.Take(9)) + "|not-a-time|x");
            check("personal line: trailing fields that do not parse cost the snooze, never the reminder",
                garbled != null && garbled.Id == "e1" && !garbled.SnoozedUntil.HasValue && garbled.Unanswered == 0);

            // The asked mark lives in the fired set, so the prune must treat it as it treats a lead's id.
            var marks = new HashSet<string>(new[] { "cal1|m1@join", "cal1|gone@join" }, StringComparer.Ordinal);
            PruneFiredAgainstFeed(new AggregateCalendarSource(new[]
            {
                new AggregateCalendarSource.Slot
                {
                    Id = "cal1", Label = "Work",
                    Source = new AggregateCalendarSource.StubSource(new[] { new CalendarEvent { Id = "m1", Start = t0 } }, null),
                },
            }).Fetch(), marks);
            check("join mark: the prune keeps the mark of a meeting still in the feed and drops one whose meeting is gone",
                marks.Contains("cal1|m1@join") && !marks.Contains("cal1|gone@join"));
            var loadingMarks = new HashSet<string>(new[] { "cal1|m1@join" }, StringComparer.Ordinal);
            PruneFiredAgainstFeed(new AggregateCalendarSource(new[]
            {
                new AggregateCalendarSource.Slot
                {
                    Id = "cal1", Label = "Work",
                    Source = new AggregateCalendarSource.StubSource(Array.Empty<CalendarEvent>(), "Loading the calendar…"),
                },
            }).Fetch(), loadingMarks);
            check("join mark: a slot still loading keeps its marks, so a restart inside the window does not ask again",
                loadingMarks.Contains("cal1|m1@join"));
        }

        // ---- M3: Join / Dismiss at a meeting's start ----
        private static void CheckJoinQuestion(Action<string, bool> check, TestClock clock, DateTimeOffset t0)
        {
            string meetingUrl, meetingKind;
            MeetingLinkDetector.TryFind(MeetingAt("x", t0), out meetingUrl, out meetingKind);
            string expectedOpen = new Uri(meetingUrl).AbsoluteUri;

            clock.Now = t0.AddMinutes(-1);
            using (var rig = new QuestionRig(null))
            {
                rig.Feed(MeetingAt("m1", t0));
                rig.Tick();
                check("join: not asked before the meeting starts", rig.Host.Of("join").Count == 0);

                clock.Now = t0.AddSeconds(10);
                rig.Tick();
                List<QuestionsHost.Asked> asks = rig.Host.Of("join");
                QuestionsHost.Asked m1 = asks.Count == 1 ? asks[0] : null;
                check("join: asked once the meeting has started", m1 != null && m1.Refused == null && m1.Open);
                check("join: ...with no companion on screen: the card is the host's, by the clock",
                    m1 != null && !rig.Module.AnyCompanionOnScreen() && rig.Host.SaidLines.Count == 0);
                check("join: '<title> is starting.', Join / Dismiss, Dismiss on no answer, the calendar's name as its title",
                    m1 != null && m1.Question.Text == "Standup is starting." && m1.Question.Title == "Work"
                    && m1.Question.Choices.Count == 2
                    && m1.Question.Choices[0].Id == JoinChoice && m1.Question.Choices[0].Label == "Join"
                    && m1.Question.Choices[1].Id == DismissChoice && m1.Question.Choices[1].Label == "Dismiss"
                    && m1.Question.TimeoutChoiceId == DismissChoice);
                check("join: ...until three minutes past the start, and not urgent",
                    m1 != null && m1.Question.Deadline == t0 + JoinWindow && m1.Question.Timeout == JoinWindow
                    && !m1.Question.RunsWhileHeld);
                check("join: its Key is a hash of the event id, which the host's log would otherwise carry",
                    m1 != null && m1.Question.Key == QuestionKey("join", "cal1|m1"));
                check("join: the asked mark is in the fired set and saved with it",
                    rig.Module._fired.Contains("cal1|m1@join") && rig.Settings.Get("fired", "").Contains("cal1|m1@join"));

                clock.Now = t0.AddSeconds(30);
                rig.Tick();
                check("join: not asked a second time while the card is up", rig.Host.Of("join").Count == 1);
                rig.Host.Choose(m1, JoinChoice);
                check("join: Join opens the meeting's link, once, through OpenUrl's checks",
                    rig.Opened.Count == 1 && rig.Opened[0] == expectedOpen);
                clock.Now = t0.AddMinutes(1);
                rig.Tick();
                check("join: once answered it is not asked again inside the window", rig.Host.Of("join").Count == 1);

                rig.Restart();
                check("WITNESS join: the asked mark survived the restart's own first tick (its slot was still loading)",
                    rig.Module._fired.Contains("cal1|m1@join"));
                rig.Feed(MeetingAt("m1", t0));
                clock.Now = t0.AddSeconds(80);
                rig.Tick();
                check("join: a restart inside the window does not ask again (the mark is persisted with the fired set)",
                    rig.Host.Of("join").Count == 1 && rig.Opened.Count == 1);

                // At or past three minutes, and in the window's last seconds, nothing is asked; nor without a link.
                clock.Now = t0 + JoinWindow;
                rig.Feed(MeetingAt("late", t0), MeetingAt("edge", t0.AddSeconds(5)),
                    new CalendarEvent { Id = "nolink", Title = "Lunch", Start = t0.AddMinutes(2), Location = "Cafe" });
                rig.Tick();
                check("join: not asked at three minutes past the start, nor with seconds left, nor for an event with no link",
                    rig.Host.Of("join").Count == 1 && !rig.Module._fired.Contains("cal1|late@join")
                    && !rig.Module._fired.Contains("cal1|edge@join") && !rig.Module._fired.Contains("cal1|nolink@join"));

                // Dismiss, nobody answering, and an expired deadline: nothing opens.
                clock.Now = t0.AddHours(1);
                rig.Feed(MeetingAt("d1", clock.Now), MeetingAt("d2", clock.Now), MeetingAt("d3", clock.Now));
                rig.Tick();
                QuestionsHost.Asked d1 = rig.Host.For("join", "cal1|d1");
                QuestionsHost.Asked d2 = rig.Host.For("join", "cal1|d2");
                QuestionsHost.Asked d3 = rig.Host.For("join", "cal1|d3");
                check("WITNESS join: three meetings starting together are three questions", d1 != null && d2 != null && d3 != null);
                rig.Host.Choose(d1, DismissChoice);
                rig.Host.NoAnswer(d2);
                rig.Host.Expire(d3);
                check("join: Dismiss, no answer and an expired deadline open nothing", rig.Opened.Count == 1);
                clock.Now = clock.Now.AddSeconds(20);
                rig.Tick();
                check("join: ...and none of the three is asked again", rig.Host.Of("join").Count == 4);

                // Withdrawn once Teams has the microphone.
                clock.Now = t0.AddHours(2);
                rig.Feed(MeetingAt("w1", clock.Now));
                rig.Tick();
                QuestionsHost.Asked w1 = rig.Host.For("join", "cal1|w1");
                check("WITNESS join: asked while no meeting app holds the microphone", w1 != null && w1.Open);
                rig.Mic.Add(MeetingAppMicrophoneSelfCheck.TeamsMediaHost, true, clock.Now.AddSeconds(5), null);
                clock.Now = clock.Now.AddSeconds(20);
                rig.Tick();
                check("join: withdrawn once Teams is using the microphone (the user has joined), and nothing opened",
                    w1 != null && w1.Withdrawn && !w1.Open && rig.Module._joinAsks.Count == 0 && rig.Opened.Count == 1);
                check("join: ...the log names the app and never the meeting",
                    rig.Logged("join question withdrawn: Teams is using the microphone") == 1 && rig.Logged("Standup") == 0);

                // Not asked while in a call; asked once that call ends inside the window (back to back).
                clock.Now = t0.AddHours(3);
                rig.Feed(MeetingAt("b2b", clock.Now));
                rig.Tick();
                check("join: not asked while Teams already holds the microphone, and not marked",
                    rig.Host.For("join", "cal1|b2b") == null && !rig.Module._fired.Contains("cal1|b2b@join"));
                rig.Mic.Entries.Clear();
                rig.Mic.Add(MeetingAppMicrophoneSelfCheck.TeamsMediaHost, true, clock.Now.AddMinutes(-40), clock.Now.AddSeconds(10));
                clock.Now = clock.Now.AddSeconds(40);
                rig.Tick();
                QuestionsHost.Asked b2b = rig.Host.For("join", "cal1|b2b");
                check("join: ...and asked once that call ends inside the window", b2b != null && b2b.Open);
                rig.Host.Choose(b2b, DismissChoice);

                // Quiet hours hold it, unmarked.
                clock.Now = t0.AddHours(4);
                DateTimeOffset local = clock.Now.ToLocalTime();
                rig.Settings.Set("quietFrom", local.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture));
                rig.Settings.Set("quietTo", local.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture));
                rig.Feed(MeetingAt("q1", clock.Now));
                rig.Tick();
                check("join: not asked during quiet hours, and not marked",
                    rig.Host.For("join", "cal1|q1") == null && !rig.Module._fired.Contains("cal1|q1@join"));
                rig.Settings.Set("quietFrom", "");
                rig.Settings.Set("quietTo", "");

                // The setting: on by default, in Timing, and off means off.
                OptionsPane pane = rig.Host.OptionsPanes.Count > 0 ? rig.Host.OptionsPanes[rig.Host.OptionsPanes.Count - 1] : null;
                SettingField field = pane == null || pane.Schema == null ? null : pane.Schema.FirstOrDefault(f => f != null && f.Id == AskToJoinKey);
                check("join setting: \"Ask me to join when a meeting starts\", a checkbox in the Timing card, on by default",
                    field != null && field.Kind == SettingKind.Bool && field.Group == "Timing"
                    && field.Label == "Ask me to join when a meeting starts" && pane.Load()[AskToJoinKey] == "true");
                if (pane != null) pane.Save(new Dictionary<string, string> { { AskToJoinKey, "false" } });
                check("join setting: Apply turns it off and it reads back off",
                    rig.Settings.Get(AskToJoinKey, "") == "false" && pane != null && pane.Load()[AskToJoinKey] == "false");
                clock.Now = t0.AddHours(5);
                rig.Feed(MeetingAt("off1", clock.Now));   // after the Save, which rebuilt the module's source
                rig.Tick();
                check("join: with the setting off nothing is asked", rig.Host.For("join", "cal1|off1") == null);
                if (pane != null) pane.Save(new Dictionary<string, string> { { AskToJoinKey, "true" } });

                // An answer completed on another thread is applied on the thread that asked.
                SynchronizationContext before = SynchronizationContext.Current;
                var asker = new QueueContext();
                SynchronizationContext.SetSynchronizationContext(asker);
                try
                {
                    clock.Now = t0.AddHours(6);
                    rig.Feed(MeetingAt("ui1", clock.Now));
                    rig.Tick();
                    QuestionsHost.Asked ui1 = rig.Host.For("join", "cal1|ui1");
                    int openedBefore = rig.Opened.Count;
                    Task.Run(delegate { rig.Host.Choose(ui1, JoinChoice); }).Wait();
                    bool notApplied = rig.Opened.Count == openedBefore && asker.Pending == 1;
                    asker.RunAll();
                    check("join: an answer completed on another thread is posted to the thread that asked, and applied there",
                        ui1 != null && notApplied && rig.Opened.Count == openedBefore + 1);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(before);
                }

                // A question still up at Shutdown is withdrawn, and its late answer does nothing.
                clock.Now = t0.AddHours(7);
                rig.Feed(MeetingAt("s1", clock.Now));
                rig.Tick();
                QuestionsHost.Asked s1 = rig.Host.For("join", "cal1|s1");
                rig.Module.Shutdown();
                check("join: Shutdown withdraws a question still on the card", s1 != null && s1.Withdrawn && !s1.Open);
                rig.Start();
                check("join: every question asked here was one the host would take ("
                      + string.Join("; ", rig.Host.Refusals) + ")", rig.Host.Refusals.Count == 0);
            }

            // Without the card (a test host with no double) nothing is asked and nothing is marked.
            var plainHost = new RecordingHost();
            var plain = new ReminderModule();
            plainHost.SettingsFor(Id).Set("hushPresenting", "false");
            plain.Init(plainHost);
            try
            {
                clock.Now = t0.AddHours(8);
                plain._source = new AggregateCalendarSource(new[]
                {
                    new AggregateCalendarSource.Slot
                    {
                        Id = "cal1", Label = "Work",
                        Source = new AggregateCalendarSource.StubSource(new[] { MeetingAt("p1", clock.Now) }, null),
                    },
                });
                plain.CheckDue();
                check("join: on a host with no question card nothing is asked or marked",
                    !plain._fired.Any(f => f.EndsWith("@join", StringComparison.Ordinal)));
            }
            finally
            {
                plain.Shutdown();
            }
        }

        // ---- M4: a one-off personal reminder ----
        private static void CheckOneOffQuestion(Action<string, bool> check, TestClock clock, DateTimeOffset t0)
        {
            clock.Now = t0;
            using (var rig = new QuestionRig(s => s.Set("personal", Lines(OneOff("p1", "Take the pizza out", t0.AddMinutes(-1))))))
            {
                // Init's own tick asked it.
                QuestionsHost.Asked first = rig.Host.For("personal", "p1");
                check("one-off: asked when it comes due, with no companion on screen",
                    first != null && first.Open && !rig.Module.AnyCompanionOnScreen());
                check("one-off: \"Reminder: <text>\", Done / Snooze 10 min / Snooze 1 hour, five minutes, no answer snoozes it",
                    first != null && first.Question.Text == "Reminder: Take the pizza out"
                    && first.Question.Choices.Count == 3
                    && first.Question.Choices[0].Id == DoneChoice && first.Question.Choices[0].Label == "Done"
                    && first.Question.Choices[1].Id == Snooze10Choice && first.Question.Choices[1].Label == "Snooze 10 min"
                    && first.Question.Choices[2].Id == Snooze60Choice && first.Question.Choices[2].Label == "Snooze 1 hour"
                    && first.Question.TimeoutChoiceId == Snooze10Choice && first.Question.Timeout == PersonalTimeout
                    && !first.Question.RunsWhileHeld && !first.Question.Deadline.HasValue);
                check("one-off: chimed as before and not spoken: the card replaces the bubble",
                    rig.Host.PlayedSounds.Count == 1 && rig.Host.SaidLines.Count == 0);
                PersonalReminder stored = rig.Personal("p1");
                check("one-off: still enabled while its card is up (no longer disabled at fire), and stamped fired",
                    stored != null && stored.Enabled && stored.LastFired == "once");

                clock.Now = t0.AddSeconds(20);
                rig.Tick();
                check("one-off: not asked again while its card is up", rig.Host.Of("personal").Count == 1);
                rig.Host.Choose(first, Snooze10Choice);
                stored = rig.Personal("p1");
                check("one-off: Snooze 10 min saves a snooze ten minutes from the answer",
                    stored != null && stored.Enabled && stored.SnoozedUntil == t0.AddSeconds(20).AddMinutes(10) && stored.Unanswered == 0);

                clock.Now = t0.AddMinutes(5);
                rig.Tick();
                check("one-off: not asked while snoozed", rig.Host.Of("personal").Count == 1);
                rig.Restart();
                check("one-off: the snooze survives a restart",
                    rig.Host.Of("personal").Count == 1 && rig.Personal("p1") != null && rig.Personal("p1").SnoozedUntil.HasValue);

                clock.Now = t0.AddMinutes(10).AddSeconds(40);
                rig.Tick();
                QuestionsHost.Asked second = rig.Host.For("personal", "p1");
                check("one-off: asked again once the snooze runs out, with its chime",
                    rig.Host.Of("personal").Count == 2 && second != null && second.Open && rig.Host.PlayedSounds.Count == 2
                    && second.Question.TimeoutChoiceId == Snooze10Choice);
                rig.Host.NoAnswer(second);
                stored = rig.Personal("p1");
                check("one-off: the first no-answer snoozes it ten minutes and counts it",
                    stored != null && stored.Enabled && stored.Unanswered == 1 && stored.SnoozedUntil == clock.Now.AddMinutes(10));

                clock.Now = clock.Now.AddMinutes(10).AddSeconds(20);
                rig.Tick();
                QuestionsHost.Asked third = rig.Host.For("personal", "p1");
                check("one-off: asked a third time, and now no answer marks it missed, which the card names as Done",
                    rig.Host.Of("personal").Count == 3 && third != null && third.Open && third.Question.TimeoutChoiceId == DoneChoice);
                rig.Host.NoAnswer(third);
                stored = rig.Personal("p1");
                check("one-off: the second no-answer disables it (missed) and says so in the log, without its text",
                    stored != null && !stored.Enabled && !stored.SnoozedUntil.HasValue && stored.Unanswered == 2
                    && rig.Logged("marked missed") == 1 && rig.Logged("pizza") == 0);
                clock.Now = clock.Now.AddMinutes(30);
                rig.Tick();
                check("WITNESS a missed one-off is not asked again", rig.Host.Of("personal").Count == 3);
                check("one-off: every question asked here was one the host would take ("
                      + string.Join("; ", rig.Host.Refusals) + ")", rig.Host.Refusals.Count == 0);
            }

            // Done, Snooze 1 hour, an answer for a reminder that was removed, and quiet hours.
            clock.Now = t0;
            using (var rig = new QuestionRig(s => s.Set("personal", Lines(
                OneOff("p2", "Water the plants", t0.AddMinutes(-2)), OneOff("p3", "Call back", t0.AddMinutes(-2)),
                OneOff("p4", "Gone soon", t0.AddMinutes(-2))))))
            {
                QuestionsHost.Asked p2 = rig.Host.For("personal", "p2");
                QuestionsHost.Asked p3 = rig.Host.For("personal", "p3");
                QuestionsHost.Asked p4 = rig.Host.For("personal", "p4");
                check("WITNESS three one-offs due together are three questions and three chimes",
                    p2 != null && p3 != null && p4 != null && rig.Host.PlayedSounds.Count == 3);
                rig.Host.Choose(p2, DoneChoice);
                PersonalReminder doneOne = rig.Personal("p2");
                check("one-off: Done finishes it (disabled), with nothing snoozed",
                    doneOne != null && !doneOne.Enabled && !doneOne.SnoozedUntil.HasValue && doneOne.Unanswered == 0);
                clock.Now = t0.AddMinutes(1);
                rig.Host.Choose(p3, Snooze60Choice);
                check("one-off: Snooze 1 hour saves a snooze an hour from the answer",
                    rig.Personal("p3") != null && rig.Personal("p3").SnoozedUntil == t0.AddMinutes(61));

                // The user removes p4 while its card is up; its answer then has nothing to act on.
                rig.Settings.Set("personal", Lines(rig.Personal("p2"), rig.Personal("p3")));
                rig.Host.Choose(p4, Snooze10Choice);
                check("one-off: an answer for a reminder removed while its card was up is ignored, and says so in the log",
                    rig.Personal("p4") == null && rig.Personal("p3") != null && rig.Logged("after it was removed") == 1);

                clock.Now = t0.AddMinutes(60);
                rig.Tick();
                check("one-off: not asked before its hour is up", rig.Host.Of("personal").Count == 3);
                clock.Now = t0.AddMinutes(61).AddSeconds(10);
                DateTimeOffset local = clock.Now.ToLocalTime();
                rig.Settings.Set("quietFrom", local.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture));
                rig.Settings.Set("quietTo", local.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture));
                rig.Tick();
                check("one-off: quiet hours still hold it", rig.Host.Of("personal").Count == 3);
                rig.Settings.Set("quietFrom", "");
                rig.Settings.Set("quietTo", "");
                rig.Tick();
                QuestionsHost.Asked p3b = rig.Host.For("personal", "p3");
                check("one-off: ...and it is asked once they end", rig.Host.Of("personal").Count == 4 && p3b != null && p3b.Open);

                // Left open at a restart: withdrawn at Shutdown with nothing decided, and asked once more at the start.
                rig.Module.Shutdown();
                PersonalReminder leftOpen = rig.Personal("p3");
                check("one-off: Shutdown withdraws its open question and decides nothing",
                    p3b != null && p3b.Withdrawn && leftOpen != null && leftOpen.Enabled && leftOpen.Unanswered == 0);
                rig.Start();
                QuestionsHost.Asked p3c = rig.Host.For("personal", "p3");
                check("one-off: a question left open by a restart is asked once more at the next start",
                    rig.Host.Of("personal").Count == 5 && p3c != null && p3c.Open);
                rig.Host.Choose(p3c, DoneChoice);
                rig.Restart();
                check("WITNESS ...and once answered Done, the next start asks nothing", rig.Host.Of("personal").Count == 5);
                check("one-off (Done, Snooze 1 hour, removed, restarted): every question asked was one the host would take ("
                      + string.Join("; ", rig.Host.Refusals) + ")", rig.Host.Refusals.Count == 0);
            }

            // A one-off fired by 1.1.0 and left open (the state a restart leaves) reads as LeftOpen; a fresh one does not.
            var fresh = OneOff("lo1", "x", t0.AddMinutes(1));
            var open = OneOff("lo2", "x", t0.AddMinutes(-1));
            open.LastFired = "once";
            string ignored;
            check("one-off: fired, enabled and not snoozed is a question left open; not yet due is nothing",
                PersonalDueNow(open, t0, out ignored) == PersonalDue.LeftOpen && PersonalDueNow(fresh, t0, out ignored) == PersonalDue.None);

            // Five due at once: the host takes four pending per module, so the fifth waits for room rather than being refused.
            clock.Now = t0;
            using (var rig = new QuestionRig(s => s.Set("personal", Lines(
                OneOff("b1", "One", t0.AddMinutes(-1)), OneOff("b2", "Two", t0.AddMinutes(-1)), OneOff("b3", "Three", t0.AddMinutes(-1)),
                OneOff("b4", "Four", t0.AddMinutes(-1)), OneOff("b5", "Five", t0.AddMinutes(-1))))))
            {
                PersonalReminder waiting = rig.Personal("b5");
                check("budget: five due together ask four, and the fifth waits unmarked rather than being refused",
                    rig.Host.Of("personal").Count == 4 && rig.Host.Refusals.Count == 0
                    && rig.Host.For("personal", "b5") == null && waiting != null && waiting.LastFired == "");
                rig.Host.Choose(rig.Host.For("personal", "b1"), DoneChoice);
                clock.Now = t0.AddSeconds(20);
                rig.Tick();
                check("budget: ...and it is asked on the next tick once a card is answered",
                    rig.Host.For("personal", "b5") != null && rig.Host.Of("personal").Count == 5 && rig.Host.Refusals.Count == 0);

                // Four left open (fired, enabled, never snoozed) when the app closes: each is asked again at the start.
                QuestionsHost.Asked b2 = rig.Host.For("personal", "b2");
                rig.Restart();
                QuestionsHost.Asked b2Again = rig.Host.For("personal", "b2");
                PersonalReminder b2Stored = rig.Personal("b2");
                check("one-off: questions left open by a restart, never snoozed, are each asked once more at the next start",
                    rig.Host.Of("personal").Count == 9 && b2 != null && b2.Withdrawn && b2Again != null && b2Again != b2
                    && b2Again.Open && b2Stored != null && b2Stored.Enabled && b2Stored.LastFired == "once"
                    && !b2Stored.SnoozedUntil.HasValue && rig.Host.Refusals.Count == 0);
            }
        }

        // ---- M5: a recurring personal reminder ----
        private static void CheckRecurringQuestion(Action<string, bool> check, TestClock clock, DateTimeOffset t0)
        {
            var daily = new PersonalReminder
            {
                Id = "r1", Text = "Stand up and stretch", Kind = PersonalReminder.KindDaily,
                TimeOfDayMinutes = t0.Hour * 60 + t0.Minute, When = t0.AddDays(-1), Anchor = t0.AddDays(-1),
                Enabled = true, LastFired = "",
            };
            string today = t0.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            clock.Now = t0.AddMinutes(1);
            using (var rig = new QuestionRig(s => s.Set("personal", Lines(daily))))
            {
                QuestionsHost.Asked first = rig.Host.For("personal", "r1");
                check("recurring: asked when it comes due, Done / Snooze 10 min, and no answer dismisses it",
                    first != null && first.Open && first.Question.Choices.Count == 2
                    && first.Question.Choices[0].Id == DoneChoice && first.Question.Choices[1].Id == Snooze10Choice
                    && first.Question.TimeoutChoiceId == DoneChoice && first.Question.Timeout == PersonalTimeout);
                clock.Now = t0.AddMinutes(1).AddSeconds(30);
                rig.Host.Choose(first, Snooze10Choice);
                PersonalReminder stored = rig.Personal("r1");
                check("recurring: Snooze 10 min saves a snooze and leaves the occurrence stamped",
                    stored != null && stored.Enabled && stored.LastFired == today
                    && stored.SnoozedUntil == t0.AddMinutes(11).AddSeconds(30));
                clock.Now = t0.AddMinutes(5);
                rig.Tick();
                check("recurring: not asked while snoozed", rig.Host.Of("personal").Count == 1);
                clock.Now = t0.AddMinutes(11).AddSeconds(40);
                rig.Tick();
                QuestionsHost.Asked snoozed = rig.Host.For("personal", "r1");
                check("recurring: asked again once the snooze runs out", rig.Host.Of("personal").Count == 2 && snoozed != null && snoozed.Open);
                rig.Host.Choose(snoozed, DoneChoice);
                stored = rig.Personal("r1");
                check("recurring: Done clears the snooze and keeps it enabled for its next occurrence",
                    stored != null && stored.Enabled && !stored.SnoozedUntil.HasValue && stored.LastFired == today);
                clock.Now = t0.AddMinutes(12);
                rig.Tick();
                check("recurring: answered inside its own fifteen-minute window, it is not asked again (a snooze never touches LastFired)",
                    rig.Host.Of("personal").Count == 2);

                clock.Now = t0.AddDays(1).AddMinutes(1);
                rig.Tick();
                QuestionsHost.Asked tomorrow = rig.Host.For("personal", "r1");
                check("recurring: the next occurrence is asked as scheduled", rig.Host.Of("personal").Count == 3 && tomorrow != null && tomorrow.Open);
                rig.Host.NoAnswer(tomorrow);
                stored = rig.Personal("r1");
                check("recurring: no answer dismisses it: still enabled, nothing snoozed, nothing counted",
                    stored != null && stored.Enabled && !stored.SnoozedUntil.HasValue && stored.Unanswered == 0
                    && stored.LastFired == t0.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                clock.Now = t0.AddDays(1).AddMinutes(3);
                rig.Tick();
                check("recurring: ...and it is not asked again until its next occurrence", rig.Host.Of("personal").Count == 3);
                check("recurring: every question asked here was one the host would take ("
                      + string.Join("; ", rig.Host.Refusals) + ")", rig.Host.Refusals.Count == 0);
            }
        }
    }
}
