using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        /// Best-effort; never throws. <paramref name="didTranscribe"/> is true only when Whisper actually ran.</summary>
        public static string Transcribe(string wavPath, string transcriptPath, string whisperExe, string modelPath,
            string meetingName, IReadOnlyList<string> attendees, out bool didTranscribe)
        {
            didTranscribe = false;
            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(meetingName) ? "Recording" : meetingName);
            sb.AppendLine("Recorded: " + DateTime.Now.ToString("f"));
            if (attendees != null && attendees.Count > 0)
                sb.AppendLine("Invited (" + attendees.Count + "): " + string.Join(", ", attendees));
            sb.AppendLine(new string('-', 48));
            sb.AppendLine();

            string why;
            string body = RunWhisper(wavPath, whisperExe, modelPath, out didTranscribe, out why);
            if (!didTranscribe)
            {
                sb.AppendLine("[Transcription pending] " + why);
                // NAMES A CONTROL THAT EXISTS. This said 'use "Re-transcribe" in the Remembrance
                // options' -- one grep hit in the whole repo, this line, because no such action was
                // ever built. It is the only sentence a user reads on the one path where
                // transcription has already failed, so it sent them hunting through twelve buttons
                // for a thirteenth.
                sb.AppendLine("Fix that, then use \"Transcribe a WAV file...\" in the Remembrance options and pick the .wav beside this file.");
                sb.AppendLine("The audio is kept until the 72-hour purge; move it out of the folder to keep it longer.");
            }
            else
            {
                sb.Append(body);
            }

            string text = sb.ToString();
            try { File.WriteAllText(transcriptPath, text, new UTF8Encoding(false)); } catch { }
            return text;
        }

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

                string outBase = Path.Combine(Path.GetDirectoryName(wavPath),
                    Path.GetFileNameWithoutExtension(wavPath) + ".whisper");
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
                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) { why = "Windows would not start whisper-cli."; return ""; }
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
                        why = "whisper-cli was still running after " + Minutes(bound) + " minutes and was stopped. "
                            + "The recording is " + Minutes(audioLength) + " minutes long and the limit is "
                            + WhisperTimeoutFactor.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + "x that, never under " + Minutes(MinimumWhisperTimeout) + " minutes. A smaller model "
                            + "(tiny.en or base.en) transcribes faster.";
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
                string txt = outBase + ".txt";
                if (!File.Exists(txt))
                { why = "whisper-cli finished cleanly but wrote no transcript beside the audio."; return ""; }
                string result = File.ReadAllText(txt);
                try { File.Delete(txt); } catch { }
                ok = true;
                return result;
            }
            catch (Exception ex) { why = "Transcription failed: " + ex.Message; return ""; }
        }

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
            return Math.Round(span.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    }
}
