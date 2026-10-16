using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion
{
    /// <summary>
    /// --trayquestions-selftest (app 1.6.0, lane feature/tray-questions): the question rules, the queue, the card's
    /// placement, the presence mapping, a real card window, and the host's entry point. Writes
    /// %TEMP%\dp-trayquestions-selftest.txt. The queue runs against a fake clock, hold probe and card, so every timing
    /// rule is exercised in milliseconds; the one real window is shown and closed without waiting on anything.
    ///
    /// The privacy rule is asserted on the LINES the log receives, not on the code that writes them: every question in
    /// here carries sentinel text, a sentinel title and sentinel labels, and no captured line may contain any of them.
    /// </summary>
    internal static class TrayQuestionsSelfTest
    {
        private const string SentinelText = "SENTINEL-TEXT-4c1e meeting about Project Bluebird";
        private const string SentinelTitle = "SENTINEL-TITLE-9d2a";
        private const string SentinelLabel = "SENTINEL-LBL";

        internal static bool Run()
        {
            string outp = Path.Combine(Path.GetTempPath(), "dp-trayquestions-selftest.txt");
            var sb = new StringBuilder();
            bool ok = true;
            Action<string, bool> check = delegate(string name, bool condition)
            {
                sb.AppendLine((condition ? "PASS: " : "FAIL: ") + name);
                if (!condition) ok = false;
            };
            try
            {
                Rules(check);
                Arbiter(check);
                Placement(check);
                Presence(check);
                Card(check);
                Host(check);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("FAIL: the self-test threw " + ex.GetType().Name + ": " + ex.Message);
            }
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(outp, sb.ToString()); }
            catch { return false; }
            return ok;
        }

        // ---- fakes ----

        private sealed class FakeClock
        {
            internal DateTimeOffset Now = new DateTimeOffset(2026, 10, 15, 16, 0, 0, TimeSpan.Zero);
            internal void Advance(TimeSpan t) { Now = Now + t; }
        }

        private sealed class RecordingView : IQuestionCardView
        {
            internal QuestionEntry Shown;
            internal int Waiting;
            internal Action<string> OnChoice;
            internal int Shows, Hides, Activations;
            public void Show(QuestionEntry question, int othersWaiting, Action<string> onChoice)
            {
                Shown = question; Waiting = othersWaiting; OnChoice = onChoice; Shows++;
            }
            public void Update(QuestionEntry question, int othersWaiting) { Waiting = othersWaiting; }
            public void Hide() { Shown = null; OnChoice = null; Hides++; }
            public void Activate() { Activations++; }
        }

        private sealed class Rig
        {
            internal readonly FakeClock Clock = new FakeClock();
            internal QuestionHold Hold = QuestionHold.None;
            internal readonly RecordingView View = new RecordingView();
            internal readonly List<string> Log = new List<string>();
            internal readonly List<Action> Posted = new List<Action>();
            internal bool PostInline = true;
            internal bool TimerRunning;
            internal int FirstShown;
            internal readonly TrayQuestionArbiter Arbiter;

            internal Rig()
            {
                Arbiter = new TrayQuestionArbiter(
                    () => Clock.Now, () => Hold, View,
                    id => id == "remembrance" ? "Remembrance" : id,
                    id => id == "remembrance" || id == "reminder",
                    (id, line) => Log.Add(id + " " + line),
                    q => FirstShown++,
                    run => TimerRunning = run,
                    a => { if (PostInline) a(); else lock (Posted) Posted.Add(a); });
            }

            internal void DrainPosted()
            {
                List<Action> batch;
                lock (Posted) { batch = new List<Action>(Posted); Posted.Clear(); }
                foreach (Action a in batch) a();
            }

            /// <summary>Accept and queue a question; returns its answer task.</summary>
            internal Task<TrayAnswer> Ask(TrayQuestion q, string module = "remembrance", CancellationToken withdraw = default(CancellationToken))
            {
                var completion = new TaskCompletionSource<TrayAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
                QuestionEntry entry;
                string reason;
                if (!TrayQuestionRules.TryAccept(q, Clock.Now, out entry, out reason))
                {
                    completion.TrySetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Refused, Reason = reason });
                    return completion.Task;
                }
                entry.ModuleId = module;
                entry.Completion = completion;
                Arbiter.Ask(entry, withdraw);
                return completion.Task;
            }

            internal void Ticks(int seconds)
            {
                for (int i = 0; i < seconds; i++) { Clock.Advance(TimeSpan.FromSeconds(1)); Arbiter.Tick(); }
            }

            /// <summary>Press a button after the click guard.</summary>
            internal void Press(string choiceId)
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                Action<string> press = View.OnChoice;
                if (press != null) press(choiceId);
            }
        }

        private static TrayQuestion Q(string key, int timeoutSeconds = 60, bool runsWhileHeld = false, DateTimeOffset? deadline = null)
        {
            return new TrayQuestion
            {
                Key = key,
                Title = SentinelTitle,
                Text = SentinelText,
                Choices = new[] { new TrayChoice("keep", SentinelLabel + " keep"), new TrayChoice("stop", SentinelLabel + " stop") },
                TimeoutChoiceId = "stop",
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                RunsWhileHeld = runsWhileHeld,
                Deadline = deadline,
            };
        }

        private static bool Ended(Task<TrayAnswer> t, TrayQuestionOutcome outcome, string choice = null)
        {
            if (!t.IsCompleted) return false;
            TrayAnswer a = t.Result;
            return a != null && a.Outcome == outcome && (choice == null || string.Equals(a.ChoiceId, choice, StringComparison.Ordinal));
        }

        // ---- the rules ----

        private static void Rules(Action<string, bool> check)
        {
            DateTimeOffset now = new DateTimeOffset(2026, 10, 15, 16, 0, 0, TimeSpan.Zero);
            Func<TrayQuestion, string> refusal = delegate(TrayQuestion q)
            {
                QuestionEntry e; string reason;
                return TrayQuestionRules.TryAccept(q, now, out e, out reason) ? null : reason;
            };
            TrayQuestion good = Q("remembrance.meeting-over");
            QuestionEntry accepted; string why;
            check("rules: a well-formed question is accepted",
                TrayQuestionRules.TryAccept(good, now, out accepted, out why) && accepted != null && why == null);
            var mutable = new List<TrayChoice> { new TrayChoice("a", "Alpha"), new TrayChoice("b", "Beta") };
            var original = new TrayQuestion { Key = "k", Text = "Original text", Choices = mutable, TimeoutChoiceId = "a", Timeout = TimeSpan.FromSeconds(30) };
            QuestionEntry copy;
            TrayQuestionRules.TryAccept(original, now, out copy, out why);
            original.Text = "Changed"; mutable[0].Label = "Changed"; mutable.Add(new TrayChoice("c", "Gamma"));
            check("rules: the host keeps a COPY -- changing the question afterwards changes nothing",
                copy != null && copy.Text == "Original text" && copy.Choices.Count == 2 && copy.Choices[0].Label == "Alpha");

            Func<Action<TrayQuestion>, TrayQuestion> variant = delegate(Action<TrayQuestion> edit)
            {
                TrayQuestion v = Q("remembrance.meeting-over");
                edit(v);
                return v;
            };
            check("rules: no question is refused", refusal(null) == "no-question");
            check("rules: a key outside [A-Za-z0-9._-] is refused (it is the one thing logged verbatim)",
                refusal(variant(v => v.Key = "has space")) == "bad-key" && refusal(variant(v => v.Key = "")) == "bad-key"
                && refusal(variant(v => v.Key = new string('k', 65))) == "bad-key");
            check("rules: empty or whitespace-only text is refused", refusal(variant(v => v.Text = " \t\n ")) == "no-text");
            check("rules: text over 240 characters is refused, not trimmed", refusal(variant(v => v.Text = new string('x', 241))) == "text-too-long"
                && refusal(variant(v => v.Text = new string('x', 240))) == null);
            check("rules: a title over 64 characters is refused", refusal(variant(v => v.Title = new string('t', 65))) == "title-too-long");
            check("rules: fewer than two choices is refused",
                refusal(variant(v => v.Choices = new[] { new TrayChoice("stop", "Stop") })) == "too-few-choices");
            check("rules: more than four choices is refused", refusal(variant(v => v.Choices = new[]
            {
                new TrayChoice("a", "A"), new TrayChoice("b", "B"), new TrayChoice("c", "C"), new TrayChoice("d", "D"), new TrayChoice("stop", "Stop"),
            })) == "too-many-choices");
            check("rules: a null choice, a bad id, an empty or 25-character label are refused",
                refusal(variant(v => v.Choices = new[] { null, new TrayChoice("stop", "Stop") })) == "bad-choice"
                && refusal(variant(v => v.Choices = new[] { new TrayChoice("bad id", "A"), new TrayChoice("stop", "Stop") })) == "bad-choice-id"
                && refusal(variant(v => v.Choices = new[] { new TrayChoice("a", "  "), new TrayChoice("stop", "Stop") })) == "bad-choice-label"
                && refusal(variant(v => v.Choices = new[] { new TrayChoice("a", new string('L', 25)), new TrayChoice("stop", "Stop") })) == "bad-choice-label");
            check("rules: duplicate ids, and labels that differ only in case, are refused",
                refusal(variant(v => v.Choices = new[] { new TrayChoice("stop", "One"), new TrayChoice("stop", "Two") })) == "duplicate-choice-id"
                && refusal(variant(v => v.Choices = new[] { new TrayChoice("a", "Stop now"), new TrayChoice("stop", "STOP NOW") })) == "duplicate-choice-label");
            check("rules: a timeout choice that is not a choice is refused", refusal(variant(v => v.TimeoutChoiceId = "later")) == "bad-timeout-choice");
            check("rules: a timeout under 15 s or over 60 min is refused",
                refusal(variant(v => v.Timeout = TimeSpan.FromSeconds(14))) == "bad-timeout"
                && refusal(variant(v => v.Timeout = TimeSpan.FromMinutes(61))) == "bad-timeout"
                && refusal(variant(v => v.Timeout = TimeSpan.FromSeconds(15))) == null);
            check("rules: a deadline under 5 s or over 24 h ahead is refused",
                refusal(variant(v => v.Deadline = now.AddSeconds(2))) == "bad-deadline"
                && refusal(variant(v => v.Deadline = now.AddHours(25))) == "bad-deadline"
                && refusal(variant(v => v.Deadline = now.AddMinutes(3))) == null);
            check("rules: control characters and bidi overrides are stripped and whitespace collapsed",
                TrayQuestionRules.Sanitize("  Keep‮recording\u0007?\r\n  Yes  ") == "Keeprecording? Yes");
        }

        // ---- the queue ----

        private static void Arbiter(Action<string, bool> check)
        {
            // Shown at once, answered after the click guard.
            var r = new Rig();
            Task<TrayAnswer> a = r.Ask(Q("remembrance.meeting-over"));
            check("queue: an accepted question is shown at once when nothing holds it, and the 1 s timer starts",
                r.View.Shown != null && r.View.Shown.Key == "remembrance.meeting-over" && r.TimerRunning && r.FirstShown == 1);
            r.View.OnChoice("keep");
            check("queue: a click inside the 500 ms click guard is ignored", !a.IsCompleted && r.View.Shown != null);
            r.Press("keep");
            check("queue: a press answers Chosen with that id, hides the card and stops the timer",
                Ended(a, TrayQuestionOutcome.Chosen, "keep") && a.Result.WasShown && r.View.Shown == null && !r.TimerRunning);

            // Timed out on screen.
            r = new Rig();
            a = r.Ask(Q("remembrance.limit", 20));
            r.Ticks(19);
            check("queue: still waiting one second before its timeout", !a.IsCompleted);
            r.Ticks(1);
            check("queue: seen and unanswered ends TimedOut carrying TimeoutChoiceId -- the module, not the host, applies it",
                Ended(a, TrayQuestionOutcome.TimedOut, "stop") && a.Result.WasShown);
            check("queue: it is listed under No answer with the label that was applied",
                r.Arbiter.Unanswered.Count == 1 && r.Arbiter.Unanswered[0].AppliedLabel == SentinelLabel + " stop");
            check("queue: the countdown line names the time left and the no-answer choice",
                TrayQuestionArbiter.CountdownText(new QuestionEntry
                {
                    Remaining = TimeSpan.FromSeconds(102), Timeout = TimeSpan.FromSeconds(120), TimeoutChoiceId = "stop",
                    Choices = new List<TrayChoice> { new TrayChoice("stop", "Stop now") },
                }, r.Clock.Now) == "No answer in 1:42 → Stop now");

            // Held: an ordinary question waits with its countdown standing still.
            r = new Rig { Hold = QuestionHold.Presenting };
            a = r.Ask(Q("reminder.join", 30), "reminder");
            r.Ticks(120);
            check("queue: while presenting an ordinary question is not shown and its countdown stands still",
                !a.IsCompleted && r.View.Shown == null && r.View.Shows == 0);
            check("queue: the hold is logged once, not once a tick",
                r.Log.FindAll(l => l.Contains("[question] held")).Count == 1);
            r.Hold = QuestionHold.None;
            r.Ticks(1);
            check("queue: when the hold lifts it is shown", r.View.Shown != null && !a.IsCompleted);

            // RunsWhileHeld: the countdown runs unseen.
            r = new Rig { Hold = QuestionHold.Fullscreen };
            a = r.Ask(Q("remembrance.meeting-over", 15, true));
            r.Ticks(15);
            check("queue: a RunsWhileHeld question counts down while held and ends Expired, unseen, with its timeout choice",
                Ended(a, TrayQuestionOutcome.Expired, "stop") && !a.Result.WasShown && r.View.Shows == 0);

            // Deadline while held.
            r = new Rig { Hold = QuestionHold.Locked };
            a = r.Ask(Q("reminder.join", 60, false, r.Clock.Now.AddSeconds(10)), "reminder");
            r.Ticks(10);
            check("queue: a deadline reached while held ends Expired", Ended(a, TrayQuestionOutcome.Expired, "stop"));

            // Visible, then held: hidden; then shown again with at least the 30 s floor.
            r = new Rig();
            a = r.Ask(Q("remembrance.past-end", 60));
            r.Ticks(50);
            r.Hold = QuestionHold.Presenting;
            r.Ticks(1);
            check("queue: a card on screen is hidden when presenting starts", r.View.Shown == null && !a.IsCompleted);
            r.Ticks(300);
            r.Hold = QuestionHold.None;
            r.Ticks(1);
            check("queue: shown again after the hold with at least 30 s left, not the 9 it had",
                r.View.Shown != null && r.View.Shown.Remaining >= TimeSpan.FromSeconds(29) && !a.IsCompleted);

            // One at a time, in order.
            r = new Rig();
            Task<TrayAnswer> first = r.Ask(Q("remembrance.next-meeting"));
            Task<TrayAnswer> second = r.Ask(Q("reminder.personal.a1"), "reminder");
            check("queue: one card at a time -- the first asked is shown and the card says one more is waiting",
                r.View.Shown != null && r.View.Shown.Key == "remembrance.next-meeting" && r.View.Waiting == 1);
            r.Press("keep");
            check("queue: when the first ends the next is shown", Ended(first, TrayQuestionOutcome.Chosen) && r.View.Shown != null && r.View.Shown.Key == "reminder.personal.a1");

            // Pre-emption: urgent takes the card from ordinary; urgent never displaces urgent.
            r = new Rig();
            Task<TrayAnswer> ordinary = r.Ask(Q("remembrance.past-end"));
            Task<TrayAnswer> urgent = r.Ask(Q("remembrance.limit", 120, true));
            check("queue: an urgent question takes the card from an ordinary one, which keeps waiting",
                r.View.Shown != null && r.View.Shown.Key == "remembrance.limit" && !ordinary.IsCompleted);
            r.Ask(Q("remembrance.meeting-over", 30, true));
            check("queue: an urgent question never displaces another urgent one already on screen",
                r.View.Shown != null && r.View.Shown.Key == "remembrance.limit");

            // Withdraw from another thread runs only on the UI thread, through the post.
            r = new Rig { PostInline = false };
            var cts = new CancellationTokenSource();
            a = r.Ask(Q("remembrance.offer-record"), "remembrance", cts.Token);
            Task.Run(() => cts.Cancel()).Wait();
            bool waitedForPost = !a.IsCompleted;
            r.DrainPosted();
            check("queue: a withdraw cancelled on a worker thread waits for the UI-thread post, then answers Withdrawn",
                waitedForPost && Ended(a, TrayQuestionOutcome.Withdrawn) && a.Result.ChoiceId == null && r.View.Shown == null);

            r = new Rig();
            cts = new CancellationTokenSource();
            a = r.Ask(Q("remembrance.offer-record"), "remembrance", cts.Token);
            r.Press("keep");
            cts.Cancel();
            check("queue: a withdraw that arrives after a click changes nothing -- first to the UI thread wins",
                Ended(a, TrayQuestionOutcome.Chosen, "keep"));

            r = new Rig();
            cts = new CancellationTokenSource();
            cts.Cancel();
            a = r.Ask(Q("remembrance.offer-record"), "remembrance", cts.Token);
            check("queue: a question asked already withdrawn answers Withdrawn and is never shown",
                Ended(a, TrayQuestionOutcome.Withdrawn) && r.View.Shows == 0);

            // Sleep guard.
            r = new Rig();
            a = r.Ask(Q("remembrance.past-end", 60));
            r.Clock.Advance(TimeSpan.FromHours(1));
            r.Arbiter.Tick();
            check("queue: an hour between ticks (the machine slept) costs a visible card one second, not the whole hour",
                !a.IsCompleted && r.View.Shown != null && r.View.Shown.Remaining == TimeSpan.FromSeconds(59));

            // ShowNow through a hold, until the hold changes.
            r = new Rig { Hold = QuestionHold.Presenting };
            a = r.Ask(Q("reminder.join"), "reminder");
            r.Arbiter.ShowNow();
            check("queue: the tray's Answer row shows a held question anyway and gives it the keyboard",
                r.View.Shown != null && r.View.Activations == 1);
            r.Hold = QuestionHold.Fullscreen;
            r.Ticks(1);
            check("queue: ...until the hold changes", r.View.Shown == null && !a.IsCompleted);

            // Cancel.
            r = new Rig();
            Task<TrayAnswer> mine = r.Ask(Q("remembrance.limit"));
            Task<TrayAnswer> theirs = r.Ask(Q("reminder.join"), "reminder");
            r.Arbiter.CancelModule("remembrance");
            check("queue: cancelling one module's questions leaves another's, and the next is shown",
                Ended(mine, TrayQuestionOutcome.Cancelled) && !theirs.IsCompleted && r.View.Shown != null && r.View.Shown.Key == "reminder.join");
            r.Arbiter.CancelAll();
            check("queue: CancelAll (the app closing) answers Cancelled with no choice and hides the card",
                Ended(theirs, TrayQuestionOutcome.Cancelled) && theirs.Result.ChoiceId == null && r.View.Shown == null && !r.TimerRunning);

            // Limits.
            r = new Rig();
            check("queue: an unknown module is refused", Ended(r.Ask(Q("x.y"), "nosuchmodule"), TrayQuestionOutcome.Refused)
                && r.Ask(Q("x.y"), "nosuchmodule").Result.Reason == "unknown-module");
            r.Ask(Q("k1"));
            Task<TrayAnswer> dup = r.Ask(Q("k1"));
            check("queue: a second pending question with the same key is refused", Ended(dup, TrayQuestionOutcome.Refused) && dup.Result.Reason == "duplicate-key");
            r.Ask(Q("k2")); r.Ask(Q("k3")); r.Ask(Q("k4"));
            Task<TrayAnswer> fifth = r.Ask(Q("k5"));
            check("queue: more than four pending from one module is refused", Ended(fifth, TrayQuestionOutcome.Refused) && fifth.Result.Reason == "too-many-pending");
            r = new Rig();
            Task<TrayAnswer> last = null;
            for (int i = 0; i < 7; i++)
            {
                last = r.Ask(Q("burst" + i));
                r.Arbiter.CancelModule("remembrance");
            }
            check("queue: more than six asks from one module in a minute is refused", Ended(last, TrayQuestionOutcome.Refused) && last.Result.Reason == "rate-limited");

            // Continuations never run inside the host's own call.
            r = new Rig();
            a = r.Ask(Q("remembrance.meeting-over"));
            bool insidePress = false, ranInside = false;
            int testThread = Thread.CurrentThread.ManagedThreadId;
            Task follow = a.ContinueWith(t => { ranInside = insidePress && Thread.CurrentThread.ManagedThreadId == testThread; },
                TaskContinuationOptions.ExecuteSynchronously);
            insidePress = true;
            r.Press("keep");
            insidePress = false;
            follow.Wait(5000);
            check("queue: an answer's continuation runs asynchronously, never inside the host's click handler", follow.IsCompleted && !ranInside);

            // The heads-up hook fires once per question, on first show only.
            r = new Rig();
            a = r.Ask(Q("remembrance.past-end", 60));
            r.Hold = QuestionHold.Presenting; r.Ticks(1);
            r.Hold = QuestionHold.None; r.Ticks(1);
            check("queue: the first-shown hook (the bubble heads-up) fires once, not again on a re-show", r.FirstShown == 1 && r.View.Shows == 2);

            // The log never carries the question.
            var logs = new List<string>();
            foreach (Rig each in new[] { r }) logs.AddRange(each.Log);
            r = new Rig();
            r.Ask(Q("remembrance.meeting-over")); r.Press("keep");
            r.Ask(Q("remembrance.limit", 15)); r.Ticks(16);
            r.Ask(Q("bad key here"));
            logs.AddRange(r.Log);
            bool keys = logs.Exists(l => l.Contains("key=remembrance.meeting-over") && l.Contains("outcome=Chosen") && l.Contains("choice=keep"));
            bool clean = !logs.Exists(l => l.Contains("SENTINEL") || l.Contains("Bluebird"));
            check("log: the lines carry the key, the outcome and the choice id (" + logs.Count + " lines captured)", keys && logs.Count >= 6);
            check("log: no line carries the question's text, title or any label", clean);
        }

        // ---- placement ----

        private static void Placement(Action<string, bool> check)
        {
            var monitor = new Rectangle(0, 0, 2560, 1440);
            var card = new Size(480, 220);
            bool allInside = true, allCornered = true, noOverlap = true;
            int cases = 0;
            foreach (TaskbarEdge edge in new[] { TaskbarEdge.Bottom, TaskbarEdge.Top, TaskbarEdge.Left, TaskbarEdge.Right })
                foreach (bool autoHide in new[] { false, true })
                    foreach (int dpi in new[] { 96, 120, 144, 192 })
                        foreach (bool atStart in new[] { false, true })
                        {
                            int thick = (int)Math.Round(48 * dpi / 96.0);
                            Rectangle bar, work;
                            switch (edge)
                            {
                                case TaskbarEdge.Top: bar = new Rectangle(0, 0, monitor.Width, thick); work = new Rectangle(0, thick, monitor.Width, monitor.Height - thick); break;
                                case TaskbarEdge.Left: bar = new Rectangle(0, 0, thick, monitor.Height); work = new Rectangle(thick, 0, monitor.Width - thick, monitor.Height); break;
                                case TaskbarEdge.Right: bar = new Rectangle(monitor.Width - thick, 0, thick, monitor.Height); work = new Rectangle(0, 0, monitor.Width - thick, monitor.Height); break;
                                default: bar = new Rectangle(0, monitor.Height - thick, monitor.Width, thick); work = new Rectangle(0, 0, monitor.Width, monitor.Height - thick); break;
                            }
                            if (autoHide) work = monitor;
                            var tb = new TaskbarInfo { Found = true, Monitor = monitor, WorkArea = work, Taskbar = bar, Edge = edge, AutoHide = autoHide, TrayAtStart = atStart, Dpi = dpi };
                            Point p = TrayAnchor.Compute(tb, card);
                            var placed = new Rectangle(p, card);
                            Rectangle area = TrayAnchor.PlacementArea(tb);
                            int m = (int)Math.Round(TrayAnchor.MarginLogical * dpi / 96.0, MidpointRounding.AwayFromZero);
                            cases++;
                            if (!area.Contains(placed)) allInside = false;
                            if (placed.IntersectsWith(bar)) noOverlap = false;
                            // The corner nearest the tray: along the bar, the tray's end; across it, the bar's side.
                            bool horizontal = edge == TaskbarEdge.Bottom || edge == TaskbarEdge.Top;
                            int alongGap = horizontal
                                ? (atStart ? placed.Left - area.Left : area.Right - placed.Right)
                                : (atStart ? placed.Top - area.Top : area.Bottom - placed.Bottom);
                            int acrossGap;
                            switch (edge)
                            {
                                case TaskbarEdge.Top: acrossGap = placed.Top - area.Top; break;
                                case TaskbarEdge.Left: acrossGap = placed.Left - area.Left; break;
                                case TaskbarEdge.Right: acrossGap = area.Right - placed.Right; break;
                                default: acrossGap = area.Bottom - placed.Bottom; break;
                            }
                            if (alongGap != m || acrossGap != m) allCornered = false;
                        }
            check("placement: " + cases + " cases (4 edges x auto-hide x 4 DPIs x tray at either end) -- the card is inside the area", allInside && cases == 64);
            check("placement: ...a margin in from the tray's corner, scaled to the DPI", allCornered);
            check("placement: ...and never over the taskbar, even an auto-hiding one", noOverlap);
            check("placement: a wide bar in the lower half is the bottom edge; a tall one on the right is the right edge",
                TrayAnchor.EdgeOf(new Rectangle(0, 1392, 2560, 48), monitor) == TaskbarEdge.Bottom
                && TrayAnchor.EdgeOf(new Rectangle(2512, 0, 48, 1440), monitor) == TaskbarEdge.Right);
            check("placement: a work area that reaches the monitor edge under the bar is an auto-hiding taskbar",
                TrayAnchor.ReservesNothing(TaskbarEdge.Bottom, monitor, monitor)
                && !TrayAnchor.ReservesNothing(TaskbarEdge.Bottom, new Rectangle(0, 0, 2560, 1392), monitor));
            TaskbarInfo live = TrayAnchor.Read();
            check("placement: reading the real taskbar does not throw and yields a usable area (found=" + (live.Found ? "yes" : "no") + ")",
                !TrayAnchor.PlacementArea(live).IsEmpty);
        }

        // ---- presence ----

        private static void Presence(Action<string, bool> check)
        {
            check("presence: locked/screensaver, presentation mode, fullscreen and quiet time hold; normal does not",
                UserPresence.FromNotificationState(1) == QuestionHold.Locked
                && UserPresence.FromNotificationState(2) == QuestionHold.Fullscreen
                && UserPresence.FromNotificationState(3) == QuestionHold.Fullscreen
                && UserPresence.FromNotificationState(4) == QuestionHold.Presenting
                && UserPresence.FromNotificationState(5) == QuestionHold.None
                && UserPresence.FromNotificationState(6) == QuestionHold.Quiet
                && UserPresence.FromNotificationState(7) == QuestionHold.Fullscreen
                && UserPresence.FromNotificationState(99) == QuestionHold.None);
            bool threw = false;
            try { UserPresence.Current(null); } catch { threw = true; }
            check("presence: asking the real shell with no app does not throw", !threw);
        }

        // ---- the card ----

        private static void Card(Action<string, bool> check)
        {
            var card = new DesktopAICompanion.Wpf.TrayQuestionCard();
            var entry = new QuestionEntry
            {
                Seq = 1, ModuleId = "remembrance", ModuleName = "Remembrance", Key = "selftest.card",
                Text = "Your meeting looks over. Keep recording?",
                Choices = new List<TrayChoice> { new TrayChoice("keep", "Keep recording"), new TrayChoice("snooze", "Snooze_1") },
                TimeoutChoiceId = "keep", Timeout = TimeSpan.FromSeconds(120), Remaining = TimeSpan.FromSeconds(120),
            };
            string pressed = null;
            card.Show(entry, 0, id => pressed = id);
            try
            {
                IntPtr hwnd = card.HandleForSelfTest;
                check("card: a window is created for the question", card.WindowForSelfTest != null && hwnd != IntPtr.Zero);
                int ex = DesktopAICompanion.Wpf.TrayQuestionCard.GetWindowLong(hwnd, -20);
                check("card: it never activates on its own (WS_EX_NOACTIVATE) and stays out of Alt+Tab (WS_EX_TOOLWINDOW)",
                    (ex & DesktopAICompanion.Wpf.TrayQuestionCard.WS_EX_NOACTIVATE) != 0 && (ex & DesktopAICompanion.Wpf.TrayQuestionCard.WS_EX_TOOLWINDOW) != 0);
                uint affinity;
                bool readAffinity = DesktopAICompanion.Wpf.TrayQuestionCard.GetWindowDisplayAffinity(hwnd, out affinity);
                check("card: it is excluded from screen capture (affinity 0x" + affinity.ToString("x") + ")",
                    readAffinity && (affinity == DesktopAICompanion.Wpf.TrayQuestionCard.WDA_EXCLUDEFROMCAPTURE
                                     || affinity == DesktopAICompanion.Wpf.TrayQuestionCard.WDA_MONITOR));
                var buttons = card.ButtonsForSelfTest;
                bool names = buttons.Count == 2
                    && System.Windows.Automation.AutomationProperties.GetName(buttons[0]) == "Keep recording"
                    && System.Windows.Automation.AutomationProperties.GetName(buttons[1]) == "Snooze_1";
                check("card: one button per choice, each named for screen readers by its label", names);
                bool noClose = true;
                foreach (var b in buttons)
                    if (string.Equals(System.Windows.Automation.AutomationProperties.GetName(b), "Close", StringComparison.OrdinalIgnoreCase)) noClose = false;
                check("card: there is no Close button -- one of the choices always ends a question", noClose);
                var literal = buttons.Count == 2 ? buttons[1].Content as System.Windows.Controls.TextBlock : null;
                check("card: a label is drawn literally ('Snooze_1' keeps its underscore, no access key)", literal != null && literal.Text == "Snooze_1");
                check("card: its buttons do not take focus until the user clicks the card", buttons.Count == 2 && !buttons[0].Focusable && !buttons[1].Focusable);
                // The card is excluded from screen capture, so a screenshot shows nothing where it sits: render its
                // visual tree to a file instead, for a person to look at (%TEMP%\dp-trayquestions-card.png).
                string png = Path.Combine(Path.GetTempPath(), "dp-trayquestions-card.png");
                bool rendered = false;
                try
                {
                    System.Windows.Window win = card.WindowForSelfTest;
                    win.UpdateLayout();
                    int w = (int)Math.Ceiling(win.ActualWidth), h = (int)Math.Ceiling(win.ActualHeight);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w * 2, h * 2, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(win);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using (FileStream fs = File.Create(png)) encoder.Save(fs);
                    rendered = new FileInfo(png).Length > 0;
                }
                catch { rendered = false; }
                check("card: its contents render to an image for a visual check (" + png + ")", rendered);
                buttons[0].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                check("card: pressing a button reports its choice id", pressed == "keep");
            }
            finally
            {
                card.Hide();
            }
            check("card: Hide closes the window", card.WindowForSelfTest == null);
        }

        // ---- the host's entry point ----

        private static void Host(Action<string, bool> check)
        {
            var host = new DesktopAICompanion.Plugins.CompanionHost(null);
            ITrayQuestions questions = host as ITrayQuestions;
            check("host: the host's IHost is an ITrayQuestions, as the ABI promises", questions != null);
            if (questions == null) return;
            Task<TrayAnswer> noApp = questions.AskAsync("remembrance", Q("remembrance.meeting-over"), CancellationToken.None);
            check("host: with no app to show it, a question answers Refused (not-available) and never throws or faults",
                noApp.Wait(5000) && !noApp.IsFaulted && noApp.Result.Outcome == TrayQuestionOutcome.Refused && noApp.Result.Reason == "not-available");
            Task<TrayAnswer> invalid = questions.AskAsync("remembrance", new TrayQuestion { Key = "k" }, CancellationToken.None);
            check("host: an invalid question answers Refused with the rule it broke",
                invalid.Wait(5000) && invalid.Result.Outcome == TrayQuestionOutcome.Refused && invalid.Result.Reason == "no-text");
            Task<TrayAnswer> nothing = questions.AskAsync("remembrance", null, CancellationToken.None);
            check("host: a null question answers Refused", nothing.Wait(5000) && nothing.Result.Outcome == TrayQuestionOutcome.Refused);
        }
    }
}
