using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

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
                sb.AppendLine("Fix that, then use \"Re-transcribe\" in the Remembrance options.");
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
                    if (!proc.WaitForExit(30 * 60 * 1000))
                    {
                        try { proc.Kill(true); } catch { }
                        why = "whisper-cli was still running after 30 minutes and was stopped.";
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
