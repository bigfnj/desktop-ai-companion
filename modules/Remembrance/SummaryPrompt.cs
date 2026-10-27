using System;
using System.Globalization;
using System.IO;
using System.Text;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// The summary's prompt (remembrance 2.8.0, lane feature/remembrance-next): the built-in one, or the user's own, kept in
    /// a .txt file they point the Summary card at. The owner's design of 2026-10-27, after finding the summaries too brief and
    /// writing a prompt of their own: "I can point it at a txt file and update the prompt on my own but new users have a nice
    /// built-in prompt already." The built-in one is the default ("Use the built-in prompt"); "Use my own prompt file" makes
    /// the path row live, and "Save the built-in prompt to a file…" writes the built-in one out to start from.
    ///
    /// READ AFRESH FOR EVERY SUMMARY, at the moment its prompt is built, so an edit to the file reaches the next summary with
    /// no Apply. Missing, unreadable, empty or over <see cref="MaximumCharacters"/>, the file is not used: the summary goes
    /// out on the built-in prompt, the Status line says so and why, and the log says why in a word (never the path, which
    /// the user named, nor the text). An empty prompt is never sent. The text is the USER's: never a module default, never
    /// in a tracked file, and it goes wherever the summary goes, with each transcript.
    ///
    /// Refused, by the owner's choice of the file: a multi-line text in the pane. The host's Text row is a single-line WPF
    /// TextBox whose text filter keeps only the first line of a paste (measured on .NET 10's WPF, TextEditor._FilterText with
    /// AcceptsReturn false: "Line one of the instructions." of a three-line paste), so a fifty-line prompt would have arrived
    /// as its first line; a host multi-line kind would have raised this module's floor; a module-owned editor window was
    /// this lane's first build, replaced the same day.
    ///
    /// THE CAPS ON A SUMMARY'S LENGTH, as found for the owner's long minutes (each named at its site):
    ///   * the built-in asks themselves (Summary in 3-6 bullets): rewritten in 2.8.0, OllamaSummarizer.BuiltInAsks;
    ///   * the CLI's system prompt asked for "plain text", against a table: dropped in 2.8.0, SummaryRoute.SystemPrompt;
    ///   * the cloud call named no output limit and read no finish_reason, so a provider that stopped at its own limit had
    ///     its cut answer saved as a whole summary: since 2.8.0 a summary cut there says so (CloudSummarizer.ChatAsync);
    ///   * NOT binding, and left as they are: the runner reads up to 8 MB of a CLI's output (MaximumOutputCharacters) and
    ///     sends no output limit; Claude Code's own output ceiling is its own and ends the call with an error rather than
    ///     a cut answer; the 360 KB one-call limit on the transcript (SummaryRoute.MaximumTranscriptBytes, about six hours
    ///     of talk) sends a longer one to the local map-reduce; the ten-minute call bound (SummaryRoute.CallTimeout); the
    ///     cloud's 1 MB response bound (AiEndpointPolicy.MaximumResponseBytes), against a long summary's tens of KB;
    ///   * the local map-reduce's 6000-character parts and 8000-character merge input (OllamaSummarizer) are sized for a
    ///     small local model's context, and its merge says what it did not cover (CoverageNote). The prompt shapes its
    ///     single-chunk call and its merge; the per-part notes stay terse.
    /// </summary>
    internal static class SummaryPrompt
    {
        internal const string ChoiceKey = "summaryPrompt";
        internal const string FileKey = "summaryPromptFile";
        internal const string BuiltInId = "builtin";
        internal const string FileId = "file";
        internal const string BuiltInDisplay = "Use the built-in prompt";
        internal const string FileDisplay = "Use my own prompt file";
        /// <summary>EnabledWhen for the path row: live only while the user's own file is chosen on screen.</summary>
        internal const string OnFileOnly = ChoiceKey + "=" + FileDisplay;

        internal const string ChoiceLabel = "Summary prompt";
        internal const string FileLabel = "Your prompt file (.txt)";
        internal const string FileEmptyHint = "Not set: choose a .txt file";
        internal const string RowLabel = "Prompt in use";
        internal const string SaveLabel = "Save the built-in prompt to a file…";

        /// <summary>
        /// The most a prompt file may hold, in characters, past which the built-in one is used and the Status line says so.
        /// On stdin, never the command line (the runner's system prompt is bounded at 8000 characters for Windows' 32k
        /// command line, and this is not that), so the bound is the one-call limit's: SummaryRoute.MaximumTranscriptBytes
        /// left room for about 6k tokens of prompt and the answer beside the transcript, and 16,000 characters is about 4k
        /// tokens. The owner's prompt is about a sixth of it.
        /// </summary>
        internal const int MaximumCharacters = 16000;

        /// <summary>A file larger than this is not read at all: 16,000 characters are at most 64 KB as UTF-8.</summary>
        internal const long MaximumFileBytes = 128 * 1024;

        internal static string[] Displays() { return new[] { BuiltInDisplay, FileDisplay }; }

        /// <summary>The stored id for an option's text, or null for text that is no option, which stores nothing.</summary>
        internal static string FromDisplay(string display)
        {
            string value = (display ?? "").Trim();
            if (string.Equals(value, BuiltInDisplay, StringComparison.Ordinal)) return BuiltInId;
            if (string.Equals(value, FileDisplay, StringComparison.Ordinal)) return FileId;
            return null;
        }

        /// <summary>Whether <paramref name="settings"/> choose the user's own file; absent and anything unknown are the
        /// built-in prompt, the reading a new user gets.</summary>
        internal static bool UsesFile(IModuleSettings settings)
        {
            return settings != null && string.Equals(settings.Get(ChoiceKey, BuiltInId), FileId, StringComparison.Ordinal);
        }

        internal static string FileOf(IModuleSettings settings)
        {
            return settings == null ? "" : (settings.Get(FileKey, "") ?? "").Trim();
        }

        /// <summary>The prompt text a builder uses: the text trimmed, or null for blank (the built-in one).</summary>
        internal static string Usable(string text)
        {
            string value = (text ?? "").Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>What a summary is sent: the file's text (<see cref="Text"/>), or null for the built-in prompt, with
        /// <see cref="Problem"/> saying why when the user's file was chosen and could not be used.</summary>
        internal sealed class Resolved
        {
            internal string Text;
            internal string Problem;
            internal string FileName = "";
        }

        /// <summary>The choice and the path as one summary reads them (the saved settings at a stop, the screen for a press),
        /// and the file read NOW.</summary>
        internal static Resolved Resolve(IModuleSettings settings)
        {
            return Resolve(UsesFile(settings), FileOf(settings));
        }

        internal static Resolved Resolve(bool usesFile, string path)
        {
            var resolved = new Resolved();
            if (!usesFile) return resolved;
            resolved.FileName = string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path);
            string problem;
            resolved.Text = ReadPromptFile(path, out problem);
            resolved.Problem = problem;
            return resolved;
        }

        /// <summary>The usable text of a prompt file, or null with the reason in plain words: not chosen, missing, unreadable,
        /// empty, or too long. Never throws.</summary>
        internal static string ReadPromptFile(string path, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(path)) { problem = "no prompt file is chosen"; return null; }
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { problem = "the prompt file is missing"; return null; }
                if (info.Length > MaximumFileBytes)
                {
                    problem = "the prompt file is too long (" + (info.Length / 1024).ToString(CultureInfo.InvariantCulture) + " KB; the most is " +
                              MaximumCharacters.ToString("N0", CultureInfo.InvariantCulture) + " characters)";
                    return null;
                }
                // The encoding from the file's own byte-order mark, UTF-8 without one: what Notepad writes either way.
                string text = Usable(File.ReadAllText(path).Replace("\0", ""));
                if (text == null) { problem = "the prompt file is empty"; return null; }
                if (text.Length > MaximumCharacters)
                {
                    problem = "the prompt file is too long (" + text.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters; the most is " +
                              MaximumCharacters.ToString("N0", CultureInfo.InvariantCulture) + ")";
                    return null;
                }
                return text;
            }
            catch (Exception ex)
            {
                problem = "the prompt file could not be read (" + ex.GetType().Name + ")";
                return null;
            }
        }

        /// <summary>The log's word for a problem: never the path or the text.</summary>
        internal static string ProblemWord(string problem)
        {
            string p = problem ?? "";
            if (p.Contains("missing")) return "missing";
            if (p.Contains("empty")) return "empty";
            if (p.Contains("too long")) return "too-long";
            if (p.Contains("no prompt file")) return "not-chosen";
            return "unreadable";
        }

        /// <summary>What the Status line adds after a summary that wanted the user's file and went out on the built-in
        /// prompt; "" otherwise.</summary>
        internal static string StatusNote(Resolved resolved)
        {
            if (resolved == null || resolved.Problem == null) return "";
            return "  (⚠ the built-in prompt was used: " + resolved.Problem + ")";
        }

        /// <summary>"2,517 characters on 49 lines": a prompt's size, never its text.</summary>
        internal static string SizeOf(string text)
        {
            string value = Usable(text) ?? "";
            int lines = value.Length == 0 ? 0 : value.Replace("\r\n", "\n").Split('\n').Length;
            return value.Length.ToString("N0", CultureInfo.InvariantCulture) + " character" + (value.Length == 1 ? "" : "s") +
                   " on " + lines.ToString(CultureInfo.InvariantCulture) + " line" + (lines == 1 ? "" : "s");
        }

        /// <summary>The Summary card's "Prompt in use" row, for the SAVED choice, the file read as the next summary would.</summary>
        internal static string InfoLine(IModuleSettings settings)
        {
            if (!UsesFile(settings))
                return "The built-in prompt: an overview, a part for each topic, the decisions, a table of action items and the open " +
                       "questions. To use your own, press \"" + SaveLabel + "\" to start from it, edit the file, then choose \"" +
                       FileDisplay + "\" and pick it.";
            Resolved resolved = Resolve(settings);
            if (resolved.Problem != null)
                return "⚠ Summaries use the built-in prompt for now: " + resolved.Problem + ".";
            return "Your file " + resolved.FileName + ", " + SizeOf(resolved.Text) + ". It is read again for every summary, so an edit " +
                   "to it changes the next one with no Apply. It goes with each transcript to whichever engine Summary runs on.";
        }

        /// <summary>
        /// Where "Save the built-in prompt to a file…" writes: handed the suggested file name, it answers the chosen path, or
        /// null when the user cancelled. A WinForms save dialog, the toolkit this module's "Transcribe a WAV file…" already
        /// opens its dialog in, on the UI thread the host runs a pane action on; the host offers modules an open dialog and
        /// no save one. A seam, so the module self-test answers it.
        /// </summary>
        internal static Func<string, string> PickSaveTarget = ShowSaveDialog;

        private static string ShowSaveDialog(string suggestedName)
        {
            using (var dialog = new System.Windows.Forms.SaveFileDialog())
            {
                dialog.Title = "Save the built-in prompt";
                dialog.Filter = "Text file (*.txt)|*.txt";
                dialog.DefaultExt = "txt";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = suggestedName ?? "";
                try { dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch { }
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
            }
        }

        internal const string SuggestedFileName = "Remembrance summary prompt.txt";

        /// <summary>The built-in prompt as the saved file holds it: one line a sentence, Windows line ends, UTF-8.</summary>
        internal static string BuiltInFileText()
        {
            return OllamaSummarizer.BuiltInInstructions.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
        }

        internal static readonly Encoding FileEncoding = new UTF8Encoding(false);
    }
}
