using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Lane feature/remembrance-next (remembrance 2.8.0): the summary's prompt file as one summary reads it, and "Save the
    /// built-in prompt to a file…" (SummaryPrompt.cs says what each is).
    /// </summary>
    public sealed partial class RemembranceModule
    {
        /// <summary>The summary's prompt for one summary, its file read NOW; a file that cannot be used is logged in a word
        /// (never its path or text) and the built-in prompt goes instead, which the caller's Status line says
        /// (SummaryPrompt.StatusNote).</summary>
        private SummaryPrompt.Resolved ResolveSummaryPrompt(bool usesFile, string path)
        {
            SummaryPrompt.Resolved resolved = SummaryPrompt.Resolve(usesFile, path);
            if (resolved.Problem != null)
                Log("summary prompt: the user's prompt file was not used (" + SummaryPrompt.ProblemWord(resolved.Problem) +
                    "), so the built-in prompt was sent");
            return resolved;
        }

        /// <summary>"Save the built-in prompt to a file…": the built-in prompt written to a .txt the user picks, to edit and
        /// then choose under "Use my own prompt file". Changes no setting: what to use stays the user's choice in the card.</summary>
        private Task<string> SaveBuiltInPromptAsync(IReadOnlyDictionary<string, string> pending)
        {
            string target;
            try { target = SummaryPrompt.PickSaveTarget(SummaryPrompt.SuggestedFileName); }
            catch (Exception ex)
            {
                Log("summary prompt: the save dialog failed (" + ex.GetType().Name + ")");
                return Task.FromResult("✗ The save dialog could not open (" + ex.GetType().Name + ").");
            }
            if (string.IsNullOrWhiteSpace(target)) return Task.FromResult("Unchanged.");
            try
            {
                System.IO.File.WriteAllText(target, SummaryPrompt.BuiltInFileText(), SummaryPrompt.FileEncoding);
            }
            catch (Exception ex)
            {
                Log("summary prompt: the built-in prompt could not be saved (" + ex.GetType().Name + ")");
                return Task.FromResult("✗ The built-in prompt could not be saved there (" + ex.GetType().Name + ").");
            }
            Log("summary prompt: the built-in prompt was saved to a file");
            return Task.FromResult("✓ Saved the built-in prompt as " + System.IO.Path.GetFileName(target) + ". Edit it as you like, then choose \"" +
                                   SummaryPrompt.FileDisplay + "\", pick the file above and press Apply. Every summary after that reads it again, " +
                                   "so later edits need no Apply.");
        }
    }
}
