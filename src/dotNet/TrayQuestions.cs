using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion
{
    // Questions by the clock (app 1.6.0, lane feature/tray-questions): the host half of ITrayQuestions. A module asks; the
    // rules below take a copy or refuse it; the arbiter queues it, decides which one the card shows and when, counts it
    // down, and reports how it ended. It APPLIES NOTHING -- a timeout answers with the module's TimeoutChoiceId and the
    // module acts on it -- and it never logs a question's text, title or labels: users attach the diagnostics log to
    // issues, and a question can carry a meeting title. The owner's decisions (card by the clock, never the bubble,
    // hidden from capture, held while presenting / fullscreen / locked) are under "#### feature/tray-questions" in
    // docs/DESIGN-REGISTER.md.
    //
    // Everything here is pure and UI-thread: the clock, the hold probe, the card and the post-to-UI are injected, so
    // --trayquestions-selftest drives every rule with no window and no waiting.

    /// <summary>Why the card is not on screen right now. None: it may be.</summary>
    internal enum QuestionHold { None = 0, Presenting = 1, Fullscreen = 2, Locked = 3, Quiet = 4 }

    /// <summary>The host's own copy of one question, taken at ask time, plus its state in the queue.</summary>
    internal sealed class QuestionEntry
    {
        internal int Seq;
        internal string ModuleId = "";
        internal string ModuleName = "";
        internal string Key = "";
        internal string Title = "";
        internal string Text = "";
        internal List<TrayChoice> Choices = new List<TrayChoice>();
        internal string TimeoutChoiceId = "";
        internal TimeSpan Timeout;
        internal DateTimeOffset? Deadline;
        internal bool RunsWhileHeld;
        internal DateTimeOffset AskedUtc;

        internal TimeSpan Remaining;
        internal bool Visible;
        internal bool WasShown;
        internal bool Ended;
        internal DateTimeOffset ShownUtc;
        internal QuestionHold HeldFor;   // the hold last logged for it, so "held" is logged once per hold, not per tick
        internal TaskCompletionSource<TrayAnswer> Completion;
        internal CancellationTokenRegistration Registration;

        /// <summary>Urgent questions go first and may take the card from one that is not: their no-answer outcome is safe
        /// to apply unseen (RunsWhileHeld), or they stop mattering at a fixed time (Deadline).</summary>
        internal bool Urgent { get { return RunsWhileHeld || Deadline.HasValue; } }

        internal string LabelOf(string choiceId)
        {
            foreach (TrayChoice c in Choices)
                if (c != null && string.Equals(c.Id, choiceId, StringComparison.Ordinal)) return c.Label;
            return "";
        }

        /// <summary>When it will end on its own if nobody answers: the earlier of its Deadline and, for a countdown that
        /// runs while held, now plus what is left. Orders the urgent questions.</summary>
        internal DateTimeOffset EffectiveEnd(DateTimeOffset nowUtc)
        {
            DateTimeOffset end = Deadline ?? DateTimeOffset.MaxValue;
            if (RunsWhileHeld)
            {
                DateTimeOffset byCountdown = nowUtc + (Remaining > TimeSpan.Zero ? Remaining : TimeSpan.Zero);
                if (byCountdown < end) end = byCountdown;
            }
            return end;
        }
    }

    /// <summary>A question that ended without an answer, for the tray's "No answer" list. Session-only, never written.</summary>
    internal sealed class UnansweredQuestion
    {
        internal DateTime EndedLocal;
        internal string ModuleName = "";
        internal string Text = "";
        internal string AppliedLabel = "";
    }

    /// <summary>The card, as the arbiter sees it. The real one is TrayQuestionCard (WPF); the self-test's is a recorder.</summary>
    internal interface IQuestionCardView
    {
        /// <summary>Show this question, replacing whatever is on screen. <paramref name="onChoice"/> is called with a
        /// choice id when a button is pressed, on the UI thread.</summary>
        void Show(QuestionEntry question, int othersWaiting, Action<string> onChoice);
        /// <summary>Refresh the countdown and the "+N waiting" line for the question already shown.</summary>
        void Update(QuestionEntry question, int othersWaiting);
        void Hide();
        /// <summary>Give the shown card the keyboard (the tray's "Answer" row, when the card is already up).</summary>
        void Activate();
    }

    internal static class TrayQuestionRules
    {
        internal const int MaxKey = 64;
        internal const int MaxTitle = 64;
        internal const int MaxText = 240;
        internal const int MaxChoiceId = 32;
        internal const int MaxLabel = 24;
        internal static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(60);
        internal static readonly TimeSpan MinDeadlineAhead = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan MaxDeadlineAhead = TimeSpan.FromHours(24);

        /// <summary>
        /// Take a COPY of the question, or refuse it with a fixed reason token. Refused rather than trimmed, so a module
        /// learns of an over-long label in its own self-test and not from a clipped button on a user's screen. Text and
        /// labels are sanitised first (controls and bidi overrides stripped, whitespace collapsed), and the limits apply
        /// to what would be drawn.
        /// </summary>
        internal static bool TryAccept(TrayQuestion q, DateTimeOffset nowUtc, out QuestionEntry entry, out string reason)
        {
            entry = null;
            reason = null;
            if (q == null) { reason = "no-question"; return false; }
            string key = q.Key ?? "";
            if (!IsToken(key, MaxKey)) { reason = "bad-key"; return false; }
            string text = Sanitize(q.Text);
            if (text.Length == 0) { reason = "no-text"; return false; }
            if (text.Length > MaxText) { reason = "text-too-long"; return false; }
            string title = Sanitize(q.Title);
            if (title.Length > MaxTitle) { reason = "title-too-long"; return false; }
            if (q.Choices == null || q.Choices.Count < 2) { reason = "too-few-choices"; return false; }
            if (q.Choices.Count > 4) { reason = "too-many-choices"; return false; }

            var choices = new List<TrayChoice>(q.Choices.Count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TrayChoice c in q.Choices)
            {
                if (c == null) { reason = "bad-choice"; return false; }
                string id = c.Id ?? "";
                if (!IsToken(id, MaxChoiceId)) { reason = "bad-choice-id"; return false; }
                string label = Sanitize(c.Label);
                if (label.Length == 0 || label.Length > MaxLabel) { reason = "bad-choice-label"; return false; }
                if (!ids.Add(id)) { reason = "duplicate-choice-id"; return false; }
                // Unique ignoring case: two buttons a screen reader announces identically cannot be told apart.
                if (!labels.Add(label)) { reason = "duplicate-choice-label"; return false; }
                choices.Add(new TrayChoice(id, label));
            }
            if (string.IsNullOrEmpty(q.TimeoutChoiceId) || !ids.Contains(q.TimeoutChoiceId)) { reason = "bad-timeout-choice"; return false; }
            if (q.Timeout < MinTimeout || q.Timeout > MaxTimeout) { reason = "bad-timeout"; return false; }
            DateTimeOffset? deadline = null;
            if (q.Deadline.HasValue)
            {
                TimeSpan ahead = q.Deadline.Value - nowUtc;
                if (ahead < MinDeadlineAhead || ahead > MaxDeadlineAhead) { reason = "bad-deadline"; return false; }
                deadline = q.Deadline.Value.ToUniversalTime();
            }

            entry = new QuestionEntry
            {
                Key = key,
                Title = title,
                Text = text,
                Choices = choices,
                TimeoutChoiceId = q.TimeoutChoiceId,
                Timeout = q.Timeout,
                Deadline = deadline,
                RunsWhileHeld = q.RunsWhileHeld,
                AskedUtc = nowUtc,
                Remaining = q.Timeout,
            };
            return true;
        }

        /// <summary>1..max characters of [A-Za-z0-9._-]: the only shape a Key or choice id can take, which is what makes a
        /// Key safe to write to the log verbatim.</summary>
        internal static bool IsToken(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length > max) return false;
            foreach (char ch in s)
            {
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
                    || ch == '.' || ch == '_' || ch == '-';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>What would be drawn: control characters and bidirectional overrides removed (a module cannot reverse
        /// the card's text or hide a line break in a label), every run of whitespace one space, trimmed.</summary>
        internal static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char ch in s)
            {
                if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
                if (char.IsControl(ch) || IsBidiControl(ch)) continue;
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static bool IsBidiControl(char ch)
        {
            return (ch >= '‪' && ch <= '‮') || (ch >= '⁦' && ch <= '⁩')
                || ch == '‎' || ch == '‏' || ch == '؜';
        }
    }

    /// <summary>
    /// The queue. UI thread only: every entry point is either called there (a click, the 1 s tick, a fullscreen change)
    /// or reached through the injected post. One card at a time; the rest wait.
    /// </summary>
    internal sealed class TrayQuestionArbiter
    {
        internal const int MaxPendingPerModule = 4;
        internal const int MaxPendingTotal = 16;
        internal const int MaxAsksPerMinute = 6;
        internal const int UnansweredKept = 10;
        /// <summary>Clicks that land this soon after a card appears or changes are ignored, so a click aimed at the card
        /// that was there cannot answer the one that replaced it.</summary>
        internal static readonly TimeSpan ClickGuard = TimeSpan.FromMilliseconds(500);
        /// <summary>A question whose countdown stood still while it was held gets at least this long when it is shown
        /// again, so the user is not handed a card with four seconds left.</summary>
        internal static readonly TimeSpan ReshowFloor = TimeSpan.FromSeconds(30);
        /// <summary>A gap between ticks longer than this is the machine having slept. A visible card that waits for its
        /// user is charged one second for it, not the whole night.</summary>
        internal static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(5);

        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Func<QuestionHold> _hold;
        private readonly IQuestionCardView _view;
        private readonly Func<string, string> _moduleName;
        private readonly Func<string, bool> _moduleKnown;
        private readonly Action<string, string> _log;
        private readonly Action<QuestionEntry> _firstShown;
        private readonly Action<bool> _runTimer;
        private readonly Action<Action> _post;

        private readonly List<QuestionEntry> _pending = new List<QuestionEntry>();
        private readonly Dictionary<string, List<DateTimeOffset>> _askTimes =
            new Dictionary<string, List<DateTimeOffset>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<UnansweredQuestion> _unanswered = new List<UnansweredQuestion>();
        private QuestionEntry _visible;
        private int _nextSeq;
        private DateTimeOffset _lastTick;
        private bool _timerRunning;
        private int _overrideSeq = -1;
        private QuestionHold _overrideHold;

        internal TrayQuestionArbiter(Func<DateTimeOffset> utcNow, Func<QuestionHold> hold, IQuestionCardView view,
            Func<string, string> moduleName, Func<string, bool> moduleKnown, Action<string, string> log,
            Action<QuestionEntry> firstShown, Action<bool> runTimer, Action<Action> post)
        {
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _hold = hold ?? (() => QuestionHold.None);
            _view = view;
            _moduleName = moduleName ?? (id => id);
            _moduleKnown = moduleKnown ?? (id => true);
            _log = log ?? delegate { };
            _firstShown = firstShown ?? delegate { };
            _runTimer = runTimer ?? delegate { };
            _post = post ?? (a => a());
        }

        /// <summary>Raised on the UI thread whenever what the tray should say changes (a question queued, shown, ended).</summary>
        internal event Action StateChanged;

        internal int PendingCount { get { return _pending.Count; } }
        internal bool CardShown { get { return _visible != null; } }

        /// <summary>The question the tray's "Answer" row offers: the one on the card, or the one that would be.</summary>
        internal QuestionEntry Current { get { return _visible ?? PickHead(_utcNow()); } }

        internal IReadOnlyList<UnansweredQuestion> Unanswered { get { return _unanswered.ToArray(); } }

        internal void ClearUnanswered()
        {
            _unanswered.Clear();
            RaiseStateChanged();
        }

        /// <summary>Queue an accepted question (TrayQuestionRules.TryAccept made it). UI thread.</summary>
        internal void Ask(QuestionEntry e, CancellationToken withdraw)
        {
            if (e == null) return;
            string id = (e.ModuleId ?? "").Trim();
            e.ModuleId = id;
            if (id.Length == 0 || !SafeKnown(id)) { Refuse(e, "unknown-module"); return; }
            int mine = 0;
            foreach (QuestionEntry p in _pending)
            {
                if (!string.Equals(p.ModuleId, id, StringComparison.OrdinalIgnoreCase)) continue;
                mine++;
                if (string.Equals(p.Key, e.Key, StringComparison.Ordinal)) { Refuse(e, "duplicate-key"); return; }
            }
            if (mine >= MaxPendingPerModule) { Refuse(e, "too-many-pending"); return; }
            if (_pending.Count >= MaxPendingTotal) { Refuse(e, "queue-full"); return; }
            DateTimeOffset now = _utcNow();
            List<DateTimeOffset> times;
            if (!_askTimes.TryGetValue(id, out times)) _askTimes[id] = times = new List<DateTimeOffset>();
            times.RemoveAll(t => now - t >= TimeSpan.FromMinutes(1));
            if (times.Count >= MaxAsksPerMinute) { Refuse(e, "rate-limited"); return; }
            times.Add(now);

            e.Seq = ++_nextSeq;
            e.ModuleName = SafeName(id);
            _log(id, "[question] asked key=" + e.Key + " #" + e.Seq + " choices=" + e.Choices.Count
                + " timeout=" + Seconds(e.Timeout) + "s deadline=" + (e.Deadline.HasValue ? "yes" : "no")
                + " runsWhileHeld=" + (e.RunsWhileHeld ? "true" : "false"));
            if (withdraw.IsCancellationRequested)
            {
                End(e, TrayQuestionOutcome.Withdrawn, null);
                return;
            }
            _pending.Add(e);
            if (withdraw.CanBeCanceled)
            {
                // The callback can run on whichever thread cancels, so it only posts; Withdraw itself runs on the UI
                // thread like everything else here. A withdraw that lands after a click finds the question ended and
                // does nothing: first to the UI thread wins.
                QuestionEntry captured = e;
                e.Registration = withdraw.Register(() => _post(() => Withdraw(captured)), false);
            }
            EnsureTimer(true);
            RaiseStateChanged();
            Reevaluate();
        }

        /// <summary>A question the host would not take, answered at once. Logged by its key and reason only.</summary>
        internal void Refuse(QuestionEntry e, string reason)
        {
            _log(e != null ? e.ModuleId : "", "[question] refused key=" + (e != null && TrayQuestionRules.IsToken(e.Key, TrayQuestionRules.MaxKey) ? e.Key : "(invalid)")
                + " reason=" + reason);
            if (e != null && e.Completion != null)
                e.Completion.TrySetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Refused, Reason = reason });
        }

        internal void Withdraw(QuestionEntry e)
        {
            if (e == null || e.Ended) return;
            End(e, TrayQuestionOutcome.Withdrawn, null);
            Reevaluate();
        }

        /// <summary>A button on the card. Ignored unless it names the question still on the card, a real choice of it,
        /// and arrives after the click guard.</summary>
        internal void Choose(int seq, string choiceId)
        {
            QuestionEntry e = _visible;
            if (e == null || e.Seq != seq || e.Ended) return;
            if (_utcNow() - e.ShownUtc < ClickGuard) return;
            bool known = false;
            foreach (TrayChoice c in e.Choices) if (string.Equals(c.Id, choiceId, StringComparison.Ordinal)) known = true;
            if (!known) return;
            End(e, TrayQuestionOutcome.Chosen, choiceId);
            Reevaluate();
        }

        /// <summary>The tray's "Answer" row: show the current question even through a hold, until it ends or the hold
        /// changes, and give it the keyboard.</summary>
        internal void ShowNow()
        {
            DateTimeOffset now = _utcNow();
            QuestionEntry head = _visible ?? PickHead(now);
            if (head == null) return;
            _overrideSeq = head.Seq;
            _overrideHold = SafeHold();
            Reevaluate();
            if (_visible != null) { try { _view.Activate(); } catch { } }
        }

        /// <summary>The 1 s tick: deadlines, countdowns, then what the card should show.</summary>
        internal void Tick()
        {
            DateTimeOffset now = _utcNow();
            TimeSpan gap = _lastTick == default(DateTimeOffset) ? TimeSpan.FromSeconds(1) : now - _lastTick;
            if (gap < TimeSpan.Zero) gap = TimeSpan.Zero;
            _lastTick = now;
            foreach (QuestionEntry e in _pending.ToArray())
            {
                if (e.Ended) continue;
                if (e.Deadline.HasValue && now >= e.Deadline.Value)
                {
                    End(e, e.Visible ? TrayQuestionOutcome.TimedOut : TrayQuestionOutcome.Expired, e.TimeoutChoiceId);
                    continue;
                }
                bool counting = e.Visible || e.RunsWhileHeld;
                if (!counting) continue;
                TimeSpan charge = gap;
                if (gap > SleepGap && !e.RunsWhileHeld) charge = TimeSpan.FromSeconds(1);
                e.Remaining -= charge;
                if (e.Remaining <= TimeSpan.Zero)
                    End(e, e.Visible ? TrayQuestionOutcome.TimedOut : TrayQuestionOutcome.Expired, e.TimeoutChoiceId);
            }
            Reevaluate();
            if (_visible != null) { try { _view.Update(_visible, WaitingBehind()); } catch { } }
        }

        /// <summary>Decide what the card shows now. Called after every change and on every fullscreen change.</summary>
        internal void Reevaluate()
        {
            DateTimeOffset now = _utcNow();
            QuestionHold hold = SafeHold();
            if (_overrideSeq != -1 && hold != _overrideHold) _overrideSeq = -1;
            QuestionEntry head = PickHead(now);
            if (head == null)
            {
                if (_visible != null) HideCard();
                return;
            }
            bool overridden = _overrideSeq != -1 && (_overrideSeq == head.Seq || (_visible != null && _overrideSeq == _visible.Seq));
            if (hold != QuestionHold.None && !overridden)
            {
                if (_visible != null) HideCard();
                if (head.HeldFor != hold)
                {
                    head.HeldFor = hold;
                    _log(head.ModuleId, "[question] held key=" + head.Key + " #" + head.Seq + " reason=" + HoldWord(hold));
                }
                return;
            }
            if (_visible != null && !_visible.Ended)
            {
                if (ReferenceEquals(_visible, head))
                {
                    // Still the right card; a question may have queued behind it, so its "+N waiting" line moves now
                    // rather than at the next tick.
                    try { _view.Update(_visible, WaitingBehind()); } catch { }
                    return;
                }
                // Only an urgent question takes the card from one that is not; urgent never displaces urgent, and a
                // newer ordinary question waits its turn.
                if (!(head.Urgent && !_visible.Urgent)) return;
                HideCard();
            }
            ShowCard(head, now);
        }

        /// <summary>The app is closing: every question ends Cancelled, and the module's own Shutdown owns its cleanup.</summary>
        internal void CancelAll()
        {
            foreach (QuestionEntry e in _pending.ToArray()) End(e, TrayQuestionOutcome.Cancelled, null);
            HideCard();
            EnsureTimer(false);
        }

        /// <summary>A module's Init threw after asking, or it is being unloaded: its questions end Cancelled.</summary>
        internal void CancelModule(string moduleId)
        {
            string id = (moduleId ?? "").Trim();
            foreach (QuestionEntry e in _pending.ToArray())
                if (string.Equals(e.ModuleId, id, StringComparison.OrdinalIgnoreCase))
                    End(e, TrayQuestionOutcome.Cancelled, null);
            Reevaluate();
        }

        // ---- internals ----

        private QuestionEntry PickHead(DateTimeOffset now)
        {
            QuestionEntry best = null;
            foreach (QuestionEntry e in _pending)
            {
                if (e.Ended) continue;
                if (best == null) { best = e; continue; }
                if (e.Urgent != best.Urgent) { if (e.Urgent) best = e; continue; }
                if (e.Urgent)
                {
                    int byEnd = e.EffectiveEnd(now).CompareTo(best.EffectiveEnd(now));
                    if (byEnd < 0 || (byEnd == 0 && e.Seq < best.Seq)) best = e;
                }
                else if (e.Seq < best.Seq) best = e;
            }
            return best;
        }

        private void ShowCard(QuestionEntry e, DateTimeOffset now)
        {
            if (e.WasShown && !e.RunsWhileHeld)
            {
                TimeSpan floor = e.Timeout < ReshowFloor ? e.Timeout : ReshowFloor;
                if (e.Remaining < floor) e.Remaining = floor;
            }
            bool first = !e.WasShown;
            e.Visible = true;
            e.WasShown = true;
            e.ShownUtc = now;
            e.HeldFor = QuestionHold.None;
            _visible = e;
            int seq = e.Seq;
            try { _view.Show(e, WaitingBehind(), id => Choose(seq, id)); }
            catch (Exception ex) { _log(e.ModuleId, "[question] the card could not be shown: " + ex.GetType().Name); }
            _log(e.ModuleId, "[question] shown key=" + e.Key + " #" + e.Seq);
            if (first) { try { _firstShown(e); } catch { } }
            RaiseStateChanged();
        }

        private void HideCard()
        {
            if (_visible != null) { _visible.Visible = false; _visible = null; }
            try { if (_view != null) _view.Hide(); } catch { }
            RaiseStateChanged();
        }

        private void End(QuestionEntry e, TrayQuestionOutcome outcome, string choiceId)
        {
            if (e == null || e.Ended) return;
            e.Ended = true;
            _pending.Remove(e);
            if (ReferenceEquals(_visible, e)) HideCard();
            e.Visible = false;
            try { e.Registration.Dispose(); } catch { }
            if (outcome == TrayQuestionOutcome.TimedOut || outcome == TrayQuestionOutcome.Expired)
            {
                _unanswered.Insert(0, new UnansweredQuestion
                {
                    EndedLocal = _utcNow().ToLocalTime().DateTime,
                    ModuleName = e.ModuleName,
                    Text = e.Text,
                    AppliedLabel = e.LabelOf(choiceId),
                });
                while (_unanswered.Count > UnansweredKept) _unanswered.RemoveAt(_unanswered.Count - 1);
            }
            if (_overrideSeq == e.Seq) _overrideSeq = -1;
            _log(e.ModuleId, "[question] ended key=" + e.Key + " #" + e.Seq + " outcome=" + outcome
                + " choice=" + (choiceId ?? "-") + " shown=" + (e.WasShown ? "true" : "false")
                + " after=" + Seconds(_utcNow() - e.AskedUtc) + "s");
            if (e.Completion != null)
                e.Completion.TrySetResult(new TrayAnswer { Outcome = outcome, ChoiceId = choiceId, WasShown = e.WasShown });
            if (_pending.Count == 0) EnsureTimer(false);
            RaiseStateChanged();
        }

        private int WaitingBehind()
        {
            int n = 0;
            foreach (QuestionEntry e in _pending) if (!e.Ended && !ReferenceEquals(e, _visible)) n++;
            return n;
        }

        private void EnsureTimer(bool run)
        {
            if (run == _timerRunning) return;
            _timerRunning = run;
            if (run) _lastTick = _utcNow();
            try { _runTimer(run); } catch { }
        }

        private QuestionHold SafeHold()
        {
            try { return _hold(); } catch { return QuestionHold.None; }
        }

        private bool SafeKnown(string id)
        {
            try { return _moduleKnown(id); } catch { return false; }
        }

        private string SafeName(string id)
        {
            try
            {
                string name = _moduleName(id);
                return string.IsNullOrWhiteSpace(name) ? id : name.Trim();
            }
            catch { return id; }
        }

        private void RaiseStateChanged()
        {
            Action handler = StateChanged;
            if (handler == null) return;
            try { handler(); } catch { }
        }

        internal static string HoldWord(QuestionHold hold)
        {
            switch (hold)
            {
                case QuestionHold.Presenting: return "presenting";
                case QuestionHold.Fullscreen: return "fullscreen";
                case QuestionHold.Locked: return "locked";
                case QuestionHold.Quiet: return "quiet";
                default: return "none";
            }
        }

        private static string Seconds(TimeSpan t)
        {
            return ((long)Math.Round(t.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"No answer in 1:42 -> Stop now": the card's countdown line, rendered by the host so a module's text never
        /// carries a time that goes stale on screen.</summary>
        internal static string CountdownText(QuestionEntry e, DateTimeOffset nowUtc)
        {
            if (e == null) return "";
            TimeSpan left = e.Remaining > TimeSpan.Zero ? e.Remaining : TimeSpan.Zero;
            if (e.Deadline.HasValue)
            {
                TimeSpan toDeadline = e.Deadline.Value - nowUtc;
                if (toDeadline < left) left = toDeadline > TimeSpan.Zero ? toDeadline : TimeSpan.Zero;
            }
            int total = (int)Math.Ceiling(left.TotalSeconds);
            string clock = (total / 60).ToString(CultureInfo.InvariantCulture) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
            return "No answer in " + clock + " → " + e.LabelOf(e.TimeoutChoiceId);
        }
    }
}
