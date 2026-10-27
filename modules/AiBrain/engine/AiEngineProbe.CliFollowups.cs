using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Lane feature/cli-followups: AI Brain's halves of the shared runner's follow-ups of 2026-10-27. The warning on the
    /// Haiku choice (BACKLOG N-measure-model-03, the owner's decision "Warn on the Haiku choice") and the open pane told
    /// when the saved CLI's details read lands (N-remembrance-backlog-01, AI Brain's wiring). The runner's own halves are
    /// in CodingAgentCliSelfCheck.cs. No CLI is started, no model is called: every call goes to the rig's fake CLI.
    /// </summary>
    public static partial class AiEngineProbe
    {
        private static bool CheckCliFollowupsLane(StringBuilder sb)
        {
            bool ok = true;
            ok &= CheckLaneHaikuNote(sb);
            ok &= CheckLaneDetailsReadLanded(sb);
            return ok;
        }

        /// <summary>N-measure-model-03: an Info row right under the Claude Code model row, live only while that row shows
        /// Haiku, saying with the measured figures that Haiku may repeat names it reads on the screen. The words are typed
        /// here, so a change to them is a change here too. No other row is gated on a model row (the WITNESS).</summary>
        private static bool CheckLaneHaikuNote(StringBuilder sb)
        {
            bool ok = true;
            const string expected = "⚠ Haiku may repeat names and other private details it reads on the screen, though told not to: " +
                                    "in a test on 112 sample screens (2026-10-27), 14 of its remarks did, against none of Opus's and 1 of Sonnet's.";
            using (var rig = new CliRig("aibrain-cli-followups-haiku", ""))
            {
                OptionsPane pane = rig.Pane;
                SettingField note = FieldOf(pane, "cliClaudeHaikuNote");
                int model = IndexOfField(pane, "cliClaudeModel");
                ok &= Check(sb, "aibrain cli-followups: the CLI card warns on Haiku in an Info row right under the Claude Code model row, " +
                                "live only while that row shows Haiku: " + (note == null ? "(no row)" : note.Label + " when " + note.EnabledWhen),
                    note != null && note.Kind == SettingKind.Info && note.Group == AiBrainModule.CliCardGroup && note.Label == "About Haiku" &&
                    model >= 0 && IndexOfField(pane, "cliClaudeHaikuNote") == model + 1 &&
                    note.EnabledWhen == "cliClaudeModel=" + AiBrainModule.ClaudeModelLabel("haiku") && IsNoteOnARowOfItsCard(pane, note));
                string shown = pane.Load()["cliClaudeHaikuNote"];
                ok &= Check(sb, "aibrain cli-followups: the note says Haiku may repeat names it reads on the screen, with the measured figures: " + shown,
                    shown == expected && shown == AiBrainModule.HaikuNote);
                var modelGated = new List<string>();
                if (pane != null && pane.Schema != null)
                    foreach (SettingField f in pane.Schema)
                        if (f != null && !string.IsNullOrEmpty(f.EnabledWhen) &&
                            (f.EnabledWhen.StartsWith("cliClaudeModel=", StringComparison.Ordinal) || f.EnabledWhen.StartsWith("cliCodexModel=", StringComparison.Ordinal)))
                            modelGated.Add(f.Id + " (" + f.EnabledWhen + ")");
                ok &= Check(sb, "WITNESS aibrain cli-followups: the Haiku note is the one row gated on a model row, and on Haiku alone: " +
                                string.Join(", ", modelGated),
                    modelGated.Count == 1 && modelGated[0] == "cliClaudeHaikuNote (cliClaudeModel=Haiku)");
            }
            return ok;
        }

        /// <summary>N-remembrance-backlog-01 (AI Brain's wiring): a pane opened before the saved CLI's details were read shows
        /// "checking…", and the read that Load started behind the cache tells the open pane when it lands, so the row names
        /// the CLI without the pane being reopened; the Load that follows finds the reading fresh and causes no second
        /// notification (the WITNESS that a notification starts no read of its own).</summary>
        private static bool CheckLaneDetailsReadLanded(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new CliRig("aibrain-cli-followups-landed", ", \"CliBackend\": \"claude\""))
            {
                OptionsPane pane = rig.Pane;
                rig.Fake.Respond = FakeCliProcess.Answering(CliReply);
                int notified = 0;
                Action counter = delegate { Interlocked.Increment(ref notified); };
                pane.InfoChanged += counter;
                try
                {
                    string before = pane.Load()["cliNameClaude"];
                    bool landed = SpinWait.SpinUntil(delegate { return Volatile.Read(ref notified) >= 1; }, TimeSpan.FromSeconds(10));
                    Thread.Sleep(50);
                    int afterLanding = Volatile.Read(ref notified);
                    string after = pane.Load()["cliNameClaude"];
                    Thread.Sleep(100);
                    int afterReload = Volatile.Read(ref notified);
                    ok &= Check(sb, "aibrain cli-followups: the saved CLI's details read tells the open pane when it lands, and the row then names the CLI: " +
                                    before + " -> " + after,
                        before == "Claude Code: checking…" && landed && afterLanding == 1 &&
                        after.StartsWith("Claude Code 2.1.292, ", StringComparison.Ordinal));
                    ok &= Check(sb, "WITNESS aibrain cli-followups: ...and the Load that follows finds the reading fresh and tells the pane nothing more (" +
                                    afterReload + ")",
                        afterReload == afterLanding && !rig.Runner.DetailsReadInFlightForDiagnostics(CodingAgentKind.Claude));
                }
                finally { pane.InfoChanged -= counter; }
            }
            return ok;
        }
    }
}
