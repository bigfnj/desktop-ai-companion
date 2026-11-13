using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The closure log lines a parity run writes to its diagnostic log, never to the trace (signed revision H :80, :150,
    /// :158, :232): the engine this process actually loaded, the synthetic label on a run whose build witness its caller
    /// supplied, the policy-blind birth witness and the population reconciliation witness. Every line ends with
    /// "runId &lt;id&gt;", the runId the same invocation's trace header carries, so a closure record can bind a line to its
    /// trace. The spellings live in this one block so that a spelling agreed with the closure checker's owner (the lane
    /// brief's open question 10) is a change here and nowhere else.
    /// </summary>
    internal static class ParityRunLog
    {
        // ---- closure line spellings (one place; each line ends RunIdField + the run's id)
        public const string LoadedEngine = "parity loaded-engine";
        public const string SyntheticWitness = "parity build-witness synthetic";
        public const string BirthWitness = "parity birth-witness";
        public const string Reconciliation = "parity reconciliation";
        public const string BuildWitnessUnreadable = "build-witness-unreadable";
        public const string RunIdField = " runId ";
        // ---- end of the spellings

        /// <summary>"parity loaded-engine engineSha256 H exeSha256 H enginePath P exePath P runId R": the files this process
        /// loaded and hashed itself, never the header's build values. The hashes come first and the paths last, since a
        /// path may hold spaces.</summary>
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
        /// The birth witness (policy:birth-cap, H :158), logged on every run whatever its scenario declares: the limit the
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
}
