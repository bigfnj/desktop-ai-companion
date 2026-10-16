using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.MeetingApps;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.ReminderModule
{
    // Questions by the clock (Reminder 1.1.0, lane feature/meeting-end; the owner's decisions of 2026-10-15). Host 1.6.0
    // draws a card above the tray clock with two to four buttons (ITrayQuestions), and this module asks two kinds of
    // question on it:
    //
    //   M3  a meeting with a join link has started: Join / Dismiss, until three minutes past its start, and withdrawn
    //       as soon as Teams or Zoom is using the microphone, because by then the user has joined;
    //   M4  a one-off personal reminder came due: Done / Snooze 10 min / Snooze 1 hour. Nobody answering snoozes it ten
    //       minutes once; a second time it is marked missed (disabled);
    //   M5  a recurring personal reminder came due: Done / Snooze 10 min. Nobody answering dismisses it; the next
    //       occurrence comes as scheduled.
    //
    // The lead-time meeting reminders (M1/M2) and the morning briefing (M6) stay speech, as before.
    //
    // THE HOST APPLIES NOTHING (PluginApi.cs): on a timeout the answer carries the question's TimeoutChoiceId and this
    // module acts on it, so every no-answer rule above lives here, where the self-test can drive it.
    //
    // A card does not need a companion on screen, so neither question is held by the F199 "nobody to show it" rule the
    // speech keeps. Quiet hours hold both. The presentation hush holds the personal one, because its chime comes with it,
    // and not the join one, which is silent: the host holds a card while the user presents, and the card it shows when
    // the presentation ends is the same one this module would have asked a tick later.
    public sealed partial class ReminderModule
    {
        // ---- the seams the self-test steps and watches ----

        /// <summary>The module's clock: every due rule on the tick and every answer reads it, so the self-test can step
        /// time through a three-minute window or a ten-minute snooze without waiting for either.</summary>
        internal static Func<DateTimeOffset> ReminderClock = () => DateTimeOffset.Now;

        /// <summary>Where Join hands its link once OpenUrl has vetted it, instead of starting the browser. Null in the
        /// app; set by the self-test, which must see the link without a browser opening on the machine running it.</summary>
        internal Action<string> OpenUrlForSelfTest;

        /// <summary>Whether Teams or Zoom holds the microphone, from Windows' own record of it (shared/MeetingApps). Read
        /// only while a join question is open or about to be asked, never on an idle tick. The self-test swaps in one
        /// over a FakeConsentStore.</summary>
        internal MeetingAppMicrophone Microphone = new MeetingAppMicrophone();

        // ---- the questions service and what is open on it ----

        private ITrayQuestions _questions;
        private bool _shutDown;

        // Open questions, keyed by what they are about: a join question by its asked mark ("<eventId>@join"), a personal
        // one by the reminder's id. The token withdraws it. An entry leaves when its answer arrives, whatever the answer,
        // so a key in here means "the user may be looking at this right now" and the tick neither asks it again nor
        // counts it as due.
        private readonly Dictionary<string, CancellationTokenSource> _joinAsks =
            new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private readonly Dictionary<string, CancellationTokenSource> _personalAsks =
            new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        // When each recent question was asked, oldest first, for QuestionRoom.
        private readonly Queue<DateTimeOffset> _askTimes = new Queue<DateTimeOffset>();

        // The host refuses a module's fifth pending question and its seventh in a minute (host 1.6.0's limits). A refusal
        // here would fall back to speech, so the tick stays inside both and leaves the rest for a later tick, unmarked:
        // five one-offs left open by a restart arrive as four cards and then a fifth, not four cards and a bubble.
        private const int MaxOpenQuestions = 4;
        private const int MaxAsksPerMinute = 6;

        // ---- M3: join a meeting that has started ----

        internal const string AskToJoinKey = "askToJoin";
        internal const string JoinChoice = "join";
        internal const string DismissChoice = "dismiss";

        /// <summary>How long after a meeting's start the join question is worth asking: the owner's three minutes.</summary>
        internal static readonly TimeSpan JoinWindow = TimeSpan.FromMinutes(3);

        // Not asked in the window's last ten seconds. The ABI refuses a Deadline under five seconds ahead, and a card that
        // would vanish before it could be read is noise.
        private static readonly TimeSpan JoinMinimumLeft = TimeSpan.FromSeconds(10);

        /// <summary>The asked mark a join question leaves in the fired set. It shares the "&lt;eventId&gt;@&lt;lead&gt;" shape,
        /// so PruneFiredAgainstFeed keeps and drops it by its slot and event exactly as it does a lead's id, and
        /// DueNowMulti, which only ever builds numeric leads, can never mistake it for one.</summary>
        internal static string JoinFiredId(string eventId) { return eventId + "@join"; }

        /// <summary>
        /// Ask Join / Dismiss for each meeting with a link that started less than three minutes ago and has not been
        /// asked, and withdraw the open ones once a meeting app holds the microphone. Returns true when the fired set
        /// changed. The asked mark is written as the question goes out, and persisted with the rest of the fired set, so
        /// neither a second tick nor a restart inside the window asks again, whatever the answer was.
        /// </summary>
        private bool CheckJoinPrompts(DateTimeOffset now, IReadOnlyList<CalendarEvent> events, bool quiet)
        {
            if (_questions == null) return false;
            WithdrawJoinPromptsIfJoined();
            if (quiet || !_settings.GetBool(AskToJoinKey, true)) return false;

            var candidates = new List<KeyValuePair<CalendarEvent, string>>();
            foreach (CalendarEvent e in Schedulable(events))
            {
                if (string.IsNullOrEmpty(e.Id)) continue;
                // An all-day event starts at midnight, which is not a moment anyone joins anything at.
                if (e.AllDay) continue;
                DateTimeOffset deadline = e.Start + JoinWindow;
                if (now < e.Start || now >= deadline || deadline - now < JoinMinimumLeft) continue;
                string firedId = JoinFiredId(e.Id);
                if (_fired.Contains(firedId) || _joinAsks.ContainsKey(firedId)) continue;
                string url, kind;
                if (!MeetingLinkDetector.TryFind(e, out url, out kind)) continue;
                candidates.Add(new KeyValuePair<CalendarEvent, string>(e, url));
            }
            if (candidates.Count == 0) return false;

            // Someone already in a call -- this meeting, joined early, or the one before it running over -- is not
            // asked, and NOT marked: if that call ends inside the window the question still comes, which is the
            // back-to-back case. The same reading withdraws an open question above.
            if (MeetingAppHoldsMicrophone()) return false;

            bool changed = false;
            int room = QuestionRoom(now);
            foreach (KeyValuePair<CalendarEvent, string> candidate in candidates)
            {
                if (room-- <= 0) break;   // left for a later tick inside the window, unmarked
                AskJoin(candidate.Key, candidate.Value, now);
                _fired.Add(JoinFiredId(candidate.Key.Id));
                changed = true;
            }
            return changed;
        }

        private void AskJoin(CalendarEvent e, string url, DateTimeOffset now)
        {
            string firedId = JoinFiredId(e.Id);
            string label = CardText(SlotLabel(e.SourceId), 64);
            var question = new TrayQuestion
            {
                // A hash, not the event id: the id carries '|' (outside the Key alphabet) and, from an .ics feed, often
                // an organiser's address, and the Key is the one part of a question the host's log records.
                Key = QuestionKey("join", e.Id),
                Title = label.Length == 0 ? null : label,
                Text = JoinText(e),
                Choices = new[] { new TrayChoice(JoinChoice, "Join"), new TrayChoice(DismissChoice, "Dismiss") },
                TimeoutChoiceId = DismissChoice,
                Timeout = JoinWindow,
                Deadline = e.Start + JoinWindow,
                RunsWhileHeld = false,   // dismissing unseen is safe, but there is nothing to gain by hurrying it
            };
            var withdraw = new CancellationTokenSource();
            _joinAsks[firedId] = withdraw;
            NoteAsked(now);
            WhenAnswered(AskSafely(question, withdraw.Token),
                delegate(TrayAnswer answer) { ApplyJoinAnswer(firedId, url, withdraw, answer); });
        }

        /// <summary>"Weekly sync is starting." with the title collapsed to one line and cut to fit the card's 240.</summary>
        internal static string JoinText(CalendarEvent e)
        {
            const string tail = " is starting.";
            string title = CardText(e == null ? "" : e.Title, 240 - tail.Length);
            return (title.Length == 0 ? "Your meeting" : title) + tail;
        }

        private void ApplyJoinAnswer(string firedId, string url, CancellationTokenSource withdraw, TrayAnswer answer)
        {
            ForgetAsk(_joinAsks, firedId, withdraw);
            if (_shutDown || answer == null) return;
            if (answer.Outcome == TrayQuestionOutcome.Refused)
            {
                try { _host.Log(Id, "join question refused (" + (answer.Reason ?? "") + ")"); } catch { }
                return;
            }
            if (answer.Outcome == TrayQuestionOutcome.Chosen && string.Equals(answer.ChoiceId, JoinChoice, StringComparison.Ordinal))
                OpenUrl(url);
            // Dismiss, no answer by start + 3 min, or withdrawn because a meeting app took the microphone: nothing to
            // do. The asked mark keeps it from coming back.
        }

        /// <summary>Withdraw every open join question once Teams or Zoom holds the microphone: the user has joined
        /// (a meeting, if not necessarily this one, and a card asking them to join another would be beside the point).
        /// Reads the record only while a question is open; a question already withdrawn is not counted again.</summary>
        private void WithdrawJoinPromptsIfJoined()
        {
            List<CancellationTokenSource> open = _joinAsks.Values.Where(c => !c.IsCancellationRequested).ToList();
            if (open.Count == 0) return;
            MicUseSnapshot mic = ReadMicrophone();
            if (mic == null || !mic.AnyInUse) return;
            try
            {
                _host.Log(Id, "join question withdrawn: " + MeetingAppMicrophone.DisplayName(mic.InUseApp)
                    + " is using the microphone");
            }
            catch { }
            // A copy, cancelled one by one: a host that answers Withdrawn inline runs ApplyJoinAnswer, which removes the
            // entry, inside Cancel.
            // (Cancel rethrows what a registered callback threw, as an AggregateException; one host callback failing
            // must not leave the other questions up or end the tick.)
            foreach (CancellationTokenSource withdraw in open)
            {
                try { withdraw.Cancel(); } catch { }
            }
        }

        private bool MeetingAppHoldsMicrophone()
        {
            MicUseSnapshot mic = ReadMicrophone();
            return mic != null && mic.AnyInUse;
        }

        // Read never throws (its contract); the catch is for a seam a test replaced with something that does. An
        // unreadable record is not "in use", so the question is asked and stays up, which is the harmless direction.
        private MicUseSnapshot ReadMicrophone()
        {
            try { return Microphone != null ? Microphone.Read() : null; }
            catch { return null; }
        }

        // ---- M4/M5: personal reminders ----

        internal const string DoneChoice = "done";
        internal const string Snooze10Choice = "snooze10";
        internal const string Snooze60Choice = "snooze60";

        /// <summary>How long a personal reminder's card waits for an answer: the owner's five minutes.</summary>
        internal static readonly TimeSpan PersonalTimeout = TimeSpan.FromMinutes(5);

        /// <summary>A one-off asked this many times with nobody answering is marked missed (disabled).</summary>
        internal const int MissedAfterUnanswered = 2;

        /// <summary>Why a personal reminder wants attention right now.</summary>
        internal enum PersonalDue
        {
            None = 0,
            Occurrence = 1,   // its schedule came round (the 1.0.8 rule): a new LastFired key
            Snoozed = 2,      // a snooze it was given has run out
            LeftOpen = 3,     // a one-off that fired and was neither answered nor snoozed: its question was still open
                              // when the app last closed, so it is asked again at this start
        }

        /// <summary>
        /// IsPersonalDue with the snooze honoured. A snooze is NOT stamped into LastFired: a daily reminder's own window
        /// stays open fifteen minutes, and a snooze answered inside it would put a different key in LastFired and let the
        /// schedule fire the same day's occurrence again. So the occurrence rule is checked first and owns LastFired, and
        /// a snooze is its own state, cleared or moved by the answer it gets. An occurrence that comes round while a
        /// snooze is pending replaces it (an every-N reminder snoozed past its next interval).
        /// </summary>
        internal static PersonalDue PersonalDueNow(PersonalReminder r, DateTimeOffset now, out string occurrenceKey)
        {
            occurrenceKey = null;
            if (r == null || !r.Enabled) return PersonalDue.None;
            string key;
            if (IsPersonalDue(r, now, out key) && !string.Equals(r.LastFired, key, StringComparison.Ordinal))
            {
                occurrenceKey = key;
                return PersonalDue.Occurrence;
            }
            if (r.SnoozedUntil.HasValue) return now >= r.SnoozedUntil.Value ? PersonalDue.Snoozed : PersonalDue.None;
            if (string.Equals(r.Kind, PersonalReminder.KindOnce, StringComparison.Ordinal)
                && string.Equals(r.LastFired, "once", StringComparison.Ordinal))
                return PersonalDue.LeftOpen;
            return PersonalDue.None;
        }

        private void AskPersonal(PersonalReminder r, DateTimeOffset now)
        {
            string id = r.Id ?? "";
            bool once = string.Equals(r.Kind, PersonalReminder.KindOnce, StringComparison.Ordinal);
            var question = new TrayQuestion
            {
                Key = QuestionKey("personal", id),
                Text = CardText(FormatPersonal(r), 240),
                Choices = once
                    ? new[] { new TrayChoice(DoneChoice, "Done"), new TrayChoice(Snooze10Choice, "Snooze 10 min"),
                              new TrayChoice(Snooze60Choice, "Snooze 1 hour") }
                    : new[] { new TrayChoice(DoneChoice, "Done"), new TrayChoice(Snooze10Choice, "Snooze 10 min") },
                // What the host's countdown names. A one-off's first no-answer snoozes it ten minutes, its second marks
                // it missed, which the card shows as Done; a recurring one is dismissed. ApplyPersonalAnswer acts on
                // the outcome itself and agrees with this.
                TimeoutChoiceId = once && r.Unanswered < MissedAfterUnanswered - 1 ? Snooze10Choice : DoneChoice,
                Timeout = PersonalTimeout,
                RunsWhileHeld = false,   // the countdown waits for the user to be able to see it
            };
            var withdraw = new CancellationTokenSource();
            _personalAsks[id] = withdraw;
            NoteAsked(now);
            WhenAnswered(AskSafely(question, withdraw.Token),
                delegate(TrayAnswer answer) { ApplyPersonalAnswer(id, withdraw, answer); });
        }

        /// <summary>
        /// Act on a personal reminder's answer, against the list as it is NOW (the user may have edited it while the card
        /// was up), and persist it through the same save as every other personal-list write. An answer for a reminder
        /// that has since been removed is ignored.
        /// </summary>
        private void ApplyPersonalAnswer(string id, CancellationTokenSource withdraw, TrayAnswer answer)
        {
            ForgetAsk(_personalAsks, id, withdraw);
            if (_shutDown || answer == null) return;
            // Withdrawn is this module's own Shutdown and Cancelled the app closing: either way nothing is decided, and
            // a one-off left like this is asked again at the next start (PersonalDue.LeftOpen).
            if (answer.Outcome == TrayQuestionOutcome.Withdrawn || answer.Outcome == TrayQuestionOutcome.Cancelled) return;

            List<PersonalReminder> list = LoadPersonal();
            PersonalReminder r = list.FirstOrDefault(x => x != null && string.Equals(x.Id ?? "", id, StringComparison.Ordinal));
            if (r == null)
            {
                try { _host.Log(Id, "a personal reminder's answer arrived after it was removed; ignored"); } catch { }
                return;
            }
            bool once = string.Equals(r.Kind, PersonalReminder.KindOnce, StringComparison.Ordinal);
            DateTimeOffset now = ReminderClock();

            if (answer.Outcome == TrayQuestionOutcome.Refused)
            {
                // Never shown, for a reason the host names (the tick keeps inside its limits, so this is the host
                // being unable to show a card at all). Said the 1.0.8 way instead, and settled the 1.0.8 way, rather
                // than asked again every tick into the same refusal.
                try { _host.Log(Id, "personal reminder question refused (" + (answer.Reason ?? "") + "); said instead"); } catch { }
                _host.SayAll(FormatPersonal(r), SpeechStyleSettings.ToStyle(_settings, "personal."));
                if (once) r.Enabled = false;
                r.SnoozedUntil = null;
                r.Unanswered = 0;
            }
            else if (answer.Outcome == TrayQuestionOutcome.Chosen)
            {
                r.Unanswered = 0;
                if (string.Equals(answer.ChoiceId, Snooze10Choice, StringComparison.Ordinal))
                    r.SnoozedUntil = now.AddMinutes(10);
                else if (once && string.Equals(answer.ChoiceId, Snooze60Choice, StringComparison.Ordinal))
                    r.SnoozedUntil = now.AddMinutes(60);
                else
                {
                    // Done, or a choice this module never offers. A recurring reminder simply waits for its next
                    // occurrence; a one-off is finished.
                    r.SnoozedUntil = null;
                    if (once) r.Enabled = false;
                }
            }
            else
            {
                // TimedOut or Expired: nobody answered.
                if (once)
                {
                    r.Unanswered++;
                    if (r.Unanswered >= MissedAfterUnanswered)
                    {
                        r.Enabled = false;
                        r.SnoozedUntil = null;
                        try { _host.Log(Id, "a one-off personal reminder went unanswered twice and is marked missed"); } catch { }
                    }
                    else r.SnoozedUntil = now.AddMinutes(10);
                }
                else r.SnoozedUntil = null;   // dismissed: the next occurrence comes as scheduled
            }
            SavePersonal(list);
        }

        // ---- the plumbing every question shares ----

        /// <summary>How many questions this tick may still ask, inside the host's per-module limits.</summary>
        private int QuestionRoom(DateTimeOffset now)
        {
            // An ask stamped after `now` means the clock was set back; forgetting it errs towards asking, where keeping
            // it would hold every question until the clock caught up with the stamp.
            while (_askTimes.Count > 0 && (now - _askTimes.Peek() >= TimeSpan.FromMinutes(1) || _askTimes.Peek() > now))
                _askTimes.Dequeue();
            int open = _joinAsks.Count + _personalAsks.Count;
            return Math.Max(0, Math.Min(MaxOpenQuestions - open, MaxAsksPerMinute - _askTimes.Count));
        }

        private void NoteAsked(DateTimeOffset now) { _askTimes.Enqueue(now); }

        private Task<TrayAnswer> AskSafely(TrayQuestion question, CancellationToken withdraw)
        {
            try
            {
                Task<TrayAnswer> asked = _questions.AskAsync(Id, question, withdraw);
                if (asked != null) return asked;
            }
            catch (Exception ex)
            {
                // The type only, as everywhere a question is logged: a message could carry the text.
                try { _host.Log(Id, "a question could not be asked (" + ex.GetType().Name + ")"); } catch { }
            }
            return Task.FromResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Refused, Reason = "ask-failed" });
        }

        /// <summary>
        /// Run <paramref name="apply"/> with the answer on the thread that asked -- the UI thread, where the tick runs and
        /// the settings and the open-question maps are touched. The host completes on its UI thread with continuations run
        /// asynchronously, so this continuation lands on the pool and posts back through the context captured at the ask;
        /// a completion already on that thread (a refusal answered inside AskAsync, or the self-test's double) runs at
        /// once. ContinueWith rather than await, so a test thread whose context has no message loop behind it still sees
        /// its own inline completions applied.
        /// </summary>
        private void WhenAnswered(Task<TrayAnswer> asked, Action<TrayAnswer> apply)
        {
            SynchronizationContext asker = SynchronizationContext.Current;
            asked.ContinueWith(delegate(Task<TrayAnswer> done)
            {
                TrayAnswer answer = done.Status == TaskStatus.RanToCompletion ? done.Result : null;
                if (asker == null || SynchronizationContext.Current == asker) ApplyAnswer(apply, answer);
                else asker.Post(delegate { ApplyAnswer(apply, answer); }, null);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void ApplyAnswer(Action<TrayAnswer> apply, TrayAnswer answer)
        {
            try { apply(answer); }
            catch (Exception ex) { try { _host.Log(Id, "a question's answer could not be applied (" + ex.GetType().Name + ")"); } catch { } }
        }

        // Remove the map entry only if it is still THIS question's: a later question about the same thing must not be
        // forgotten by an earlier one's late answer. The token source is not disposed, here or at Shutdown: one with no
        // timer and no WaitHandle holds nothing but managed memory, and a host that answers Withdrawn inline runs this
        // from inside that source's own Cancel, where disposing it is outside its contract.
        private static void ForgetAsk(Dictionary<string, CancellationTokenSource> asks, string key, CancellationTokenSource withdraw)
        {
            CancellationTokenSource current;
            if (asks.TryGetValue(key, out current) && ReferenceEquals(current, withdraw)) asks.Remove(key);
        }

        /// <summary>Shutdown's half: every open question is withdrawn, and an answer that still arrives is ignored.</summary>
        private void WithdrawAllQuestions()
        {
            _shutDown = true;
            foreach (CancellationTokenSource withdraw in _joinAsks.Values.Concat(_personalAsks.Values).ToList())
            {
                try { withdraw.Cancel(); } catch { }
            }
            _joinAsks.Clear();
            _personalAsks.Clear();
            _askTimes.Clear();
        }

        /// <summary>A question Key for <paramref name="id"/>: "&lt;kind&gt;-" and sixteen hex digits of its SHA-256. Stable,
        /// so the host's duplicate-key refusal still recognises the same question asked twice.</summary>
        internal static string QuestionKey(string kind, string id)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id ?? ""));
            return kind + "-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>
        /// Text for a card: control characters and runs of whitespace become one space, the ends are trimmed, and anything
        /// past <paramref name="max"/> is cut with an ellipsis (never inside a surrogate pair). The host measures Text
        /// after stripping control characters, so a string this returns within the limit is within it there too.
        /// </summary>
        internal static string CardText(string raw, int max)
        {
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in raw ?? "")
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            string text = sb.ToString();
            if (text.Length <= max) return text;
            int cut = max - 1;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut).TrimEnd() + "…";
        }
    }
}
