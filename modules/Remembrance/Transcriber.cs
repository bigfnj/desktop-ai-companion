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

            string body = RunWhisper(wavPath, whisperExe, modelPath, out didTranscribe);
            if (!didTranscribe)
            {
                sb.AppendLine("[Transcription pending] Whisper is not configured. Set the whisper-cli path and a");
                sb.AppendLine("model in the Remembrance options (or run the setup script), then use \"Re-transcribe\".");
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

        private static string RunWhisper(string wavPath, string whisperExe, string modelPath, out bool ok)
        {
            ok = false;
            try
            {
                if (string.IsNullOrWhiteSpace(whisperExe) || !File.Exists(whisperExe)) return "";
                if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return "";
                if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath)) return "";

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
                    if (proc == null) return "";
                    proc.StandardOutput.ReadToEnd();
                    proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(30 * 60 * 1000)) { try { proc.Kill(); } catch { } return ""; }
                    if (proc.ExitCode != 0) return "";
                }
                string txt = outBase + ".txt";
                if (!File.Exists(txt)) return "";
                string result = File.ReadAllText(txt);
                try { File.Delete(txt); } catch { }
                ok = true;
                return result;
            }
            catch { return ""; }
        }
    }
}
