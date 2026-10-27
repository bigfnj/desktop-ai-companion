using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopAICompanion.ModuleKit;

namespace DesktopAICompanion.AgentFlow
{
    public sealed partial class AgentFlowModule
    {
        /// <summary>
        /// N-agentflow-enhancements-01: OnTick's own use of the transcript beat. TranscriptsDue, the decision, is asserted
        /// in SelfCheckMultiPress; what nothing drove was the tick that consults it, because the worker runs on the pool
        /// and --module-selftest has no UI context, and the real-host check gives the copy empty transcript roots. So a
        /// tick that read the transcripts every time, or never after the first, passed both. Here one instance runs
        /// twenty-one OnTick bodies INLINE (RunTickInlineForSelfTest) at one-second steps of an injected clock
        /// (TickClockForSelfTest), over a Claude transcript fixture that grows by one record before every tick, and the
        /// folds are counted by the cursors' folded bytes (SessionCache.FoldedBytesForSelfTest): a tick that read the
        /// transcript moves them, one that did not leaves them. Mode Notify, so the tick scans; argv.json points at a
        /// file that does not exist, so the port probe answers NotFound without opening a socket, and nothing is
        /// looked at or pressed. No real transcript, rule file or editor is read.
        /// </summary>
        private static bool SelfCheckTickBeat(SelfTestProbe probe)
        {
            var utf8 = new System.Text.UTF8Encoding(false);
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-beat-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            string home = System.IO.Path.Combine(root, "home");
            string transcripts = System.IO.Path.Combine(root, "transcripts");
            string empty = System.IO.Path.Combine(root, "none");
            string homeWas = Environment.GetEnvironmentVariable(RuleLoader.HomeVariable);
            string claudeWas = Environment.GetEnvironmentVariable(TranscriptReader.ClaudeRootVariable);
            string codexWas = Environment.GetEnvironmentVariable(TranscriptReader.CodexRootVariable);
            AgentFlowModule module = null;
            try
            {
                System.IO.Directory.CreateDirectory(home);
                System.IO.Directory.CreateDirectory(transcripts);
                System.IO.Directory.CreateDirectory(empty);
                Environment.SetEnvironmentVariable(RuleLoader.HomeVariable, home);
                Environment.SetEnvironmentVariable(TranscriptReader.ClaudeRootVariable, transcripts);
                Environment.SetEnvironmentVariable(TranscriptReader.CodexRootVariable, empty);

                string session = System.IO.Path.Combine(transcripts, "beat-session.jsonl");
                System.IO.File.WriteAllBytes(session, utf8.GetBytes(
                    "{\"type\":\"permission-mode\",\"permissionMode\":\"default\"}\n"));
                Action grow = () =>
                {
                    string stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    using (var stream = new System.IO.FileStream(session, System.IO.FileMode.Append,
                               System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite))
                    {
                        byte[] line = utf8.GetBytes("{\"timestamp\":\"" + stamp + "\",\"message\":{\"content\":"
                                                    + "[{\"type\":\"text\",\"text\":\"one more record\"}]}}\n");
                        stream.Write(line, 0, line.Length);
                    }
                };

                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow"))
                {
                    host.UseStorage("agentflow", storage);
                    var seeded = host.SettingsFor("agentflow");
                    seeded.Set(SettingMode, AgentMode.Notify);
                    seeded.Set(SettingWatchClaude, "true");
                    seeded.Set(SettingWatchCodex, "false");
                    seeded.Set(SettingArgvPath, System.IO.Path.Combine(root, "no-argv-here", "argv.json"));
                    module = new AgentFlowModule();
                    module.Init(host);   // no UI context, so Init starts no tick of its own (RA-036)

                    long clock = 0;
                    module.TickClockForSelfTest = () => clock;
                    module.RunTickInlineForSelfTest = true;
                    int ticks = 0, folds = 0;
                    long foldedAfterFirst = -1;
                    var foldedAt = new List<string>();
                    for (clock = 0; clock <= 20000; clock += 1000)
                    {
                        grow();
                        long before = module._sessions.FoldedBytesForSelfTest;
                        module.OnTick(null, EventArgs.Empty);
                        ticks++;
                        long after = module._sessions.FoldedBytesForSelfTest;
                        if (ticks == 1) foldedAfterFirst = after;
                        if (after != before)
                        {
                            folds++;
                            foldedAt.Add((clock / 1000).ToString(CultureInfo.InvariantCulture) + " s");
                        }
                    }
                    string where = string.Join(", ", foldedAt.ToArray());

                    probe.Check("WITNESS (N-agentflow-enhancements-01) the first inline tick folds the growing transcript whole, and its worker finished before OnTick returned ("
                                + foldedAfterFirst.ToString(CultureInfo.InvariantCulture) + " bytes)",
                        foldedAfterFirst > 0 && module.TicksEnteredForSelfTest == ticks && module._scanning == 0);
                    probe.Check("(N-agentflow-enhancements-01) OnTick folds a transcript that grows every tick only on the ten-second beat: 3 folds in 21 one-second ticks, at 0, 10 and 20 s ("
                                + folds.ToString(CultureInfo.InvariantCulture) + " folds, at " + where + ")",
                        folds == 3 && where == "0 s, 10 s, 20 s");
                    probe.Check("WITNESS (N-agentflow-enhancements-01) the folded session reached the tick's UI half: one session seen ("
                                + module._lastSessions.ToString(CultureInfo.InvariantCulture) + ")",
                        module._lastSessions == 1);
                }
            }
            catch (Exception exception)
            {
                probe.Check("(N-agentflow-enhancements-01) the inline tick ran (" + exception.GetType().Name + ": "
                            + exception.Message + ")", false);
            }
            finally
            {
                if (module != null)
                {
                    module.RunTickInlineForSelfTest = false;
                    module.Shutdown();
                }
                Environment.SetEnvironmentVariable(RuleLoader.HomeVariable, homeWas);
                Environment.SetEnvironmentVariable(TranscriptReader.ClaudeRootVariable, claudeWas);
                Environment.SetEnvironmentVariable(TranscriptReader.CodexRootVariable, codexWas);
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
            return true;
        }
    }
}
