using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Lane feature/remembrance-next (remembrance 2.8.0): the summary's prompt file as one summary reads it, "Save the built-in
    /// prompt to a file…", and the follow-up after the summary (SummaryPrompt.cs and FollowUp.cs say what each is).
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

        /// <summary>What one follow-up runs on, read once: at a recording's stop from what is saved, for a press from what
        /// the card shows. Its prompt file is named here and read when the follow-up runs.</summary>
        private sealed class FollowUpCall
        {
            internal bool On;
            internal CodingAgentKind Agent = CodingAgentKind.Codex;
            internal string Model = "";
            internal string Effort = "";
            internal bool LeaveOutOwnClaudeSettings;
            internal bool ReadsTranscript;
            internal string PromptFile = "";
        }

        /// <param name="shown">Where the switch, the CLI, what it reads, its prompt file, and that CLI's model, effort and
        /// Claude Code switch are read: the saved settings at a stop, the on-screen copy (OnScreenSettings) for a press.</param>
        private static FollowUpCall FollowUpCallFrom(IModuleSettings shown)
        {
            CodingAgentKind agent = FollowUp.AgentOf(shown);
            return new FollowUpCall
            {
                On = FollowUp.IsOn(shown),
                Agent = agent,
                Model = SummaryRoute.ModelFor(shown, agent),
                Effort = SummaryRoute.EffortFor(shown, agent),
                LeaveOutOwnClaudeSettings = agent == CodingAgentKind.Claude && SummaryRoute.LeavesOutOwnClaudeSettings(shown),
                ReadsTranscript = FollowUp.ReadsTranscript(shown),
                PromptFile = FollowUp.PromptFileOf(shown),
            };
        }

        /// <summary>The card's "Last follow-up" row: what the last one did, this session.</summary>
        private volatile string _lastFollowUp;
        /// <summary>The summary this session wrote last, a recording's or one made by hand: one candidate for "the latest
        /// summary" (LatestSummaryPath).</summary>
        private volatile string _lastSummaryPath;
        /// <summary>A press of "Run the follow-up on the latest summary" in flight (the single-flight gate of the repo's
        /// background work).</summary>
        private int _followUpPressInFlight;

        private void NoteSummaryWritten(string summaryPath)
        {
            _lastSummaryPath = summaryPath;
        }

        /// <summary>
        /// One follow-up: the summary's text (its header left off), the transcript too when the setting says so, and the
        /// user's follow-up prompt, read from its file now, in one call through the CLI chosen, written as "&lt;name&gt; -
        /// follow-up.md" beside the summary under a header that says what was sent where. Null when it was written, else the
        /// failure in plain words; the card's "Last follow-up" row is set either way. NEVER WRITES THE SUMMARY'S FILE: it only
        /// reads it, and the follow-up is written to a file of its own, through a temporary file, so a failure leaves any
        /// earlier follow-up as it was too. The log carries the outcome's class, never the prompt, the summary or the answer.
        /// </summary>
        private async Task<string> WriteFollowUpAsync(FollowUpCall call, string meetingName, string summaryPath)
        {
            string failure;
            string written = null;
            try { failure = await RunFollowUpAsync(call, meetingName, summaryPath, out written).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log("follow-up failed: " + ex.GetType().Name);
                failure = "the follow-up could not be written";
            }
            string at = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            _lastFollowUp = failure == null
                ? "✓ " + at + ": saved as " + System.IO.Path.GetFileName(written)
                : "✗ " + at + ": " + FollowUp.Failed(failure) + ".";
            NotifyInfoChanged();
            return failure;
        }

        // Not async itself: the out parameter. The work is RunFollowUpCoreAsync's.
        private Task<string> RunFollowUpAsync(FollowUpCall call, string meetingName, string summaryPath, out string written)
        {
            written = FollowUp.PathFor(summaryPath);
            return RunFollowUpCoreAsync(call, meetingName, summaryPath, written);
        }

        private async Task<string> RunFollowUpCoreAsync(FollowUpCall call, string meetingName, string summaryPath, string followUpPath)
        {
            if (call == null) return "no follow-up is set up";
            string problem;
            string prompt = FollowUp.ReadPrompt(call.PromptFile, out problem);
            if (prompt == null)
            {
                Log("follow-up skipped: its prompt file is " + SummaryPrompt.ProblemWord(problem));
                return problem;
            }
            if (followUpPath == null) return "that file is not a summary";
            CodingAgentCli cli = _cli;
            if (cli == null) return "the CLI runner is not available";
            string summaryText;
            try { summaryText = System.IO.File.ReadAllText(summaryPath); }
            catch (Exception ex)
            {
                Log("follow-up failed: summary-unreadable " + ex.GetType().Name);
                return "the summary could not be read";
            }
            string summary = FollowUp.BodyOf(summaryText);
            if (summary.Length == 0) return "the summary is empty";
            string transcript = null;
            if (call.ReadsTranscript)
            {
                string transcriptPath = FollowUp.TranscriptPathFor(summaryPath);
                try { transcript = System.IO.File.Exists(transcriptPath) ? System.IO.File.ReadAllText(transcriptPath) : null; }
                catch (Exception ex) { Log("follow-up: transcript-unreadable " + ex.GetType().Name); transcript = null; }
                if (transcript == null) return "the transcript beside the summary could not be read";
                if (!SummaryRoute.FitsOneCall(SummaryRoute.Utf8Bytes(transcript) + SummaryRoute.Utf8Bytes(summary)))
                    return "the transcript is too long to send in one call with the summary";
            }
            CancellationToken token;
            try
            {
                CancellationTokenSource lifetime = _installCts;
                token = lifetime != null ? lifetime.Token : CancellationToken.None;
            }
            catch (ObjectDisposedException) { return "Remembrance is shutting down"; }
            CliAnswer answer = await cli.AskAsync(new CliRequest
            {
                Agent = call.Agent,
                SystemPrompt = FollowUp.SystemPrompt,
                Prompt = FollowUp.BuildPrompt(prompt, meetingName, summary, transcript),
                Timeout = FollowUp.CallTimeout,
                Purpose = "follow-up",
                Model = call.Model,
                Effort = call.Effort,
                LeaveOutOwnClaudeSettings = call.LeaveOutOwnClaudeSettings,
            }, token).ConfigureAwait(false);
            if (!answer.Ok || string.IsNullOrWhiteSpace(answer.Text))
            {
                Log("follow-up failed: cli-" + CodingAgentCli.OutcomeWord(answer.Outcome));
                return CodingAgentCliText.Describe(call.Agent, answer, FollowUp.CallTimeout, SummaryRoute.DefaultModelFor(call.Agent))
                    .TrimStart('✗', '⚠', ' ').TrimEnd('.');
            }
            string temporary = followUpPath + ".writing";
            try
            {
                System.IO.File.WriteAllText(temporary, FollowUp.FileHeader(meetingName, call.Agent, answer, transcript != null) + answer.Text,
                    new System.Text.UTF8Encoding(false));
                System.IO.File.Move(temporary, followUpPath, true);
            }
            catch (Exception ex)
            {
                try { System.IO.File.Delete(temporary); } catch { }
                Log("follow-up write failed: " + ex.GetType().Name);
                return "the follow-up could not be written";
            }
            Log("follow-up written: " + CodingAgents.IdOf(call.Agent) + (transcript != null ? " with the transcript" : ""));
            return null;
        }

        /// <summary>"Summarize a transcript…" after it wrote the summary: the follow-up, when the card has it on, and the
        /// press's ending with both outcomes and the prompt's note. False when the follow-up is off, and the caller ends the
        /// press as before.</summary>
        private async Task<bool> FollowUpAfterTryItAsync(FollowUpCall call, string name, string summaryPath, string promptNote)
        {
            if (call == null || !call.On) return false;
            string failure = await WriteFollowUpAsync(call, name, summaryPath).ConfigureAwait(false);
            FinishTryIt((failure == null ? "Summarized + follow-up: " + name : "Summarized (" + FollowUp.Failed(failure) + "): " + name) + promptNote,
                failure == null ? "Summary and follow-up ready." : "Summary ready. The follow-up failed.");
            return true;
        }

        /// <summary>The newest summary there is: the one this session wrote last, or the newest ".summary.txt" in the storage
        /// folder, its capture folders and its day folders, whichever was written later. Null when there is none.</summary>
        private string LatestSummaryPath()
        {
            string best = null;
            DateTime bestAt = DateTime.MinValue;
            Action<string> consider = delegate(string path)
            {
                try
                {
                    if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
                    DateTime at = System.IO.File.GetLastWriteTimeUtc(path);
                    if (at > bestAt) { bestAt = at; best = path; }
                }
                catch { }
            };
            consider(_lastSummaryPath);
            try
            {
                string root = StorageRoot();
                if (System.IO.Directory.Exists(root))
                {
                    foreach (string f in System.IO.Directory.EnumerateFiles(root, "*.summary.txt")) consider(f);
                    foreach (string sub in System.IO.Directory.EnumerateDirectories(root))
                        foreach (string f in System.IO.Directory.EnumerateFiles(sub, "*.summary.txt")) consider(f);
                }
            }
            catch (Exception ex) { Log("follow-up: the storage folder could not be listed (" + ex.GetType().Name + ")"); }
            return best;
        }

        /// <summary>"Run the follow-up on the latest summary": on the CLI, the reading and the prompt file the card SHOWS, the
        /// switch on or off; in the background, its end in the Status line and the card.</summary>
        private Task<string> RunFollowUpOnLatestAsync(IReadOnlyDictionary<string, string> pending)
        {
            FollowUpCall call = FollowUpCallFrom(OnScreenSettings(pending));
            string problem;
            if (FollowUp.ReadPrompt(call.PromptFile, out problem) == null)
                return Task.FromResult("✗ No follow-up runs without its prompt: " + problem + ".");
            string latest = LatestSummaryPath();
            if (latest == null) return Task.FromResult("✗ There is no summary to run it on yet.");
            if (Interlocked.CompareExchange(ref _followUpPressInFlight, 1, 0) != 0)
                return Task.FromResult("⚠ A follow-up is already running; its end shows in the Status line.");
            string name = System.IO.Path.GetFileName(latest);
            name = name.Substring(0, name.Length - ".summary.txt".Length);
            Task.Run(async () =>
            {
                try
                {
                    string failure = await WriteFollowUpAsync(call, name, latest).ConfigureAwait(false);
                    FinishTryIt(failure == null ? "Follow-up: " + name : "Follow-up failed (" + failure + "): " + name,
                        failure == null ? "Follow-up ready." : "The follow-up failed.");
                }
                catch (Exception ex) { Log("follow-up press failed: " + ex.GetType().Name); }
                finally { Interlocked.Exchange(ref _followUpPressInFlight, 0); }
            });
            return Task.FromResult("Running the follow-up through " + CodingAgents.ChoiceLabel(call.Agent) + " on " +
                                   System.IO.Path.GetFileName(latest) + "… it will be saved beside it as " +
                                   System.IO.Path.GetFileName(FollowUp.PathFor(latest)) + ".");
        }
    }
}
