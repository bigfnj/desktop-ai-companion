using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Lane feature/remembrance-next (remembrance 2.8.0): the summary's prompt (the prompt it builds; the built-in one, more
    /// thorough; the user's file read afresh for every summary, an unusable one falling back to the built-in prompt with the
    /// Status line and the log saying so; "Save the built-in prompt to a file…"; a recording's summary, "Summarize a
    /// transcript…", the cloud call and the local merge all following it; a cloud summary cut at the provider's limit saying
    /// so). Every CLI call goes to the shared runner's fake and every cloud call to the fake provider: nothing is started and
    /// no model is called. The transcript, the prompts and the answers are synthetic sentinels, and the log is checked for
    /// none of them.
    /// </summary>
    public sealed partial class RemembranceModule
    {
        private const string NextOwnPrompt = "SENTINEL-OWN-PROMPT-5c2e write long minutes\nwith a table of actions";
        private const string NextFollowUpPrompt = "SENTINEL-FOLLOWUP-PROMPT-9a41 draft the announcement";
        private const string NextTranscript = "Quill: the orchard harvest moves to Thursday.\nMarlow: I will book the barn.";
        private const string NextSummaryAnswer = "CLI SUMMARY QZX-SUM: harvest on Thursday; Marlow books the barn.";
        private const string NextFollowUpAnswer = "FOLLOW-UP ANSWER QZX-FUP: the announcement draft.";

        /// <summary>The user message a Claude Code call was sent on stdin (stream-json), or "".</summary>
        private static string ClaudeStdinText(FakeCliCall call)
        {
            try
            {
                var message = System.Text.Json.Nodes.JsonNode.Parse(call.Input.Trim()) as System.Text.Json.Nodes.JsonObject;
                var content = message["message"]["content"] as System.Text.Json.Nodes.JsonArray;
                return (string)content[content.Count - 1]["text"];
            }
            catch (Exception) { return ""; }
        }

        /// <summary>What a model call was sent: Claude Code's stdin message, Codex's stdin as it is.</summary>
        private static string PromptOf(FakeCliCall call)
        {
            return call == null ? "" : call.IsCodex ? call.Input : ClaudeStdinText(call);
        }

        private static void SelfCheckRemembranceNext(Action<string, bool> check)
        {
            SelfCheckNextPrompts(check);
            SelfCheckNextPromptFile(check);
            SelfCheckNextCloudCut(check);
            SelfCheckNextInModule(check);
        }

        // ---- the prompt, pure ----
        private static void SelfCheckNextPrompts(Action<string, bool> check)
        {
            string own = OllamaSummarizer.BuildSingleShotPrompt("Standup", NextTranscript, NextOwnPrompt);
            check("remembrance 2.8.0 prompt: your own prompt opens the summary's prompt in place of the built-in asks, the meeting, the warning and the transcript after it",
                own.StartsWith("SENTINEL-OWN-PROMPT-5c2e write long minutes" + Environment.NewLine + "with a table of actions" + Environment.NewLine, StringComparison.Ordinal) &&
                own.Contains("Meeting: Standup") && own.Contains("do not quote them as fact") && own.Contains("Add nothing that is not in the transcript.") &&
                own.EndsWith("TRANSCRIPT:" + Environment.NewLine + NextTranscript + Environment.NewLine, StringComparison.Ordinal) &&
                !own.Contains(OllamaSummarizer.BuiltInOpening) && !own.Contains("Topics: a part"));
            string blank = OllamaSummarizer.BuildSingleShotPrompt("Standup", NextTranscript, "  \r\n \t ");
            check("WITNESS remembrance 2.8.0 prompt: a blank or whitespace prompt is the built-in one, which opens with the built-in line",
                blank == OllamaSummarizer.BuildSingleShotPrompt("Standup", NextTranscript) &&
                blank.StartsWith(OllamaSummarizer.BuiltInOpening + Environment.NewLine, StringComparison.Ordinal));
            check("remembrance 2.8.0 prompt: the built-in asks are the thorough ones: an overview, a part per topic, a table of action items, open questions, and an ending never cut mid-sentence",
                blank.Contains("Overview: what the meeting was for") && blank.Contains("Topics: a part for each topic") &&
                blank.Contains("Action items: a table with the columns Owner, Action, Details, Due and Follow up with") &&
                blank.Contains("Open questions:") && blank.Contains("never stop in the middle of a sentence") && !blank.Contains("3-6 bullets"));
            check("remembrance 2.8.0 prompt: the CLI's system prompt no longer asks for plain text, which argued against a table",
                !SummaryRoute.SystemPrompt.Contains("plain text") && SummaryRoute.SystemPrompt.StartsWith("You write summaries of meeting transcripts", StringComparison.Ordinal));

            // The local map-reduce: the merge follows the user's prompt, and so does a transcript that fits one part.
            string longTranscript = string.Join("\n", System.Linq.Enumerable.Repeat("a line of orchard talk", 800));
            var prompts = new List<string>();
            Func<string, Task<string>> model = delegate(string prompt) { lock (prompts) prompts.Add(prompt); return Task.FromResult("note"); };
            OllamaSummarizer.SummaryResult merged = OllamaSummarizer.SummarizeWithAsync(model, "Standup", longTranscript, NextOwnPrompt, null,
                CancellationToken.None).GetAwaiter().GetResult();
            string merge = prompts.Find(p => p.StartsWith("Below are notes", StringComparison.Ordinal)) ?? "";
            prompts.Clear();
            OllamaSummarizer.SummarizeWithAsync(model, "Standup", "a short meeting", NextOwnPrompt, null, CancellationToken.None).GetAwaiter().GetResult();
            string single = prompts.Count == 1 ? prompts[0] : "";
            check("remembrance 2.8.0 prompt: a local summary follows your own prompt, in its merge and for a transcript that fits one part",
                merged.Ok && merge.Contains("SENTINEL-OWN-PROMPT-5c2e") && merge.Contains("NOTES:") && !merge.Contains("Topics: a part") &&
                single.StartsWith("SENTINEL-OWN-PROMPT-5c2e", StringComparison.Ordinal));
        }

        // ---- the prompt file, as one summary reads it ----
        private static void SelfCheckNextPromptFile(Action<string, bool> check)
        {
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dp-remembrance-promptfile-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(scratch);
                string good = System.IO.Path.Combine(scratch, "mine.txt");
                string empty = System.IO.Path.Combine(scratch, "empty.txt");
                string huge = System.IO.Path.Combine(scratch, "huge.txt");
                System.IO.File.WriteAllText(good, "\r\n  " + NextOwnPrompt + "  \r\n");
                System.IO.File.WriteAllText(empty, " \r\n\t \r\n");
                System.IO.File.WriteAllText(huge, new string('x', SummaryPrompt.MaximumCharacters + 1));
                SummaryPrompt.Resolved used = SummaryPrompt.Resolve(true, good);
                SummaryPrompt.Resolved builtIn = SummaryPrompt.Resolve(false, good);
                check("remembrance 2.8.0 prompt file: chosen, its text is the prompt, trimmed; not chosen, the built-in prompt whatever path is saved",
                    used.Text == NextOwnPrompt && used.Problem == null && used.FileName == "mine.txt" && builtIn.Text == null && builtIn.Problem == null &&
                    SummaryPrompt.StatusNote(used) == "" && SummaryPrompt.StatusNote(builtIn) == "");
                SummaryPrompt.Resolved missing = SummaryPrompt.Resolve(true, System.IO.Path.Combine(scratch, "gone.txt"));
                SummaryPrompt.Resolved blank = SummaryPrompt.Resolve(true, empty);
                SummaryPrompt.Resolved unset = SummaryPrompt.Resolve(true, "");
                check("remembrance 2.8.0 prompt file: missing, empty or not chosen, it is the built-in prompt and the Status line says why, never an empty prompt: " +
                      missing.Problem + " | " + blank.Problem + " | " + unset.Problem,
                    missing.Text == null && missing.Problem == "the prompt file is missing" &&
                    blank.Text == null && blank.Problem == "the prompt file is empty" &&
                    unset.Text == null && unset.Problem == "no prompt file is chosen" &&
                    SummaryPrompt.StatusNote(missing) == "  (⚠ the built-in prompt was used: the prompt file is missing)");
                SummaryPrompt.Resolved tooLong = SummaryPrompt.Resolve(true, huge);
                check("remembrance 2.8.0 prompt file: one past the bound is the built-in prompt, said, never cut: " + tooLong.Problem,
                    tooLong.Text == null && tooLong.Problem == "the prompt file is too long (16,001 characters; the most is 16,000)");
                check("remembrance 2.8.0 prompt file: the log's word for a problem names no path: " +
                      SummaryPrompt.ProblemWord(missing.Problem) + ", " + SummaryPrompt.ProblemWord(blank.Problem) + ", " + SummaryPrompt.ProblemWord(tooLong.Problem),
                    SummaryPrompt.ProblemWord(missing.Problem) == "missing" && SummaryPrompt.ProblemWord(blank.Problem) == "empty" &&
                    SummaryPrompt.ProblemWord(tooLong.Problem) == "too-long" && SummaryPrompt.ProblemWord(unset.Problem) == "not-chosen");
            }
            catch (Exception ex) { check("remembrance 2.8.0 prompt file: the checks threw " + ex.GetType().Name + ": " + ex.Message, false); }
            finally { try { System.IO.Directory.Delete(scratch, true); } catch { } }
        }

        // ---- a cloud summary stopped at the provider's output limit says so ----
        private static void SelfCheckNextCloudCut(Action<string, bool> check)
        {
            const string Key = "sk-test-cloud-0123456789abcdef";
            string error;
            var cut = new FakeCloudProvider { AcceptKey = Key, Reply = "CLOUD SUMMARY that stops in the mid", FinishReason = "length" };
            var whole = new FakeCloudProvider { AcceptKey = Key, Reply = "CLOUD SUMMARY that ends.", FinishReason = "stop" };
            using (CloudSummarizer cutCloud = CloudSummarizer.TryCreate("https://cloud.example.invalid/v1/", Key, cut, out error))
            using (CloudSummarizer wholeCloud = CloudSummarizer.TryCreate("https://cloud.example.invalid/v1/", Key, whole, out error))
            {
                CloudResult cutResult = cutCloud.ChatAsync("gpt-test-small", "S", "P", TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
                CloudResult wholeResult = wholeCloud.ChatAsync("gpt-test-small", "S", "P", TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
                string saved = CloudSummarizer.TextAsSaved(cutResult);
                check("remembrance 2.8.0 cloud: a summary the provider stopped at its output limit is saved with a note saying it ends early",
                    cutResult.Ok && cutResult.StoppedAtLimit && saved.StartsWith("CLOUD SUMMARY that stops in the mid", StringComparison.Ordinal) &&
                    saved.EndsWith(CloudSummarizer.StoppedAtLimitNote, StringComparison.Ordinal));
                check("WITNESS remembrance 2.8.0 cloud: ...and one that ended by itself is saved as it came",
                    wholeResult.Ok && !wholeResult.StoppedAtLimit && CloudSummarizer.TextAsSaved(wholeResult) == "CLOUD SUMMARY that ends.");
            }
        }

        // ---- inside the module: the card, a recording's summary, an edit, a file gone, a press, the cloud ----
        private static void SelfCheckNextInModule(Action<string, bool> check)
        {
            Func<string, string, string, string, string, Action<string>, CancellationToken, Task<OllamaSummarizer.SummaryResult>> savedSummarize = Summarize;
            TranscribeFile savedTranscribe = TranscribeWav;
            Func<string, CancellationToken, Task<IReadOnlyList<string>>> savedLister = ListModels;
            Func<string, string> savedSaveTarget = SummaryPrompt.PickSaveTarget;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-next-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            // A data folder of the module's own, for the cloud key the cloud check saves.
            var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage(Id);
            Func<Func<bool>, bool> pumpUntil = delegate(Func<bool> condition)
            {
                return SpinWait.SpinUntil(delegate { ui.Drain(); return condition(); }, TimeSpan.FromSeconds(15));
            };
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                System.IO.Directory.CreateDirectory(scratch);
                string exe = System.IO.Path.Combine(scratch, "whisper-cli.exe");
                string whisperModel = System.IO.Path.Combine(scratch, "ggml-base.en.bin");
                System.IO.File.WriteAllBytes(exe, new byte[16]);
                System.IO.File.WriteAllBytes(whisperModel, new byte[16]);
                string promptFile = System.IO.Path.Combine(scratch, "my summary prompt.txt");
                System.IO.File.WriteAllText(promptFile, NextOwnPrompt);
                TranscribeWav = delegate(string wav, string transcriptPath, string whisperExe, string modelPath,
                    string meetingName, IReadOnlyList<string> attendees, DateTimeOffset? recordedAt, out bool did)
                {
                    did = true;
                    try { System.IO.File.WriteAllText(transcriptPath, NextTranscript); } catch { }
                    return NextTranscript;
                };
                int localSummaries = 0;
                Summarize = delegate(string endpoint, string summaryModel, string meetingName, string transcript,
                    string instructions, Action<string> report, CancellationToken token)
                {
                    Interlocked.Increment(ref localSummaries);
                    return Task.FromResult(new OllamaSummarizer.SummaryResult { Ok = true, Text = "LOCAL SUMMARY" });
                };
                ListModels = delegate { return Task.FromResult((IReadOnlyList<string>)new List<string>()); };

                using (var cliScratch = new FakeCliScratch())
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    bool codexFails = false;
                    var fake = new FakeCliProcess();
                    Func<FakeCliCall, CancellationToken, Task<CliProcessResult>> ordinary = FakeCliProcess.Answering(NextSummaryAnswer);
                    fake.Respond = delegate(FakeCliCall call, CancellationToken token)
                    {
                        if (!call.IsModelCall) return ordinary(call, token);
                        bool followUpCall = PromptOf(call).StartsWith("SENTINEL-FOLLOWUP-PROMPT", StringComparison.Ordinal);
                        if (call.IsCodex)
                            return Task.FromResult(codexFails
                                ? FakeCliProcess.Result(1, FakeCliProcess.CodexFailure("stream disconnected before completion"), "")
                                : FakeCliProcess.Result(0, FakeCliProcess.CodexStream(followUpCall ? NextFollowUpAnswer : NextSummaryAnswer), ""));
                        string alias = call.After("--model");
                        return Task.FromResult(FakeCliProcess.Result(0, FakeCliProcess.ClaudeStream(followUpCall ? NextFollowUpAnswer : NextSummaryAnswer,
                            false, FakeCliProcess.AnsweredModelFor(alias), alias ?? "fake-default"), ""));
                    };
                    CodingAgentCli runner = cliScratch.NewRunner(fake, new List<string>());
                    Func<string, FakeCliCall> findCall = delegate(string startsWith)
                    {
                        return fake.Calls.Find(c => c.IsModelCall && PromptOf(c).StartsWith(startsWith, StringComparison.Ordinal));
                    };

                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host.UseStorage(Id, storage);
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor(Id);
                    string store = System.IO.Path.Combine(scratch, "store");
                    s.Set("storageLocation", store);
                    s.Set("summaryOn", "true");
                    s.Set("whisperExe", exe);
                    s.Set("whisperModel", whisperModel);
                    s.Set("summaryModelsCache", "alpha:1b");
                    s.Set(SummaryRoute.SettingKey, CodingAgents.ClaudeId);
                    var module = new RemembranceModule();
                    module.CliForDiagnostics = runner;
                    module.Init(host);
                    // One capture after another: a distinct second, so each gets its own folder.
                    Action recordOnce = delegate
                    {
                        Thread.Sleep(1100);
                        module.StartRecordingForSelfTest();
                        Thread.Sleep(80);
                        module.StopRecordingForSelfTest();
                    };
                    try
                    {
                        OptionsPane pane = host.OptionsPanes[0];
                        IReadOnlyDictionary<string, string> fresh = pane.Load();

                        // ---- the rows and buttons ----
                        SettingField choiceRow = FieldFor(pane, SummaryPrompt.ChoiceKey);
                        SettingField fileRow = FieldFor(pane, SummaryPrompt.FileKey);
                        SettingField promptRow = FieldFor(pane, "summaryPromptShown");
                        check("remembrance 2.8.0 prompt: the Summary card offers the built-in prompt or your own file, the path row live only for the file, a row saying which is in use, and the save button: " +
                              ActionLabelsOf(pane, "Summary"),
                            choiceRow != null && choiceRow.Kind == SettingKind.Radio && choiceRow.Group == "Summary" &&
                            string.Join("|", choiceRow.Options) == "Use the built-in prompt|Use my own prompt file" &&
                            fileRow != null && fileRow.Kind == SettingKind.FilePath && fileRow.Group == "Summary" &&
                            fileRow.FileExtensions != null && string.Join("|", fileRow.FileExtensions) == "txt" &&
                            fileRow.EnabledWhen == "summaryPrompt=Use my own prompt file" &&
                            promptRow != null && promptRow.Kind == SettingKind.Info && promptRow.Group == "Summary" &&
                            ActionLabelsOf(pane, "Summary") == "Save the built-in prompt to a file…");
                        check("remembrance 2.8.0 prompt: a fresh install uses the built-in prompt, and the row says so: " + fresh["summaryPromptShown"],
                            fresh[SummaryPrompt.ChoiceKey] == "Use the built-in prompt" && fresh[SummaryPrompt.FileKey] == "" &&
                            fresh["summaryPromptShown"].StartsWith("The built-in prompt:", StringComparison.Ordinal) && s.Get(SummaryPrompt.ChoiceKey, null) == null);


                        // ---- Save the built-in prompt to a file… ----
                        string savedPromptPath = System.IO.Path.Combine(scratch, "saved prompt.txt");
                        string suggested = null;
                        SummaryPrompt.PickSaveTarget = delegate(string name) { suggested = name; return savedPromptPath; };
                        PaneAction saveAction = PaneActionFor(pane, "Summary", SummaryPrompt.SaveLabel);
                        string saveAnswer = Press(saveAction, CopyOf(fresh), ui, TimeSpan.FromSeconds(5)) ?? "(no answer)";
                        string savedText = ReadWhenWritten(savedPromptPath);
                        SummaryPrompt.PickSaveTarget = delegate { return null; };
                        string cancelAnswer = Press(saveAction, CopyOf(fresh), ui, TimeSpan.FromSeconds(5));
                        check("remembrance 2.8.0 prompt: Save the built-in prompt to a file writes the built-in prompt to the file picked, changes no setting, and Cancel writes nothing: " + saveAnswer,
                            saveAnswer.StartsWith("✓ Saved the built-in prompt as saved prompt.txt.", StringComparison.Ordinal) &&
                            suggested == SummaryPrompt.SuggestedFileName && savedText == SummaryPrompt.BuiltInFileText() &&
                            SummaryPrompt.Usable(savedText) == OllamaSummarizer.BuiltInInstructions.Replace("\n", "\r\n") &&
                            cancelAnswer == "Unchanged." && s.Get(SummaryPrompt.ChoiceKey, null) == null && s.Get(SummaryPrompt.FileKey, null) == null);

                        // ---- an Apply of your own file ----
                        Dictionary<string, string> apply = CopyOf(pane.Load());
                        apply[SummaryPrompt.ChoiceKey] = SummaryPrompt.FileDisplay;
                        apply[SummaryPrompt.FileKey] = promptFile;
                        bool applied = pane.Save(apply);
                        IReadOnlyDictionary<string, string> afterApply = pane.Load();
                        check("remembrance 2.8.0 prompt: an Apply of your own file stores the choice and the path, and the row names the file and its size, never its text: " +
                              afterApply["summaryPromptShown"],
                            applied && s.Get(SummaryPrompt.ChoiceKey, null) == "file" && s.Get(SummaryPrompt.FileKey, null) == promptFile &&
                            afterApply["summaryPromptShown"].StartsWith("Your file my summary prompt.txt, " + SummaryPrompt.SizeOf(NextOwnPrompt) + ".", StringComparison.Ordinal) &&
                            !afterApply["summaryPromptShown"].Contains("SENTINEL"));

                        // ---- a recording's summary on your file ----
                        fake.Clear();
                        host.ClearLoggedLines();
                        recordOnce();
                        pumpUntil(delegate { return module.StatusLine().StartsWith("Transcript + summary: ", StringComparison.Ordinal); });
                        FakeCliCall summaryCall = findCall("SENTINEL-OWN-PROMPT");
                        string summaryPrompt = PromptOf(summaryCall);
                        check("remembrance 2.8.0 prompt: a recording's summary sends your file's text on stdin, in place of the built-in prompt, never on the command line",
                            summaryCall != null && !summaryCall.IsCodex && summaryPrompt.Contains(NextTranscript.Split('\n')[0]) &&
                            !summaryPrompt.Contains(OllamaSummarizer.BuiltInOpening) && summaryCall.After("--system-prompt") == SummaryRoute.SystemPrompt &&
                            !summaryCall.Arguments.Exists(a => a.Contains("SENTINEL-OWN")));

                        // ---- the file edited with no Apply: the next summary reads the new text ----
                        System.IO.File.WriteAllText(promptFile, "SENTINEL-EDITED-PROMPT-77ab the same, but shorter");
                        fake.Clear();
                        recordOnce();
                        pumpUntil(delegate { return findCall("SENTINEL-EDITED-PROMPT-77ab") != null; });
                        check("WITNESS remembrance 2.8.0 prompt file: an edit to the file reaches the next summary with no Apply",
                            findCall("SENTINEL-EDITED-PROMPT-77ab") != null && findCall("SENTINEL-OWN-PROMPT") == null);

                        // ---- the file gone: the summary goes out on the built-in prompt, said ----
                        System.IO.File.Delete(promptFile);
                        fake.Clear();
                        host.ClearLoggedLines();
                        recordOnce();
                        string noneLine = null;
                        bool noneSaid = pumpUntil(delegate { noneLine = module.StatusLine(); return noneLine.Contains("(⚠ the built-in prompt was used"); });
                        List<FakeCliCall> goneCalls = fake.Calls.FindAll(c => c.IsModelCall);
                        string nonePart = noneLine == null ? "(no Status line)" : noneLine.Split(new[] { "  |  " }, StringSplitOptions.None)[0];
                        check("remembrance 2.8.0 prompt file: a file gone at the summary sends the built-in prompt, and the Status line and the log say so, naming no path: " + nonePart,
                            noneSaid && goneCalls.Count >= 1 && PromptOf(goneCalls[0]).StartsWith(OllamaSummarizer.BuiltInOpening, StringComparison.Ordinal) &&
                            nonePart.EndsWith("(⚠ the built-in prompt was used: the prompt file is missing)", StringComparison.Ordinal) &&
                            host.LoggedLines.Exists(l => l.Contains("summary prompt: the user's prompt file was not used (missing), so the built-in prompt was sent")) &&
                            !host.LoggedLines.Exists(l => l.Contains("my summary prompt") || l.Contains("my follow-up prompt")));

                        // ---- Summarize a transcript…: the prompt file the card SHOWS ----
                        string screenPrompt = System.IO.Path.Combine(scratch, "on screen.txt");
                        System.IO.File.WriteAllText(screenPrompt, "SENTINEL-SCREEN-PROMPT-31cd");
                        string manual = System.IO.Path.Combine(scratch, "Orchard.transcript.txt");
                        System.IO.File.WriteAllText(manual, NextTranscript);
                        host.PickedFiles = new List<string> { manual };
                        Dictionary<string, string> manualScreen = CopyOf(pane.Load());
                        manualScreen[SummaryPrompt.FileKey] = screenPrompt;
                        fake.Clear();
                        Press(PaneActionFor(pane, "Try it on a file", "Summarize a transcript…"), manualScreen, ui, TimeSpan.FromSeconds(5));
                        bool manualDone = pumpUntil(delegate { return module.StatusLine().StartsWith("Summarized: Orchard", StringComparison.Ordinal); });
                        check("remembrance 2.8.0 prompt: Summarize a transcript sends the prompt file the card shows, applied or not",
                            manualDone && findCall("SENTINEL-SCREEN-PROMPT-31cd") != null && s.Get(SummaryPrompt.FileKey, null) == promptFile);

                        // ---- a cloud summary sends it too, as its prompt ----
                        const string CloudKey = "sk-test-cloud-0123456789abcdef";
                        var provider = new FakeCloudProvider { AcceptKey = CloudKey };
                        module.CloudHandlerForDiagnostics = provider;
                        string keyError;
                        bool keySaved = module._cloudKeys != null && module._cloudKeys.TrySet(CloudKey, out keyError);
                        Dictionary<string, string> cloudScreen = CopyOf(pane.Load());
                        cloudScreen[SummaryRoute.SettingKey] = SummaryRoute.CloudDisplay;
                        cloudScreen["cloudProvider"] = "custom";
                        cloudScreen["cloudEndpoint"] = "https://cloud.example.invalid/v1";
                        cloudScreen["cloudModel"] = "gpt-test-small";
                        cloudScreen[SummaryPrompt.FileKey] = screenPrompt;
                        string barn = System.IO.Path.Combine(scratch, "Barn.transcript.txt");
                        System.IO.File.WriteAllText(barn, NextTranscript);
                        host.PickedFiles = new List<string> { barn };
                        Press(PaneActionFor(pane, "Try it on a file", "Summarize a transcript…"), cloudScreen, ui, TimeSpan.FromSeconds(5));
                        bool cloudDone = pumpUntil(delegate { return module.StatusLine().StartsWith("Summarized: Barn", StringComparison.Ordinal); });
                        string[] chat = provider.LastChat();
                        check("remembrance 2.8.0 prompt: a cloud summary sends your prompt file's text as its prompt, the transcript after it: " +
                              (keySaved ? "" : "no key saved, ") + module.StatusLine().Split(new[] { "  |  " }, StringSplitOptions.None)[0],
                            keySaved && cloudDone && chat != null && chat[3].Contains("SENTINEL-SCREEN-PROMPT-31cd") && chat[3].Contains("orchard harvest moves") &&
                            !chat[3].Contains("Topics: a part"));

                        check("remembrance 2.8.0 prompt: no log line carries the prompts, the transcript or the summary",
                            !host.LoggedLines.Exists(l => l.Contains("SENTINEL-") || l.Contains("orchard harvest") || l.Contains("QZX-SUM")));
                        check("remembrance 2.8.0 prompt: ...and the local summarizer was never asked", localSummaries == 0);
                    }
                    finally { try { module.Shutdown(); } catch { } }
                }
            }
            catch (Exception ex) { check("remembrance 2.8.0: the checks threw " + ex.GetType().Name + ": " + ex.Message, false); }
            finally
            {
                Summarize = savedSummarize;
                TranscribeWav = savedTranscribe;
                ListModels = savedLister;
                SummaryPrompt.PickSaveTarget = savedSaveTarget;
                SynchronizationContext.SetSynchronizationContext(previous);
                try { storage.Dispose(); } catch { }
                try { System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }
    }
}
