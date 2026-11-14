using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The closure log lines a parity run writes to its diagnostic log, never to the trace (signed revision H :80, :150,
    /// :158, :232): the engine this process actually loaded, the synthetic label on a run whose build witness its caller
    /// supplied, the policy-blind birth witness and the population reconciliation witness. Every line ends with
    /// "runId &lt;id&gt;", the runId the same invocation's trace header carries, so a closure record can bind a line to its
    /// trace. The loaded-engine and birth-witness lines are written on every run, traced and trace-off alike (Codex's
    /// closure rulings, 2026-11-13); the loaded-engine line also on a run the core run refuses after its entry, whose
    /// runId then has no header (Q-c). The spellings live in this one block so that a spelling agreed with the closure
    /// checker's owner (the lane brief's open question 10) is a change here and nowhere else.
    /// </summary>
    internal static class ParityRunLog
    {
        // ---- closure line spellings (one place; each line ends RunIdField + the run's id)
        public const string LoadedEngine = "parity loaded-engine";
        public const string SyntheticWitness = "parity build-witness synthetic";
        public const string BirthWitness = "parity birth-witness";
        public const string Reconciliation = "parity reconciliation";
        public const string BuildWitnessUnreadable = "build-witness-unreadable";
        public const string RegistryIntegrity = "parity registry-integrity";
        public const string InputAdmitted = "parity INPUT-ADMITTED";
        public const string RunIdField = " runId ";
        // ---- end of the spellings

        /// <summary>The registry drift self-test passed (C7, H :230-232): "parity registry-integrity ok registrySha256 D
        /// engineSha256 H enginePath P runId R", naming the digest the check computed on the embedded registry and the
        /// engine assembly that holds the compiled table. Adopted unchanged by Codex's ruling (2026-11-13, ef41a984): a
        /// diagnostic line only, never a substitute for INPUT-ADMITTED or reconciliation.</summary>
        public static string RegistryIntegrityOkLine(string registrySha256, ParityLoadedEngine engine, string runId)
        {
            return RegistryIntegrity + " ok registrySha256 " + registrySha256 + " engineSha256 " + engine.EngineSha256
                + " enginePath " + engine.EnginePath + RunIdField + runId;
        }

        /// <summary>C8 (H steps 4-6, :232): a candidate admitted by its full scenario SHA-256 whose pack is the registered
        /// one: "parity INPUT-ADMITTED registrySha256 D id I case C scenarioSha256 S packSha256 P runId R", in the field
        /// order the lane brief fixed (registry digest, id, case, scenarioSha256, packSha256, runId). D is the digest this
        /// process's drift self-test computed; S and P are the digests this run computed on the bytes it read and the pack it
        /// hashed, equal to the row's by admission. Logged after the pack comparison and before the pets are loaded. Codex
        /// agreed the spelling and its order unchanged (review of 2026-11-14, 774b308c) as an admission proof only: not
        /// whole-run closure, outside the trace JSONL and the comparer's JSON, and its adoption by the oracle and the
        /// comparer is prospective.</summary>
        public static string InputAdmittedLine(string registrySha256, ParityPolicyRow row, string scenarioSha256, string packSha256, string runId)
        {
            return InputAdmitted + " registrySha256 " + registrySha256 + " id " + row.Id + " case " + row.Case + " scenarioSha256 " + scenarioSha256
                + " packSha256 " + packSha256 + RunIdField + runId;
        }

        /// <summary>The registry drift self-test failed: a named internal defect, exit 2 and no trace; also an admitted
        /// scenario that does not interpret (H step 5). Adopted unchanged by Codex's ruling (2026-11-13, ef41a984); never
        /// positive evidence.</summary>
        public static string RegistryIntegrityDefectLine(string defect, string runId)
        {
            return RegistryIntegrity + " internal-defect: " + defect + "; a candidate scenario is admitted only through a verified registry"
                + RunIdField + runId;
        }

        /// <summary>"parity loaded-engine engineSha256 H exeSha256 H enginePath P exePath P runId R": the files this process
        /// loaded and hashed itself, never the header's build values, logged on every run at the core run's entry. The
        /// hashes come first and the paths last, since a path may hold spaces.</summary>
        public static string LoadedEngineLine(ParityLoadedEngine engine, string runId)
        {
            return LoadedEngine + " engineSha256 " + engine.EngineSha256 + " exeSha256 " + engine.ExeSha256
                + " enginePath " + engine.EnginePath + " exePath " + engine.ExePath + RunIdField + runId;
        }

        /// <summary>The label a run below the command carries: its caller handed it the build values the header holds, so
        /// they name no file this process read, and the run is never closure evidence (H :80, :187).</summary>
        public static string SyntheticWitnessLine(string runId)
        {
            return SyntheticWitness + ": the header's build values were supplied by the caller, not read by this process,"
                + " so this run is not closure evidence" + RunIdField + runId;
        }

        /// <summary>The refusal when this process cannot read its own EXE or engine (H :80): a null or empty path, or a
        /// file that cannot be opened. Exit 2 and no trace.</summary>
        public static string BuildWitnessUnreadableLine(string what, string path, Exception fault)
        {
            string detail = path == null ? "null" : path.Length == 0 ? "empty" : "'" + path + "', which cannot be read: " + fault.Message;
            return BuildWitnessUnreadable + ": " + what + " path is " + detail
                + "; a parity run names the EXE and engine this process loaded, so it writes no trace without them";
        }

        /// <summary>
        /// The birth witness (policy:birth-cap, H :158), logged on every run that starts, traced or trace-off, whatever its scenario declares: the limit the
        /// world ended with, its write record since construction, and every decision Breed's loop made, each with its
        /// tick, parent and attempt and the operands and result of the comparison itself. "limitWrites 0 none" is the
        /// direct-world, no-override evidence; any recorded write is listed with its writer, base and deduction.
        /// </summary>
        public static string BirthWitnessLine(IList<TickBirthDecision> decisions, TickLimitWrites writes, int limit, string runId)
        {
            var b = new StringBuilder(BirthWitness);
            b.Append(" limit ").Append(limit.ToString(CultureInfo.InvariantCulture));
            b.Append(" limitWrites ").Append(writes.Count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(LimitWrites(writes));
            b.Append(" decisions ").Append(decisions.Count.ToString(CultureInfo.InvariantCulture)).Append(' ');
            if (decisions.Count == 0) b.Append("none");
            else
            {
                b.Append('[');
                for (int i = 0; i < decisions.Count; i++)
                {
                    if (i > 0) b.Append(", ");
                    b.Append(Decision(decisions[i]));
                }
                b.Append(']');
            }
            return b.Append(RunIdField).Append(runId).ToString();
        }

        /// <summary>"none", or every run of the record in order: "[16 by writer x1 from write 0 | ...]".</summary>
        private static string LimitWrites(TickLimitWrites writes)
        {
            if (writes.Runs.Count == 0) return "none";
            var b = new StringBuilder("[");
            for (int i = 0; i < writes.Runs.Count; i++)
            {
                TickLimitWrite run = writes.Runs[i];
                if (i > 0) b.Append(" | ");
                b.Append(run.Value.ToString(CultureInfo.InvariantCulture)).Append(" by ").Append(run.Writer)
                    .Append(" x").Append(run.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" from write ").Append(run.FirstWrite.ToString(CultureInfo.InvariantCulture));
            }
            return b.Append(']').ToString();
        }

        /// <summary>"t6 m1 #16 16 vs 1+15 refuse": tick, parent, attempt, then limit vs active+pending and the result, all
        /// as the comparison held them.</summary>
        public static string Decision(TickBirthDecision d)
        {
            return "t" + d.Tick.ToString(CultureInfo.InvariantCulture) + " " + d.ParentSid + " #" + d.Attempt.ToString(CultureInfo.InvariantCulture)
                + " " + d.Room.Limit.ToString(CultureInfo.InvariantCulture) + " vs " + d.Room.Active.ToString(CultureInfo.InvariantCulture)
                + "+" + d.Room.Pending.ToString(CultureInfo.InvariantCulture) + (d.Room.Admits ? " admit" : " refuse");
        }

        /// <summary>The positive reconciliation witness (H :136, :150): the proofs this run executed, as a list (A14's
        /// window-effects, A15's active-pending-membership), how many checkpoints passed, N+1 on a complete run, and which
        /// state records were compared: a traced run's at every checkpoint, a trace-off run's final ones at N. Written only
        /// by the run's own ledger when every checkpoint passed and, trace-off, its final states were compared. The list
        /// form is the one Codex agreed exactly (closure spellings Q-d, 2026-11-13).</summary>
        public static string ReconciliationLine(int checkpoints, bool traceOff, string runId)
        {
            return Reconciliation + " window-effects, active-pending-membership checkpoints " + checkpoints.ToString(CultureInfo.InvariantCulture)
                + " stateRecords " + (traceOff ? "final" : "every-checkpoint") + RunIdField + runId;
        }

        /// <summary>A trace-off run whose N+1 checkpoints all ran but whose final state records were never compared (the
        /// final-state write failed): never the witness, which names a comparison that did not happen. The form Codex
        /// agreed (Q4): the checkpoints that ran, then the scenario's N+1, each its own value, so the line binds both to the
        /// scenario rather than to each other; the ledger writes it only when they are equal.</summary>
        public static string ReconciliationFinalStatesMissingLine(int ran, int expected, string runId)
        {
            return Reconciliation + " incomplete: checkpoints " + ran.ToString(CultureInfo.InvariantCulture) + " of "
                + expected.ToString(CultureInfo.InvariantCulture) + ", final state records not compared" + RunIdField + runId;
        }

        /// <summary>The first failing checkpoint, logged when it fails; the run goes on to N with status error.</summary>
        public static string ReconciliationFailedLine(string failure, string runId)
        {
            return Reconciliation + " failed at " + failure + RunIdField + runId;
        }

        /// <summary>A run that stopped before N (aborted or failed): how many of its N+1 checkpoints ran. Never a witness.</summary>
        public static string ReconciliationIncompleteLine(int ran, int expected, string runId)
        {
            return Reconciliation + " incomplete: checkpoints " + ran.ToString(CultureInfo.InvariantCulture) + " of "
                + expected.ToString(CultureInfo.InvariantCulture) + RunIdField + runId;
        }
    }

    /// <summary>
    /// Where this process's own EXE and engine assembly are: <see cref="Process"/>, Environment.ProcessPath and the engine
    /// assembly's Location. The window self-test hands ParityRun a provider whose Location is empty or whose process path
    /// is null, which no real process can be made to show, to see the build-witness-unreadable refusal. Internal and never
    /// read from an argument or a setting, so no caller of the command can point the witness at another file.
    /// </summary>
    internal sealed class ParityEnginePaths
    {
        public static readonly ParityEnginePaths Process = new ParityEnginePaths(() => Environment.ProcessPath, () => typeof(TickWorld).Assembly.Location);

        public readonly Func<string> Exe;
        public readonly Func<string> Engine;

        internal ParityEnginePaths(Func<string> exe, Func<string> engine)
        {
            Exe = exe;
            Engine = engine;
        }
    }

    /// <summary>
    /// The EXE and engine this process loaded, each hashed by this process (signed revision H :78-80). The command builds
    /// its header's exeSha256 and engineSha256 from it, and ParityRun.Run logs it on every run, the self-test's included,
    /// beside whatever build values its caller supplied.
    /// </summary>
    internal sealed class ParityLoadedEngine
    {
        public readonly string ExePath;
        public readonly string ExeSha256;
        public readonly string EnginePath;
        public readonly string EngineSha256;

        private ParityLoadedEngine(string exePath, string exeSha256, string enginePath, string engineSha256)
        {
            ExePath = exePath;
            ExeSha256 = exeSha256;
            EnginePath = enginePath;
            EngineSha256 = engineSha256;
        }

        /// <summary>Both files read and hashed, or null with the build-witness-unreadable line in <paramref name="refusal"/>.</summary>
        internal static ParityLoadedEngine Read(ParityEnginePaths paths, out string refusal)
        {
            string exePath = paths.Exe();
            string exeSha256 = SelfHash("the process EXE", exePath, out refusal);
            if (exeSha256 == null) return null;
            string enginePath = paths.Engine();
            string engineSha256 = SelfHash("the engine assembly", enginePath, out refusal);
            if (engineSha256 == null) return null;
            return new ParityLoadedEngine(exePath, exeSha256, enginePath, engineSha256);
        }

        /// <summary>
        /// One self-hash read. The read is guarded rather than the path pre-checked: a null path (no ProcessPath, as under
        /// some hosts), an empty one (an assembly loaded from bytes has Location ""), a missing or locked file all throw
        /// an argument fault, and each is the same producer refusal, never an unhandled exception out of the command. A
        /// pre-check would cover only the paths someone thought of.
        /// </summary>
        private static string SelfHash(string what, string path, out string refusal)
        {
            try
            {
                refusal = null;
                return ParityRun.Sha256(File.ReadAllBytes(path));
            }
            catch (Exception e) when (ParityRun.IsArgumentFault(e) || e is NotSupportedException)
            {
                refusal = ParityRunLog.BuildWitnessUnreadableLine(what, path, e);
                return null;
            }
        }
    }

    /// <summary>
    /// The scripted desktop window as it is at a checkpoint (ParityEnvironment.DesktopWindow): TickDesktopWindow's own
    /// shown, minimised and rectangle values and its per-kind success counters, never the active-window area the
    /// companions read, which is refreshed only at the next tick.
    /// </summary>
    internal struct ParityWindowState
    {
        public readonly bool Shown;
        public readonly bool Minimised;
        public readonly TickRect Rect;
        public readonly long Moves;
        public readonly long Minimises;

        public ParityWindowState(bool shown, bool minimised, TickRect rect, long moves, long minimises)
        {
            Shown = shown;
            Minimised = minimised;
            Rect = rect;
            Moves = moves;
            Minimises = minimises;
        }

        public override string ToString()
        {
            return (Shown ? "shown" : "hidden") + (Minimised ? " minimised" : "") + " [" + Rect.X + "," + Rect.Y + ","
                + (Rect.X + Rect.Width) + "," + (Rect.Y + Rect.Height) + "] moves " + Moves + " minimises " + Minimises;
        }
    }

    /// <summary>
    /// A15, population reconciliation (signed revision H :136, :142-150): ParityRun.Run owns one per run, traced and
    /// trace-off alike, and checks it at every completed checkpoint 0..N. Two sides, kept independent:
    /// <list type="bullet">
    /// <item>the recorded side seeds the run's validated scenario rows (never the world's lists or a count of successful
    /// starts) and replays the lifecycle records the trace writer COMMITTED to its event sink, the exact lines, whether or
    /// not the sink writes them out (ParityTraceWriter.Committed);</item>
    /// <item>the actual side is TickWorld.SnapshotMembership, a copy of the private active and pending lists, which the
    /// writer never reads, compared as a (sid, pending) multiset so a duplicate shows.</item>
    /// </list>
    /// A traced run also compares the state records committed at each checkpoint; a trace-off run forms none until its
    /// final states, which are compared at N. The first failure is logged with its checkpoint and the run goes on to N
    /// with status error (H :136, :150); only a run whose N+1 checkpoints all passed writes the positive witness.
    /// It reads events by kind and sid only, and spells no error code: the bd-parity-host-2 invariant scans this file.
    ///
    /// A14, window reconciliation (H :136), at the same checkpoints: the recorded side replays the scenario's activeWindow
    /// timeline (a NEW entry replaces the window whole, as ParityEnvironment.Tick does; hiding keeps the rectangle) and
    /// the window events committed since the last checkpoint, in commit order: a window-move's "from" must be the replayed
    /// rectangle of a shown, unminimised window, which then takes its "to"; a window-minimize needs the same window and
    /// minimises it. The actual side is ParityEnvironment.DesktopWindow, the live TickDesktopWindow and its per-kind
    /// success counters. Both the state and each counter must agree; counters compared at every checkpoint are the
    /// per-tick counts compared tick by tick. The timeline entry for tick k takes effect before any event of tick k
    /// (TickWorld.StepOnce ticks the environment first), so the events are replayed at the checkpoint, after it.
    /// </summary>
    internal sealed class ParityRunLedger
    {
        private sealed class Member
        {
            public bool Pending;
            public bool DisposeRequested;
        }

        /// <summary>One activeWindow entry as the scenario states it, kept whole: its rectangle is read only when the entry
        /// takes effect (<see cref="ReplayWindow"/>), as ParityEnvironment.Tick reads it.</summary>
        private struct WindowEntry
        {
            public int Tick;
            public JsonElement Entry;
        }

        private readonly Dictionary<string, Member> _members = new Dictionary<string, Member>(StringComparer.Ordinal);
        private readonly List<string> _states = new List<string>();
        private readonly bool _traceOff;
        private readonly TextWriter _log;
        private readonly string _runId;
        private string _inconsistency;
        private bool _finalStatesCompared;

        // A14's recorded side: the timeline, the entry in force, the replayed window and the window events of this tick.
        private readonly List<WindowEntry> _timeline = new List<WindowEntry>();
        private readonly List<string> _windowEvents = new List<string>();
        private int _windowEntry = -1;
        private bool _shown, _minimised;
        private TickRect _rect;
        private long _moves, _minimises;

        public ParityRunLedger(bool traceOff, TextWriter log, string runId)
        {
            _traceOff = traceOff;
            _log = log;
            _runId = runId;
        }

        /// <summary>Checkpoints compared so far.</summary>
        public int Checkpoints { get; private set; }

        /// <summary>The first failure, "checkpoint k: what", or null while every checkpoint has passed.</summary>
        public string Failure { get; private set; }

        /// <summary>One start row's sid, from the validated scenario. A start companion is pending until
        /// <see cref="StartupActivated"/>, so a failed starter's tick-0 dispose-request takes it out before it ever joins.</summary>
        public void Seed(string sid)
        {
            if (_members.ContainsKey(sid)) Inconsistent("a start row repeats sid " + sid);
            else _members[sid] = new Member { Pending = true };
        }

        /// <summary>TickWorld.ActivateStartup: every start companion still pending is active, with no birth-activate.</summary>
        public void StartupActivated()
        {
            foreach (Member m in _members.Values) m.Pending = false;
        }

        /// <summary>
        /// One entry of the validated scenario's activeWindow timeline, in order, by its tick only (ScenarioInvalid checked
        /// it is an Int32). Review of round 4: the rectangle was read here, for every entry, before the run's try, and
        /// neither validator checks a rectangle, so a short, null, string or non-Int32 one threw out of the command after
        /// the header where the base ended the run in error (exit 1, an error footer). It is read now where and when
        /// ParityEnvironment.Tick reads it, when the entry takes effect, so an entry the environment never reads (one
        /// superseded at its own tick) is never read here either, and one it cannot read has already failed the run.
        /// </summary>
        public void WindowTimeline(JsonElement entry)
        {
            _timeline.Add(new WindowEntry { Tick = entry.GetProperty("tick").GetInt32(), Entry = entry.Clone() });
        }

        /// <summary>One line the trace writer committed: a state record is kept for the next comparison, a lifecycle event
        /// replayed. Every other event (behavior, error, birth-refused, ...) creates no member.</summary>
        public void Commit(string line)
        {
            using (JsonDocument doc = JsonDocument.Parse(line))
            {
                JsonElement r = doc.RootElement;
                string type = r.GetProperty("type").GetString();
                if (type == "state")
                {
                    _states.Add(Key(r.GetProperty("sid").GetString(), r.GetProperty("pending").GetBoolean()));
                    return;
                }
                if (type != "event") return;
                string sid = r.GetProperty("sid").GetString();
                Member m;
                switch (r.GetProperty("kind").GetString())
                {
                    case "birth-request":
                        string child = r.GetProperty("child").GetString();
                        if (_members.ContainsKey(child)) Inconsistent("birth-request for an existing member " + child);
                        else _members[child] = new Member { Pending = true };
                        break;
                    case "birth-activate":
                        if (_members.TryGetValue(sid, out m) && m.Pending) m.Pending = false;
                        else Inconsistent("birth-activate for " + sid + ", which is not pending");
                        break;
                    case "window-move":
                    case "window-minimize":
                        // A14: replayed at the checkpoint, after this tick's timeline entry took effect.
                        _windowEvents.Add(line);
                        break;
                    case "dispose-request":
                        // Unknown: no member and no tombstone, a no-op, so the sid may still be born later. A failed
                        // child never gets here: it commits only birth-refused with reason error and no dispose-request,
                        // so its provisional sid simply has no member. Pending: gone at once, no dispose follows. Active:
                        // marked, gone at its dispose.
                        if (!_members.TryGetValue(sid, out m)) break;
                        if (m.Pending) _members.Remove(sid);
                        else m.DisposeRequested = true;
                        break;
                    case "dispose":
                        if (_members.TryGetValue(sid, out m) && !m.Pending && m.DisposeRequested) _members.Remove(sid);
                        else Inconsistent("dispose of " + sid + " with no active member carrying an earlier dispose-request");
                        break;
                }
            }
        }

        /// <summary>A completed startup or step: the replay against the private lists and, in a traced run, against the
        /// state records committed since the last checkpoint.</summary>
        public void Checkpoint(int tick, TickMembership actual, ParityWindowState window)
        {
            string windowFault = ReplayWindow(tick);
            string fault = _inconsistency;
            if (fault == null) fault = Differ("the private lists", Actual(actual), "the lifecycle replay", Recorded());
            if (fault == null && !_traceOff) fault = Differ("the committed state records", Sorted(_states), "the lifecycle replay", Recorded());
            if (fault == null) fault = windowFault;
            if (fault == null)
            {
                var replayed = new ParityWindowState(_shown, _minimised, _rect, _moves, _minimises);
                if (window.Shown != replayed.Shown || window.Minimised != replayed.Minimised || !SameRect(window.Rect, replayed.Rect)
                    || window.Moves != replayed.Moves || window.Minimises != replayed.Minimises)
                    fault = "the live window holds " + window + ", the window replay " + replayed;
            }
            _states.Clear();
            _inconsistency = null;
            Checkpoints++;
            Fail(tick, fault);
        }

        /// <summary>A14's recorded side for this checkpoint: the timeline entry in force at <paramref name="tick"/>, applied
        /// only when it is a new one, then this tick's committed window events in order. Null, or what broke the replay.</summary>
        private string ReplayWindow(int tick)
        {
            int latest = -1;
            for (int i = 0; i < _timeline.Count; i++)
                if (_timeline[i].Tick <= tick) latest = i;
            if (latest != _windowEntry)
            {
                // Shown only when "visible" is true and it has a rectangle [left, top, right, bottom], as
                // ParityEnvironment.Tick reads it, else hidden, which keeps the replayed rectangle.
                _windowEntry = latest;
                _minimised = false;
                JsonElement entry = _timeline[latest].Entry, visible, rect;
                _shown = entry.TryGetProperty("visible", out visible) && visible.ValueKind == JsonValueKind.True && entry.TryGetProperty("rect", out rect);
                if (_shown)
                {
                    rect = entry.GetProperty("rect");
                    _rect = Ltrb(rect[0].GetInt32(), rect[1].GetInt32(), rect[2].GetInt32(), rect[3].GetInt32());
                }
            }
            string fault = null;
            foreach (string line in _windowEvents)
            {
                using (JsonDocument doc = JsonDocument.Parse(line))
                {
                    JsonElement r = doc.RootElement;
                    bool move = r.GetProperty("kind").GetString() == "window-move";
                    if (fault == null && (!_shown || _minimised))
                        fault = "a committed " + (move ? "window-move" : "window-minimize") + " of a window the replay holds "
                            + (_shown ? "minimised" : "hidden");
                    if (move)
                    {
                        JsonElement f = r.GetProperty("from"), t = r.GetProperty("to");
                        TickRect from = Ltrb(f[0].GetInt32(), f[1].GetInt32(), f[2].GetInt32(), f[3].GetInt32());
                        if (fault == null && !SameRect(from, _rect))
                            fault = "a committed window-move from " + f.GetRawText() + " where the replay holds the window at "
                                + new ParityWindowState(_shown, _minimised, _rect, _moves, _minimises);
                        _rect = Ltrb(t[0].GetInt32(), t[1].GetInt32(), t[2].GetInt32(), t[3].GetInt32());
                        _moves++;
                    }
                    else
                    {
                        _minimised = true;
                        _minimises++;
                    }
                }
            }
            _windowEvents.Clear();
            return fault;
        }

        private static TickRect Ltrb(int left, int top, int right, int bottom)
        {
            return new TickRect(left, top, right - left, bottom - top);
        }

        private static bool SameRect(TickRect a, TickRect b)
        {
            return a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
        }

        /// <summary>A trace-off run's end: its final state records, written once at N, against the replay and the private
        /// lists. Nothing for a traced run, whose checkpoint N compared its records already.</summary>
        public void FinalStates(int tick, TickMembership actual)
        {
            if (!_traceOff) return;
            List<string> states = Sorted(_states);
            string fault = Differ("the final state records", states, "the lifecycle replay", Recorded());
            if (fault == null) fault = Differ("the final state records", states, "the private lists", Actual(actual));
            _states.Clear();
            _finalStatesCompared = true;
            Fail(tick, fault);
        }

        /// <summary>The closing line: when the run stopped early, how many of its N+1 checkpoints ran, whether or not a
        /// failure was logged before (Codex Q-e: the actual incomplete coverage is logged even beside a first-failure line);
        /// else, for a trace-off run whose final-state write failed after checkpoint N, that its final state records were
        /// not compared, again whether or not a failure was logged before (Codex Q4: a failure-first branch must not hide
        /// it); else nothing after a failure, which was logged when it happened; else the positive witness when every
        /// checkpoint passed and, trace-off, the final state records were compared too.</summary>
        public string ClosingLine(int ticks)
        {
            if (Checkpoints != ticks + 1) return ParityRunLog.ReconciliationIncompleteLine(Checkpoints, ticks + 1, _runId);
            if (_traceOff && !_finalStatesCompared) return ParityRunLog.ReconciliationFinalStatesMissingLine(Checkpoints, ticks + 1, _runId);
            if (Failure != null) return null;
            return ParityRunLog.ReconciliationLine(Checkpoints, _traceOff, _runId);
        }

        private void Fail(int tick, string fault)
        {
            if (fault == null || Failure != null) return;
            Failure = "checkpoint " + tick.ToString(CultureInfo.InvariantCulture) + ": " + fault;
            _log.WriteLine(ParityRunLog.ReconciliationFailedLine(Failure, _runId));
        }

        private void Inconsistent(string what)
        {
            if (_inconsistency == null) _inconsistency = what;
        }

        private static string Key(string sid, bool pending)
        {
            return sid + (pending ? " pending" : " active");
        }

        private List<string> Recorded()
        {
            var keys = new List<string>();
            foreach (KeyValuePair<string, Member> m in _members) keys.Add(Key(m.Key, m.Value.Pending));
            return Sorted(keys);
        }

        private static List<string> Actual(TickMembership actual)
        {
            var keys = new List<string>();
            foreach (string sid in actual.Active) keys.Add(Key(sid, false));
            foreach (string sid in actual.Pending) keys.Add(Key(sid, true));
            return Sorted(keys);
        }

        private static List<string> Sorted(List<string> keys)
        {
            var sorted = new List<string>(keys);
            sorted.Sort(StringComparer.Ordinal);
            return sorted;
        }

        /// <summary>Null when both multisets hold the same members the same number of times, else what differs.</summary>
        private static string Differ(string side, List<string> actual, string otherSide, List<string> replay)
        {
            if (actual.Count == replay.Count)
            {
                bool same = true;
                for (int i = 0; i < actual.Count && same; i++) same = actual[i] == replay[i];
                if (same) return null;
            }
            return side + " hold [" + string.Join(", ", actual) + "], " + otherSide + " [" + string.Join(", ", replay) + "]";
        }
    }
}
