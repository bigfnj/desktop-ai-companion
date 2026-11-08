using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NAudio.Wave;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Transcribes a 16 kHz mono WAV with a local whisper.cpp CLI (offline; nothing leaves the machine). The
    /// module stores the whisper-cli path and a model path in settings; when either is missing the audio is
    /// kept and a stub transcript is written pointing at setup, so a recording is never lost for lack of Whisper.
    /// The transcript is always headed with the calendar attendee roster, which needs no Whisper at all.
    /// </summary>
    internal static class Transcriber
    {
        /// <summary>Write the transcript (header + body) to <paramref name="transcriptPath"/> and return it.
        /// Best-effort; never throws. <paramref name="didTranscribe"/> is true only when Whisper actually ran.
        /// <paramref name="recordedAt"/> is when the recording STARTED (CapturePaths.StartedAt on the stop path);
        /// null when nobody knows, which the header then says instead of guessing from the clock.</summary>
        public static string Transcribe(string wavPath, string transcriptPath, string whisperExe, string modelPath,
            string meetingName, IReadOnlyList<string> attendees, DateTimeOffset? recordedAt, out bool didTranscribe)
        {
            didTranscribe = false;
            string header = Header(meetingName, attendees, recordedAt);

            // A TRANSCRIPT ALREADY THERE IS NEVER TRADED FOR A STUB (N-audit-1106-040). "Transcribe a WAV file..." on a
            // capture's own recording.wav targets that capture's own transcript, and a failed run (a timeout, a bad
            // model) used to overwrite real words with "[Transcription pending]". Only a run that produced text replaces
            // one; a failure leaves it as it was and the caller reads the reason from the text returned. A stub of
            // ours is not a transcript, so it is replaced either way: the newer reason is the one worth reading.
            bool keep = HoldsTranscript(transcriptPath);

            // THE STUB GOES DOWN FIRST (N-audit-1106-041): whisper-cli can run for hours, and if the app is closed under
            // it (the job object below then ends whisper-cli with it) the capture would otherwise be left with audio and
            // no transcript, which nothing points at and the 72-hour purge then deletes. Written only where no real
            // transcript stands, for the reason above.
            if (!keep) WriteText(transcriptPath, header + PendingBody(InterruptedReason, false));

            string why;
            string body = Whisper(wavPath, whisperExe, modelPath, out didTranscribe, out why);
            string text = didTranscribe ? header + body : header + PendingBody(why, true);
            if (!didTranscribe && keep) return text;
            string unsaved = WriteText(transcriptPath, text);
            if (!didTranscribe) return text;
            // THE WORDS ARE NEVER LEFT IN NO FILE (N-audit-1106-289). RunWhisper used to delete whisper-cli's own
            // "<name>.whisper.txt" as it read it, before this write, and the write swallowed its failure while
            // didTranscribe stayed true: a transcript that could not be saved (the file open in an editor, a full disk, a
            // folder that went read-only) was announced as "Transcript ready." with the words in no file at all, and the
            // stub written before the run still on disk saying the app had closed. Now whisper-cli's file goes only once
            // the transcript is written, and a write that fails is a failed run that says where the words are.
            string whisperText = WhisperOutputPath(wavPath);
            if (unsaved != null)
            {
                didTranscribe = false;
                return header + PendingBody(UnsavedReason(unsaved, whisperText), true);
            }
            try { File.Delete(whisperText); } catch { }
            return text;
        }

        /// <summary>Why a run whose words are in whisper-cli's own file, and not in the transcript, did not finish
        /// (N-audit-1106-289). <paramref name="failure"/> is the type of what stopped the write.</summary>
        internal static string UnsavedReason(string failure, string whisperText)
        {
            return "whisper-cli transcribed it, but writing the transcript failed with " + failure +
                   ", so its words are kept beside the audio in " + Path.GetFileName(whisperText) + ".";
        }

        /// <summary>The file whisper-cli writes its text to for <paramref name="wavPath"/>: RunWhisper hands it
        /// "&lt;name&gt;.whisper" as -of, and -otxt adds ".txt".</summary>
        internal static string WhisperOutputPath(string wavPath)
        {
            return WhisperOutputBase(wavPath) + ".txt";
        }

        private static string WhisperOutputBase(string wavPath)
        {
            return Path.Combine(Path.GetDirectoryName(wavPath), Path.GetFileNameWithoutExtension(wavPath) + ".whisper");
        }

        /// <summary>
        /// The stub alone, with no whisper run: the shutdown save writes it beside the recording it had no time to
        /// transcribe (N-audit-1106-038), so the capture folder says what the audio is waiting for and how to finish it,
        /// in the same words as every other stub. Never over a real transcript. Best-effort; never throws.
        /// </summary>
        public static void WriteStub(string transcriptPath, string meetingName, IReadOnlyList<string> attendees,
            DateTimeOffset? recordedAt, string why)
        {
            if (HoldsTranscript(transcriptPath)) return;
            WriteText(transcriptPath, Header(meetingName, attendees, recordedAt) + PendingBody(why, false));
        }

        /// <summary>Why a stub written before whisper-cli started is still there: the run never finished.</summary>
        internal const string InterruptedReason =
            "whisper-cli was still running on this recording when it was stopped, most likely because the app closed.";

        /// <summary>The shutdown save's reason (N-audit-1106-038).</summary>
        internal const string AppClosedReason =
            "The app closed while this recording was being saved, so it was not transcribed.";

        /// <summary>The first word of every stub's body, at the start of its first line. What tells a stub of ours from a
        /// transcript: whisper-cli's -otxt output never opens with it.</summary>
        internal const string PendingMarker = "[Transcription pending] ";

        /// <summary>The line that closes the header of every transcript and stub this module has written since 1.0.0,
        /// with an empty line after it; the body starts below.</summary>
        private static readonly string HeaderRule = new string('-', 48);

        /// <summary>True when <paramref name="text"/> is one of our stubs: its BODY opens with the marker.</summary>
        internal static bool IsPendingStub(string text)
        {
            int body = BodyStart(text);
            return body >= 0 && text.Length - body >= PendingMarker.Length &&
                   string.CompareOrdinal(text, body, PendingMarker, 0, PendingMarker.Length) == 0;
        }

        /// <summary>
        /// Where the body of a transcript or stub of ours starts: just past the header's closing rule and the empty line
        /// under it, the first such pair in the text. -1 when there is none, which makes the text no stub, so a file this
        /// module cannot read as its own is kept.
        ///
        /// THE BODY, NOT THE WHOLE TEXT (N-cxr-w1-01). The marker used to be looked for at the start of ANY line, header
        /// included, and the header's first line is the meeting's name: a WAV picked for "Transcribe a WAV file..." named
        /// "[Transcription pending] Standup.wav", or a calendar subject that opens that way, made a real transcript read
        /// as a stub. A re-run then wrote the pre-run stub over it before whisper-cli started, and a failure left failure
        /// text where the words had been. The header's own lines cannot be the rule pair: its first line is followed by
        /// "Recorded:", never by an empty line. Both line endings are read, since a transcript is a file the user may
        /// have edited and saved with either.
        /// </summary>
        private static int BodyStart(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            foreach (string newline in new[] { "\r\n", "\n" })
            {
                string rule = newline + HeaderRule + newline + newline;
                int at = text.IndexOf(rule, StringComparison.Ordinal);
                if (at >= 0) return at + rule.Length;
            }
            return -1;
        }

        /// <summary>True when a transcript worth keeping is on disk at <paramref name="path"/>: a file with text in it
        /// that is not a stub. An unreadable file counts as one, so a read failure never licenses an overwrite.</summary>
        internal static bool HoldsTranscript(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                string text = File.ReadAllText(path);
                return !string.IsNullOrWhiteSpace(text) && !IsPendingStub(text);
            }
            catch { return true; }
        }

        /// <summary>The reason a stub gives, from the text Transcribe returned: what follows the marker on the body's first
        /// line, or null for a transcript (the header's lines are never read for it, as in IsPendingStub). The manual path
        /// puts it on the Status line when it kept an earlier transcript, which then is the only place the reason
        /// appears.</summary>
        internal static string PendingReason(string text)
        {
            if (!IsPendingStub(text)) return null;
            int start = BodyStart(text) + PendingMarker.Length;
            int end = text.IndexOf('\n', start);
            return (end < 0 ? text.Substring(start) : text.Substring(start, end - start)).TrimEnd('\r');
        }

        private static string Header(string meetingName, IReadOnlyList<string> attendees, DateTimeOffset? recordedAt)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(meetingName) ? "Recording" : meetingName);
            // "Recorded:" is the capture's START. Until 2026-09-30 it was DateTime.Now at the moment whisper ran:
            // the stop-plus-mix time on the stop path, off by the meeting's length, and days out on the manual
            // "Transcribe a WAV file..." path, and the summary prompt inherited whichever it got (RA-166). The
            // moment of transcription keeps a line of its own, so nothing the old header said is lost.
            sb.AppendLine(RecordedLine(recordedAt));
            sb.AppendLine("Transcribed: " + DateTime.Now.ToString("f"));
            if (attendees != null && attendees.Count > 0)
                sb.AppendLine("Invited (" + attendees.Count + "): " + string.Join(", ", attendees));
            sb.AppendLine(new string('-', 48));
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>The stub's body. <paramref name="fix"/> is true when the reason is a fault the user has to put right
        /// first (whisper-cli refused or was stopped), false when nothing needs fixing (the app closed).</summary>
        private static string PendingBody(string why, bool fix)
        {
            var sb = new StringBuilder();
            sb.AppendLine(PendingMarker + why);
            // NAMES A CONTROL THAT EXISTS. This said 'use "Re-transcribe" in the Remembrance
            // options' -- one grep hit in the whole repo, this line, because no such action was
            // ever built. It is the only sentence a user reads on the one path where
            // transcription has already failed, so it sent them hunting through twelve buttons
            // for a thirteenth.
            sb.AppendLine((fix ? "Fix that, then use" : "Use") + " \"Transcribe a WAV file...\" in the Remembrance options and pick the .wav beside this file.");
            sb.AppendLine("The audio is kept until the 72-hour purge; move it out of the folder to keep it longer.");
            return sb.ToString();
        }

        /// <summary>Null when written, else the type of the exception that stopped it (N-audit-1106-289): the stubs ignore
        /// it, as best effort, and Transcribe's final write acts on it.</summary>
        private static string WriteText(string path, string text)
        {
            try { File.WriteAllText(path, text, new UTF8Encoding(false)); return null; }
            catch (Exception ex) { return ex.GetType().Name; }
        }

        /// <summary>RunWhisper's shape, for the seam below.</summary>
        internal delegate string WhisperRun(string wavPath, string whisperExe, string modelPath, out bool ok, out string why);

        /// <summary>The whisper-cli run behind a delegate, so the self-test can look at the transcript file WHILE a run
        /// is in progress and decide how the run ends, with no child process. Defaults to the real run.</summary>
        internal static WhisperRun Whisper = RunWhisper;

        /// <summary>
        /// Run whisper-cli, and say WHY when it does not work.
        ///
        /// Six distinct failure paths used to return the same empty string, so the caller printed the one
        /// configuration message for all of them -- including the case the StatusLine cheerfully calls
        /// "Whisper: configured", because a truncated ggml model passes File.Exists and only whisper-cli
        /// itself knows it is bad. Its stderr was read and dropped on the floor.
        /// </summary>
        private static string RunWhisper(string wavPath, string whisperExe, string modelPath, out bool ok, out string why)
        {
            ok = false;
            why = "";
            try
            {
                if (string.IsNullOrWhiteSpace(whisperExe) || !File.Exists(whisperExe))
                { why = NotConfigured("the whisper-cli path"); return ""; }
                if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                { why = NotConfigured("a model"); return ""; }
                if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
                { why = "The recording is gone \u2014 there is nothing left to transcribe."; return ""; }

                string outBase = WhisperOutputBase(wavPath);
                TimeSpan audioLength = TryReadDuration(wavPath);
                var psi = new ProcessStartInfo
                {
                    FileName = whisperExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(modelPath);
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(wavPath);
                psi.ArgumentList.Add("-otxt");
                psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(outBase);
                // IN A KILL-ON-CLOSE JOB (N-audit-1106-041). whisper-cli used to be a free-standing child: close the app
                // in the middle of a two-hour recording's run and it carried on for up to MaximumWhisperTimeout, holding
                // the CPU and writing a .whisper.txt nobody would ever collect, because the read-back below died with the
                // app. The job's only handle is this one, so it closes when this block ends, when anything in it throws,
                // and when the process ends however it ends, and Windows ends whisper-cli with it. AI Brain contains its
                // OCR child the same way. Rejected: tracking the Process and killing it from Shutdown, which a crash or a
                // task-manager kill never reaches.
                using (Process proc = StartChild(psi))
                using (IDisposable job = AttachJob(proc))
                {
                    if (proc == null) { why = "Windows would not start whisper-cli."; return ""; }
                    // A REFUSED JOB STOPS THE RUN (N-cxr-w1-02). This was best effort, as AI Brain's is: TryAttach
                    // returns null when Windows refuses to create, configure or assign the job, using(null) is legal, and
                    // whisper-cli ran on as the free-standing child the job exists to prevent, with nothing said. AI
                    // Brain's OCR child lives for seconds; this one lives for hours, so the gap is the whole point of the
                    // job. It is ended here, before anything waits on it, and the stub says why. Rejected: carrying on
                    // and logging, which keeps the transcript on the rare machine that refuses a nested job, but leaves
                    // the user nothing on screen and the next app close an orphan holding the CPU. What remains
                    // uncontained is the moment between Process.Start and the assign, which Process.Start (no
                    // CREATE_SUSPENDED) cannot close.
                    if (job == null)
                    {
                        StopChild(proc);
                        why = JobRefusedReason;
                        return "";
                    }
                    // BOTH pipes drained CONCURRENTLY, and only then the wait.
                    //
                    // This was ReadToEnd() on stdout and then on stderr, followed by WaitForExit(timeout).
                    // stdout only reaches EOF when the child exits, so the wait was always called on a
                    // process that had already finished and the 30-minute kill switch could never fire.
                    // Worse, once whisper-cli's stderr crossed the 4 KB pipe default while this thread was
                    // blocked on stdout, both sides stopped forever -- and a model-load banner on a long
                    // recording is exactly how you cross 4 KB.
                    System.Threading.Tasks.Task<string> outText = proc.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> errText = proc.StandardError.ReadToEndAsync();
                    // THE LIMIT FOLLOWS THE RECORDING. It was a flat 30 minutes, which small.en on a laptop
                    // CPU spends on well under an hour of audio: a two-hour all-hands was killed at minute 30,
                    // the stub sent the user to "Transcribe a WAV file...", and that ran into the same wall
                    // with the same model (F182). See WhisperTimeoutFor for the floor, the factor and the ceiling.
                    TimeSpan bound = WhisperTimeoutFor(audioLength);
                    if (!proc.WaitForExit((int)bound.TotalMilliseconds))
                    {
                        try { proc.Kill(true); } catch { }
                        why = DescribeKill(bound, audioLength);
                        return "";
                    }
                    // WaitForExit(int) does not guarantee the redirected readers have drained; the
                    // parameterless overload is the documented way to flush them.
                    proc.WaitForExit();
                    string err = "";
                    try { err = errText.GetAwaiter().GetResult(); } catch { }
                    try { outText.GetAwaiter().GetResult(); } catch { }
                    if (proc.ExitCode != 0) { why = DescribeExit(proc.ExitCode, err); return ""; }
                }
                string txt = WhisperOutputPath(wavPath);
                if (!File.Exists(txt))
                { why = "whisper-cli finished cleanly but wrote no transcript beside the audio."; return ""; }
                // Read and LEFT where it is: Transcribe deletes it once the transcript is written (N-audit-1106-289).
                string result = File.ReadAllText(txt);
                ok = true;
                return result;
            }
            catch (Exception ex) { why = "Transcription failed: " + ex.Message; return ""; }
        }

        /// <summary>How whisper-cli is started and put in its job, behind delegates so the self-test can have the job
        /// refused with a harmless child standing in for whisper-cli (N-cxr-w1-02). AttachJob returns null when Windows
        /// refuses the job. Default to the real ones.</summary>
        internal static Func<ProcessStartInfo, Process> StartChild = Process.Start;
        internal static Func<Process, IDisposable> AttachJob = WhisperJob.TryAttach;

        /// <summary>Why a run whose job Windows refused did not transcribe (N-cxr-w1-02).</summary>
        internal const string JobRefusedReason =
            "Windows would not let Remembrance tie whisper-cli to the app, so it was stopped rather than left to run on its own after the app closes.";

        /// <summary>
        /// End a whisper-cli and wait for it to be gone, so nothing it does outlives this call: one in no job, which would
        /// otherwise run on after the app closes (N-cxr-w1-02), and the verify probe's after its five minutes, whose
        /// scratch folder is deleted next and cannot be while the child still holds check.wav open (N-audit-1106-290).
        /// The wait is bounded, so a child that will not end cannot hold the caller; best-effort, since one that has
        /// already exited is the outcome wanted. Both the probe and the run stop their child here, so there is one way
        /// to end one.
        /// </summary>
        internal static void StopChild(Process proc)
        {
            try { KillChild(proc); } catch { }
            try { proc.WaitForExit(5000); } catch { }
        }

        /// <summary>The kill StopChild asks for, behind a delegate so the self-test can make it land late, as a real one
        /// may: TerminateProcess only starts a process's end, so Kill(true) can return while the child still holds its
        /// files (N-audit-1106-290). A test with the real kill could not fail on the probe's old code, because Kill(true)
        /// snapshots every process on the machine after the kill, which usually outlasts the child's teardown. Defaults
        /// to the real kill, of the whole tree.</summary>
        internal static Action<Process> KillChild = delegate(Process proc) { proc.Kill(true); };

        /// <summary>The one message that is genuinely about setup, so the five that are NOT stop borrowing
        /// it.</summary>
        private static string NotConfigured(string what)
        {
            return "Whisper is not configured. Set " + what +
                   " in the Remembrance options (or run the setup script).";
        }

        /// <summary>
        /// How long whisper-cli may run for a recording of a given length: <see cref="WhisperTimeoutFactor"/>
        /// times the audio, never under <see cref="MinimumWhisperTimeout"/> (the old flat limit, and what an
        /// unreadable length falls back to) and never over <see cref="MaximumWhisperTimeout"/>, so a wedged
        /// whisper-cli still dies. The factor is sized for small.en on a slow CPU: measured on this box's
        /// Ryzen 9 3900X with base.en, 967.6 s of speech took 110.2 s (8.8x real time); small.en is roughly
        /// three times the compute, a four-core laptop a third of the throughput, so 4x covers the slowest
        /// pairing the dropdown offers with room to spare. The stop path logs audio length beside wall time
        /// for every real run so this can be re-read off real numbers.
        /// </summary>
        internal static readonly TimeSpan MinimumWhisperTimeout = TimeSpan.FromMinutes(30);
        internal static readonly TimeSpan MaximumWhisperTimeout = TimeSpan.FromHours(6);
        internal const int WhisperTimeoutFactor = 4;

        internal static TimeSpan WhisperTimeoutFor(TimeSpan audioLength)
        {
            if (audioLength <= TimeSpan.Zero) return MinimumWhisperTimeout;
            double scaled = audioLength.TotalMilliseconds * WhisperTimeoutFactor;
            if (scaled < MinimumWhisperTimeout.TotalMilliseconds) return MinimumWhisperTimeout;
            if (scaled > MaximumWhisperTimeout.TotalMilliseconds) return MaximumWhisperTimeout;
            return TimeSpan.FromMilliseconds(scaled);
        }

        /// <summary>The audio length from the WAV header, for any PCM WAV (the manual "Transcribe a WAV
        /// file..." action takes files this module did not write). Zero when it cannot be read, which the
        /// limit treats as "unknown", not as "short".</summary>
        internal static TimeSpan TryReadDuration(string wavPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath)) return TimeSpan.Zero;
                using (var reader = new WaveFileReader(wavPath)) return reader.TotalTime;
            }
            catch { return TimeSpan.Zero; }
        }

        private static string Minutes(TimeSpan span)
        {
            return Math.Round(span.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The header's "Recorded:" line: the capture's start, or an honest "unknown" for a WAV this
        /// module did not record and knows nothing about (the manual path). Pure, so the self-test pins that the
        /// clock is not consulted (RA-166).</summary>
        internal static string RecordedLine(DateTimeOffset? recordedAt)
        {
            return "Recorded: " + (recordedAt.HasValue
                ? recordedAt.Value.ToLocalTime().ToString("f")
                : "unknown (transcribed from a file; the recording's own start time is not in it)");
        }

        /// <summary>
        /// The kill message: which limit applied, and why, in terms of what is actually known. Pure, so the
        /// self-test can pin the unknown-length case: TryReadDuration's zero means "the header could not be read",
        /// which WhisperTimeoutFor honours as unknown, and this sentence used to print it as "The recording is 0
        /// minutes long" -- a false statement about a file that is merely unreadable to NAudio, and a
        /// misdescription of the rule that had applied, the floor rather than 4x (R-041). A readable length
        /// under a minute is said in seconds rather than rounded to 0, and the sentence names the floor or the
        /// ceiling when one of them, not the factor, decided the bound.
        /// </summary>
        internal static string DescribeKill(TimeSpan bound, TimeSpan audioLength)
        {
            string factor = WhisperTimeoutFactor.ToString(CultureInfo.InvariantCulture);
            string head = "whisper-cli was still running after " + Minutes(bound) + " minutes and was stopped. ";
            string rule;
            if (audioLength <= TimeSpan.Zero)
            {
                rule = "The recording's length could not be read from its WAV header, so the default "
                       + Minutes(MinimumWhisperTimeout) + "-minute limit applied.";
            }
            else
            {
                string length = audioLength.TotalMinutes < 1
                    ? Math.Round(audioLength.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " seconds"
                    : Minutes(audioLength) + " minutes";
                // Deliberately NOT the `double scaled = ...` line WhisperTimeoutFor uses, byte for byte: that line
                // is what the F182 mutation case in tests/mutate-selftest-guards.py rewrites, and a second copy of
                // it here (a comment quoting it counts too; the harness matches raw bytes) made the case match
                // twice and go NO-OP in the whole run of 2026-09-30, a silent loss of the limit's own coverage.
                TimeSpan byFactor = TimeSpan.FromMilliseconds(audioLength.TotalMilliseconds * WhisperTimeoutFactor);
                if (byFactor < MinimumWhisperTimeout)
                    rule = "The recording is " + length + " long; the limit is " + factor + "x that but never under "
                           + Minutes(MinimumWhisperTimeout) + " minutes, so the " + Minutes(MinimumWhisperTimeout)
                           + "-minute floor applied.";
                else if (byFactor > MaximumWhisperTimeout)
                    rule = "The recording is " + length + " long; the limit is " + factor + "x that but never over "
                           + Minutes(MaximumWhisperTimeout) + " minutes, so the " + Minutes(MaximumWhisperTimeout)
                           + "-minute ceiling applied.";
                else
                    rule = "The recording is " + length + " long and the limit is " + factor + "x that.";
            }
            return head + rule + " A smaller model (tiny.en or base.en) transcribes faster.";
        }

        /// <summary>Why whisper-cli refused, from its own stderr. Pure, so the self-test can assert that a
        /// non-zero exit reports the real reason rather than borrowing the configuration message: a
        /// truncated model file passes every File.Exists on the way in, and only whisper-cli knows.</summary>
        internal static string DescribeExit(int exitCode, string stderr)
        {
            string last = "";
            if (!string.IsNullOrEmpty(stderr))
                foreach (string line in stderr.Replace("\r\n", "\n").Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) last = line.Trim();
            if (last.Length > 200) last = last.Substring(0, 200) + "\u2026";
            return "whisper-cli exited " +
                   exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                   (last.Length > 0 ? ": " + last : " without saying why.");
        }

        /// <summary>
        /// A job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE holding one whisper-cli (N-audit-1106-041): AI Brain's
        /// ProcessJob, trimmed to what this needs. Disposing it, or this process ending, closes the last handle and
        /// Windows terminates whatever is still in the job. Null when Windows refuses any step.
        /// </summary>
        private sealed class WhisperJob : IDisposable
        {
            private const uint JobObjectLimitKillOnJobClose = 0x00002000;
            private const int JobObjectExtendedLimitInformation = 9;
            private IntPtr _handle;

            private WhisperJob(IntPtr handle) { _handle = handle; }

            internal static WhisperJob TryAttach(Process process)
            {
                if (process == null) return null;
                IntPtr job = IntPtr.Zero;
                IntPtr info = IntPtr.Zero;
                try
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) return null;
                    var limits = new JobObjectExtendedLimitInformationData();
                    limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformationData));
                    info = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(limits, info, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, info, (uint)size) ||
                        !AssignProcessToJobObject(job, process.Handle))
                    {
                        CloseHandle(job);
                        job = IntPtr.Zero;
                        return null;
                    }
                    var result = new WhisperJob(job);
                    job = IntPtr.Zero;
                    return result;
                }
                catch
                {
                    if (job != IntPtr.Zero) CloseHandle(job);
                    return null;
                }
                finally
                {
                    if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                }
            }

            public void Dispose()
            {
                IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information,
                uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectBasicLimitInformation
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectExtendedLimitInformationData
            {
                public JobObjectBasicLimitInformation BasicLimitInformation;
                public IoCounters IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }
        }
    }
}
