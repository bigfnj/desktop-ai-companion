using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.MeetingApps;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// The meeting-end watch's half that lives in the module (lane feature/meeting-end, remembrance 2.4.0): a UI-thread
    /// timer that feeds RecordingWatch every two seconds while recording and OfferWatch every five while idle, puts their
    /// questions to the host's tray card (ITrayQuestions, host 1.6.0) and acts on the answers. The decisions are in
    /// RecordingWatch.cs, where the self-test drives them with no audio; this file is the plumbing and the one place a
    /// question's answer turns into a stop, a split or a new recording.
    ///
    /// READ AT USE, NOT SUBSCRIBED. The meeting comes from ReadContext("meeting.current") on each tick, never from
    /// ContextChanged: PluginApi.cs records the decision of 2026-09-17 that a module reads shared context when it needs
    /// it, and the tick is when this needs it.
    ///
    /// WHAT IS READ, AND WHEN (PRIVACY.md). While recording: each track's loudness, a number a second, kept in memory
    /// only; and unless "When the meeting ends" is Off, Windows' record of when Teams and Zoom last started and stopped
    /// using the microphone, every two seconds. While idle: nothing at all unless "Offer to record meetings" is on and a
    /// calendar meeting is current, and then the same record every five seconds. The log names only "Teams" or "Zoom",
    /// a question's key and a choice's id, never a meeting's name or a question's text.
    /// </summary>
    public sealed partial class RemembranceModule
    {
        internal const string LongestRecordingKey = "longestRecordingHours";
        internal const string OfferToRecordKey = "offerToRecord";
        /// <summary>"Let Reminder's Join &amp; record start a recording" (2.5.0), on by default: the request only ever comes
        /// from a button the user pressed. Off, Remembrance says it does not accept them, so Reminder offers no button.</summary>
        internal const string AcceptJoinRecordKey = "acceptJoinRecord";
        internal const string AcceptJoinRecordLabel = "Let Reminder's Join & record start a recording";
        internal const string MeetingsCard = "Meetings";
        private const int WatchRecordingMs = 2000;
        private const int WatchIdleMs = 5000;

        private System.Windows.Forms.Timer _watchTimer;
        private EventHandler _watchHandler;
        private ITrayQuestions _questions;
        private MeetingAppMicrophone _mic;

        /// <summary>Test seam: the microphone-use reader Init uses instead of the registry's (one over a
        /// FakeConsentStore), set before Init. Null in the shipped module.</summary>
        internal MeetingAppMicrophone MicForDiagnostics;

        /// <summary>The watch's clock. The self-test stands it in to reach three hours, or a release fifteen seconds
        /// old, without waiting. Restored by the test that swaps it.</summary>
        internal static Func<DateTimeOffset> WatchClock = () => DateTimeOffset.UtcNow;

        /// <summary>How the Status line reads OneDrive's variables; the self-test stands it in.</summary>
        internal static Func<string, string> EnvironmentLookup = Environment.GetEnvironmentVariable;

        private RecordingWatch _watch;
        private readonly Dictionary<string, int> _meterNext = new Dictionary<string, int>(StringComparer.Ordinal);
        // Moves on at every start and every stop, so an answer that comes back for an earlier recording -- the user's
        // click racing the withdraw a stop made -- is recognised as stale and ignored.
        private int _generation;
        private CancellationTokenSource _questionCts;
        private int _questionSerial;
        private readonly OfferWatch _offer = new OfferWatch();
        private CancellationTokenSource _offerCts;
        private int _offerSerial;
        private bool _watchFaultLogged;
        private bool _noQuestionsLogged;

        // A track that stopped saving, logged once each and kept for the Status line until the next recording starts.
        private readonly object _trackFailureLock = new object();
        private readonly HashSet<string> _trackFailuresLogged = new HashSet<string>(StringComparer.Ordinal);
        private volatile string _trackFailureLine;

        // ---- lifetime ----

        /// <summary>Called from Init. The timer is created and disposed like the purge timer, and never ticks in a
        /// process with no message loop (the headless self-test drives WatchTickForSelfTest instead).</summary>
        private void InitWatch(IHost host)
        {
            _questions = host as ITrayQuestions;
            _mic = MicForDiagnostics ?? new MeetingAppMicrophone();
            _watchTimer = new System.Windows.Forms.Timer { Interval = WatchIdleMs };
            _watchHandler = delegate { OnWatchTick(); };
            _watchTimer.Tick += _watchHandler;
            _watchTimer.Start();
            // A request already in the channel when this instance starts (a reload, an update) belongs to an earlier one:
            // it is noted as seen, not acted on, so a reload cannot start a recording nobody just asked for.
            string id, meeting; DateTimeOffset at;
            if (RecordingHandshake.TryParseRequest(SafeRead(host, RecordingHandshake.RequestKey), out id, out at, out meeting))
                _lastRecordRequestId = id;
            PublishRecorderReady(false);
        }

        /// <summary>Called from Shutdown: every open question is withdrawn and the timer goes.</summary>
        private void ShutdownWatch()
        {
            CancelQuestion();
            CancelOffer();
            // Gone: Reminder must stop offering Join & record the moment there is nobody to take it.
            try { if (_host != null) _host.PublishContext(Id, RecordingHandshake.ReadyKey, ""); } catch { }
            _watch = null;
            if (_watchTimer != null)
            {
                try { _watchTimer.Stop(); if (_watchHandler != null) _watchTimer.Tick -= _watchHandler; _watchTimer.Dispose(); }
                catch { }
                _watchTimer = null;
                _watchHandler = null;
            }
        }

        private void SetWatchInterval(int ms)
        {
            System.Windows.Forms.Timer timer = _watchTimer;
            try { if (timer != null && timer.Interval != ms) timer.Interval = ms; } catch { }
        }

        /// <summary>A recording has started (StartRecording, on the UI thread): a fresh watch over its tracks, anchored
        /// on the meeting it started in.</summary>
        private void BeginWatch(MeetingContext meeting)
        {
            _generation++;
            CancelQuestion();
            _offer.NoteRecordingStarted();
            CancelOffer();
            _watch = new RecordingWatch(WatchClock(), meeting);
            _meterNext.Clear();
            lock (_trackFailureLock) { _trackFailuresLogged.Clear(); _trackFailureLine = null; }
            AudioRecorder recorder = _recorder;
            if (recorder != null)
            {
                foreach (AudioRecorder.TrackLevels t in recorder.Tracks) _watch.AddTrack(t.Name, t.Loopback, t.Measured);
                foreach (string line in recorder.ConverterFallbacks)
                    Log("a track is written in its device's format and converted when the recording stops, not as it arrives (" + line + ")");
            }
            SetWatchInterval(WatchRecordingMs);
            PublishRecorderReady(true);
        }

        /// <summary>A recording is stopping (every stop path): its question is withdrawn and its watch dropped.</summary>
        private void EndWatch()
        {
            _generation++;
            CancelQuestion();
            _watch = null;
            SetWatchInterval(WatchIdleMs);
            PublishRecorderReady(false);
        }

        // ---- Join & record (2.5.0): Reminder's third button, over the shared context ----

        /// <summary>The id of the last record request seen, acted on or not, so each is considered once.</summary>
        private string _lastRecordRequestId = "";

        /// <summary>Tell Reminder whether a Join &amp; record press can start a recording now: published at Init, at every start
        /// and stop, and when the pane is applied; "" at Shutdown. UI thread, like every host call here.</summary>
        private void PublishRecorderReady(bool recording)
        {
            IHost host = _host;
            if (host == null || _settings == null) return;
            try
            {
                host.PublishContext(Id, RecordingHandshake.ReadyKey,
                    RecordingHandshake.ReadyJson(_settings.GetBool(AcceptJoinRecordKey, true), recording));
            }
            catch { }
        }

        private static string SafeRead(IHost host, string key)
        {
            if (host == null) return "";
            try { return host.ReadContext(key) ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// On the idle tick: a Join &amp; record request Reminder published since the last one this instance saw starts a
        /// recording named for its meeting. Read at use, like meeting.current, so the start comes within one idle tick
        /// (5 s) of the press. Ignored, with a line in the log that names no meeting, when the setting is off or the
        /// request is older than <see cref="RecordingHandshake.RequestFreshFor"/>; never considered while recording.
        /// </summary>
        private void HandleRecordRequest(DateTimeOffset now)
        {
            if (_recording) return;
            string json = SafeRead(_host, RecordingHandshake.RequestKey);
            string id, meetingJson; DateTimeOffset at;
            if (!RecordingHandshake.TryParseRequest(json, out id, out at, out meetingJson)) return;
            if (string.Equals(id, _lastRecordRequestId, StringComparison.Ordinal)) return;
            _lastRecordRequestId = id;
            if (!_settings.GetBool(AcceptJoinRecordKey, true))
            {
                Log("join & record: a request from Reminder was ignored because \"" + AcceptJoinRecordLabel + "\" is off");
                return;
            }
            TimeSpan age = now - at;
            if (age > RecordingHandshake.RequestFreshFor || age < -RecordingHandshake.RequestFreshFor)
            {
                Log("join & record: a request from Reminder was ignored because it was "
                    + ((long)age.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s old");
                return;
            }
            Log("join & record: Reminder's Join & record asked for a recording; starting one");
            MeetingContext meeting = MeetingContext.Parse(meetingJson);
            // An empty meeting names nothing; the meeting current now is the better name then.
            StartRecording(meeting.IsEmpty ? null : meeting);
        }

        internal void PublishRecorderReadyForSelfTest() { PublishRecorderReady(_recording); }

        // ---- the tick ----

        private void OnWatchTick()
        {
            if (_host == null) return;
            DateTimeOffset now = WatchClock();
            try
            {
                if (_recording && _recorder != null && _watch != null) TickRecording(now);
                else TickIdle(now);
            }
            catch (Exception ex)
            {
                // Once per session: a fault here every two seconds would bury the log. The type only.
                if (!_watchFaultLogged) { _watchFaultLogged = true; Log("meeting watch: a tick failed (" + ex.GetType().Name + ")"); }
            }
            SetWatchInterval(_recording ? WatchRecordingMs : WatchIdleMs);
        }

        /// <summary>One tick, driven by the self-test; the timer's own tick in the app.</summary>
        internal void WatchTickForSelfTest() { OnWatchTick(); }
        internal WatchRule OpenQuestionForSelfTest { get { RecordingWatch w = _watch; return w != null ? w.Open : WatchRule.None; } }
        internal string TrackFailureLineForSelfTest { get { return _trackFailureLine; } }
        internal string StatusLineForSelfTest() { return StatusLine(); }
        internal int WatchIntervalForSelfTest { get { return _watchTimer != null ? _watchTimer.Interval : 0; } }

        private string ReadMeetingJson()
        {
            IHost host = _host;
            if (host == null) return "";
            try { return host.ReadContext(MeetingContext.Key) ?? ""; } catch { return ""; }
        }

        private void TickRecording(DateTimeOffset now)
        {
            AudioRecorder recorder = _recorder;
            RecordingWatch watch = _watch;
            NoteTrackFailures(recorder);
            foreach (AudioRecorder.TrackLevels t in recorder.Tracks)
            {
                int next;
                _meterNext.TryGetValue(t.Name, out next);
                List<LevelSecond> seconds = t.Meter.CopySince(next);
                if (seconds.Count == 0) continue;
                watch.Feed(t.Name, seconds);
                _meterNext[t.Name] = seconds[seconds.Count - 1].Index + 1;
            }
            string mode = CurrentMeetingEndMode();
            MeetingContext current = MeetingContext.Parse(ReadMeetingJson());
            // Off asks nothing that needs the microphone record, so it is not read at all.
            MicUseSnapshot mic = mode == MeetingEndMode.Off ? null : _mic.Read();
            WatchStep step = watch.Tick(now, mode, LongestRecordingHours(), mic, current);
            if (step.WithdrawSerial != 0 && step.WithdrawSerial == _questionSerial) CancelQuestion();
            if (step.Ask != null) PutQuestion(step.Ask);
            Backstop(now);
        }

        private void TickIdle(DateTimeOffset now)
        {
            // Join & record first (2.5.0): a press the user just made outranks an offer the module might make.
            HandleRecordRequest(now);
            if (_recording) return;
            bool enabled = _settings.GetBool(OfferToRecordKey, false);
            if (!enabled && _offerSerial == 0) return;
            MeetingContext current = MeetingContext.Parse(ReadMeetingJson());
            // No calendar meeting: nothing to offer, and the microphone record is not read.
            if (current.IsEmpty && _offerSerial == 0) return;
            MicUseSnapshot mic = _mic.Read();
            WatchStep step = _offer.Tick(now, enabled, _recording, current, mic);
            if (step.WithdrawSerial != 0 && step.WithdrawSerial == _offerSerial) CancelOffer();
            if (step.Ask != null) PutOffer(step.Ask);
        }

        /// <summary>
        /// R1 and R3 must not hang on a host that never answers: the recording they would stop runs on, which is the very
        /// thing this lane exists to prevent. Each runs while the card is held, so the host answers within its Timeout;
        /// a minute past that, the module withdraws it and applies the no-answer choice itself, logged as such.
        /// </summary>
        private void Backstop(DateTimeOffset now)
        {
            RecordingWatch watch = _watch;
            if (watch == null) return;
            WatchRule open = watch.Open;
            if (open != WatchRule.MeetingOver && open != WatchRule.Limit) return;
            DateTimeOffset askedAt;
            TimeSpan timeout;
            if (!watch.OpenDeadline(out askedAt, out timeout) || now - askedAt < timeout + WatchTuning.Backstop) return;
            int serial = watch.OpenSerial;
            string choice = watch.OpenTimeoutChoice;
            CancelQuestion();
            Log("meeting watch: no answer to " + WatchQuestions.KeyOf(open) + " came back from the host; applying its no-answer choice (" + choice + ")");
            Act(watch.Answer(serial, TrayQuestionOutcome.TimedOut, choice, now));
        }

        // ---- asking ----

        private void PutQuestion(WatchAsk ask)
        {
            CancelQuestion();
            ITrayQuestions questions = _questions;
            if (questions == null)
            {
                // Only a host older than 1.6.0 lacks it, and MinHostVersion keeps this module off one; a test double
                // that does not implement it lands here. The question counts as refused, which holds it back a minute.
                if (!_noQuestionsLogged) { _noQuestionsLogged = true; Log("meeting watch: this host cannot ask questions"); }
                RecordingWatch w = _watch;
                if (w != null) w.Answer(ask.Serial, TrayQuestionOutcome.Refused, null, WatchClock());
                return;
            }
            var cts = new CancellationTokenSource();
            _questionCts = cts;
            _questionSerial = ask.Serial;
            int generation = _generation;
            // The key and what set it off, for tuning the thresholds on real meetings; never the text.
            Log("meeting watch: asked " + ask.Question.Key + (string.IsNullOrEmpty(ask.Note) ? "" : " (" + ask.Note + ")"));
            Ask(questions, ask.Question, cts.Token, delegate(TrayAnswer answer) { OnRecordingAnswer(generation, ask, answer); });
        }

        private void PutOffer(WatchAsk ask)
        {
            CancelOffer();
            ITrayQuestions questions = _questions;
            if (questions == null) { _offer.Answer(ask.Serial, TrayQuestionOutcome.Refused, null, WatchClock()); return; }
            var cts = new CancellationTokenSource();
            _offerCts = cts;
            _offerSerial = ask.Serial;
            Log("meeting watch: asked " + ask.Question.Key);
            Ask(questions, ask.Question, cts.Token, delegate(TrayAnswer answer) { OnOfferAnswer(ask, answer); });
        }

        /// <summary>
        /// Put a question and route its answer back to the UI thread. The host's Task never faults and completes on its
        /// UI thread with continuations run asynchronously (PluginApi.cs); the continuation still posts through this
        /// module's own context, so the answer is handled where every other recording decision is, and a self-test's
        /// queueing context sees it when it drains.
        /// </summary>
        private void Ask(ITrayQuestions questions, TrayQuestion question, CancellationToken withdraw, Action<TrayAnswer> onAnswer)
        {
            Task<TrayAnswer> task;
            try { task = questions.AskAsync(Id, question, withdraw); }
            catch (Exception ex) { task = Task.FromResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Refused, Reason = ex.GetType().Name }); }
            if (task == null) task = Task.FromResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Refused, Reason = "no-task" });
            task.ContinueWith(delegate(Task<TrayAnswer> done)
            {
                TrayAnswer answer = done.Status == TaskStatus.RanToCompletion && done.Result != null
                    ? done.Result : new TrayAnswer { Outcome = TrayQuestionOutcome.Cancelled };
                PostToUi(delegate { onAnswer(answer); });
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private void PostToUi(Action action)
        {
            SendOrPostCallback run = delegate
            {
                try { action(); }
                catch (Exception ex) { Log("meeting watch: handling an answer failed (" + ex.GetType().Name + ")"); }
            };
            SynchronizationContext ui = _ui;
            try
            {
                if (ui != null) ui.Post(run, null);
                else run(null);
            }
            catch { }
        }

        private void CancelQuestion()
        {
            CancellationTokenSource cts = _questionCts;
            _questionCts = null;
            _questionSerial = 0;
            if (cts != null) { try { cts.Cancel(); } catch { } }
        }

        private void CancelOffer()
        {
            CancellationTokenSource cts = _offerCts;
            _offerCts = null;
            _offerSerial = 0;
            if (cts != null) { try { cts.Cancel(); } catch { } }
        }

        // ---- answers ----

        private void OnRecordingAnswer(int generation, WatchAsk ask, TrayAnswer answer)
        {
            if (_host == null) return;
            RecordingWatch watch = _watch;
            if (generation != _generation || watch == null || !_recording)
            {
                // The recording it was about has ended, or another has started: whatever it says is not about this one.
                if (answer.Outcome == TrayQuestionOutcome.Chosen)
                    Log("meeting watch: an answer to " + ask.Question.Key + " came back after its recording ended; ignored");
                return;
            }
            if (ask.Serial == _questionSerial) { _questionCts = null; _questionSerial = 0; }
            if (answer.Outcome == TrayQuestionOutcome.Refused)
                Log("meeting watch: the host refused " + ask.Question.Key + " (" + (answer.Reason ?? "") + ")");
            Act(watch.Answer(ask.Serial, answer.Outcome, answer.ChoiceId, WatchClock()));
        }

        private void Act(WatchAction action)
        {
            if (action == null || action.Kind == WatchActionKind.None || !_recording) return;
            if (action.Kind == WatchActionKind.Stop)
            {
                StopRecording(false, action.KeepUntil, action.Reason);
                return;
            }
            if (action.Kind == WatchActionKind.Split)
            {
                // Two recordings: this one saved whole, as it is, and a new one named for the meeting that started.
                StopRecording(false, null, action.Reason);
                StartRecording(action.Meeting);
            }
        }

        private void OnOfferAnswer(WatchAsk ask, TrayAnswer answer)
        {
            if (_host == null) return;
            if (ask.Serial == _offerSerial) { _offerCts = null; _offerSerial = 0; }
            if (answer.Outcome == TrayQuestionOutcome.Refused)
                Log("meeting watch: the host refused " + ask.Question.Key + " (" + (answer.Reason ?? "") + ")");
            MeetingContext meeting = _offer.Answer(ask.Serial, answer.Outcome, answer.ChoiceId, WatchClock());
            if (meeting == null || _recording) return;
            Log("meeting watch: " + ask.Question.Key + " was answered Record; starting a recording");
            StartRecording(meeting);
        }

        // ---- what the watch reads from the settings ----

        private string CurrentMeetingEndMode()
        {
            return MeetingEndMode.Read(_settings.Get(MeetingEndMode.SettingKey, MeetingEndMode.Default));
        }

        /// <summary>"Longest recording" in whole hours, 1 to 8, 3 when unset or unreadable.</summary>
        private int LongestRecordingHours()
        {
            int hours;
            try { hours = _settings.GetInt(LongestRecordingKey, WatchTuning.LimitHoursDefault); }
            catch { hours = WatchTuning.LimitHoursDefault; }
            return Math.Max(WatchTuning.LimitHoursMin, Math.Min(WatchTuning.LimitHoursMax, hours));
        }

        /// <summary>The Meetings card's "Meeting apps" row: whether Windows' record can be read here, or why not.</summary>
        private string MeetingAppsLine()
        {
            MicUseSnapshot snap;
            try { snap = (_mic ?? new MeetingAppMicrophone()).Read(); }
            catch { snap = null; }
            if (snap != null && snap.Available)
                return "Teams and Zoom, from the start and stop times Windows keeps of their microphone use.";
            string why = snap != null && !string.IsNullOrEmpty(snap.Unavailable) ? snap.Unavailable : "the record could not be read";
            return "Not available: " + why + ". The meeting-over question then waits for silence alone.";
        }

        // ---- a track that stopped saving ----

        /// <summary>Log each track that stopped saving, once, and keep the newest for the Status line. Called by the tick
        /// while recording and by the save after the stop, which runs on a pool thread, hence the lock.</summary>
        private void NoteTrackFailures(AudioRecorder recorder)
        {
            if (recorder == null) return;
            foreach (TrackWriteFailure f in recorder.WriteFailures)
            {
                string line = "the " + f.Track + " track stopped saving " + AudioRecorder.Duration(f.At) + " in (" + f.ExceptionType +
                              "); the recording keeps what it saved before that";
                lock (_trackFailureLock)
                {
                    if (!_trackFailuresLogged.Add(f.Track)) continue;
                    _trackFailureLine = line;
                }
                Log(line);
            }
        }

        /// <summary>The Status line's warnings this lane adds: the storage folder inside OneDrive, and a track that stopped
        /// saving. "" when neither applies.</summary>
        private string MeetingStatusWarnings(string root)
        {
            string s = "";
            if (CaptureStore.OneDriveRootOf(root, EnvironmentLookup) != null)
                s += "  |  ⚠ The storage folder is inside OneDrive: recordings upload to the cloud, and audio deleted here " +
                     "stays in the OneDrive recycle bin. Choose a folder outside OneDrive to keep them on this computer.";
            string failure = _trackFailureLine;
            if (!string.IsNullOrEmpty(failure)) s += "  |  ⚠ " + char.ToUpperInvariant(failure[0]) + failure.Substring(1) + ".";
            return s;
        }
    }
}
