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

        /// <summary>
        /// N-audit-1106-094: a tick that finds VS Code's port not answering. The tick cleared the Status card's panel
        /// census and left the session-limit rows alone, so the card, the Session limit card and the tray kept the last
        /// figure and its reset beside a red connection row. One OnTick INLINE, as SelfCheckTickBeat runs it, with the
        /// rows holding what an answering read left and the watch marked as having just read; argv.json points at a
        /// file that does not exist, so the port answers NotFound without a socket. Mode Notify, both watches off, empty
        /// transcript roots: nothing real is read. Then (N-audit-1106-114) two more inline ticks against a fake VS Code
        /// and an argv.json in the same temporary folder naming its port.
        /// </summary>
        private static bool SelfCheckLimitWhenPortGone(SelfTestProbe probe)
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-portgone-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            string empty = System.IO.Path.Combine(root, "none");
            string homeWas = Environment.GetEnvironmentVariable(RuleLoader.HomeVariable);
            string claudeWas = Environment.GetEnvironmentVariable(TranscriptReader.ClaudeRootVariable);
            string codexWas = Environment.GetEnvironmentVariable(TranscriptReader.CodexRootVariable);
            AgentFlowModule module = null;
            try
            {
                System.IO.Directory.CreateDirectory(empty);
                Environment.SetEnvironmentVariable(RuleLoader.HomeVariable, empty);
                Environment.SetEnvironmentVariable(TranscriptReader.ClaudeRootVariable, empty);
                Environment.SetEnvironmentVariable(TranscriptReader.CodexRootVariable, empty);
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow"))
                {
                    host.UseStorage("agentflow", storage);
                    var seeded = host.SettingsFor("agentflow");
                    seeded.Set(SettingMode, AgentMode.Notify);
                    seeded.Set(SettingWatchClaude, "false");
                    seeded.Set(SettingWatchCodex, "false");
                    seeded.Set(SettingArgvPath, System.IO.Path.Combine(root, "no-argv-here", "argv.json"));
                    module = new AgentFlowModule();
                    module.Init(host);   // no UI context, so Init starts no tick of its own (RA-036)
                    module.TickClockForSelfTest = () => 0;
                    module.RunTickInlineForSelfTest = true;

                    // What an answering read left behind, and the watch marked as having just read.
                    const string figure = "Session: 42 % (resets at 17:00).";
                    const string trayFigure = "Session limit: 42 % · resets 17:00";
                    module._limitLine = figure;
                    module._limitTray = trayFigure;
                    DateTimeOffset readAt = DateTimeOffset.Now;
                    module._limitWatch.DueToRead(readAt);
                    bool sawFigure = module.LimitTrayRow() == trayFigure;

                    module.OnTick(null, EventArgs.Empty);

                    string tray = module.LimitTrayRow();
                    var values = new Dictionary<string, string>();
                    module.AddStatusValues(values);
                    module.AddSessionLimitValues(values);
                    probe.Check("WITNESS (N-audit-1106-094) the inline tick ran its worker and found the port not answering, with the rows "
                                + "holding a figure before it",
                        sawFigure && module.TicksEnteredForSelfTest == 1 && module._scanning == 0
                        && !module._portAnswering && module._lastCheckedAt.HasValue);
                    probe.Check("(N-audit-1106-094) with VS Code not answering, the tray's session-limit row says it was not read instead of "
                                + "keeping the last figure (" + tray + ")",
                        tray == LimitNotAnsweringTray);
                    probe.Check("(N-audit-1106-094) ...and so do the Status card's limit row and the Session limit card's last reading ("
                                + values["aboutStatusLimit"] + " | " + values["aboutLimitNow"] + ")",
                        values["aboutStatusLimit"].StartsWith(LimitNotAnsweringLine, StringComparison.Ordinal)
                        && values["aboutLimitNow"] == LimitNotAnsweringLine
                        && values["aboutStatusLimit"].IndexOf("42 %", StringComparison.Ordinal) < 0);
                    probe.Check("(N-audit-1106-094) ...and the first tick that finds the port again reads the panels at once, not up to a "
                                + "minute later",
                        module._limitWatch.DueToRead(readAt.AddSeconds(1)));

                    // N-audit-1106-114, on the same inline tick: argv.json names a port a fake VS Code answers on. Inspected
                    // without the module's own port, the tick read that answer as "the port answers" while every sweep went
                    // to the cdpPort setting. With cdpPort the same port, it answers; with cdpPort another one, it does not.
                    using (var editor = new FakeCdpServer(new[]
                    {
                        new FakeCdpServer.Target { Id = "claude-1", Url = "vscode-webview://c?" + CdpApprover.ClaudeTargetMarker, EvaluateResult = "none" },
                    }))
                    {
                        string argv = System.IO.Path.Combine(root, "argv.json");
                        System.IO.File.WriteAllText(argv, "{\n\t\"" + VsCodeSetup.PortKey + "\": \""
                                                          + editor.Port.ToString(CultureInfo.InvariantCulture) + "\"\n}\n");
                        module._settings.Set(SettingArgvPath, argv);
                        module._settings.Set(SettingCdpPort, editor.Port.ToString(CultureInfo.InvariantCulture));
                        module.OnTick(null, EventArgs.Empty);
                        bool sameAnswers = module._portAnswering;
                        int elsewhere = editor.Port == 65535 ? 65534 : editor.Port + 1;
                        module._settings.Set(SettingCdpPort, elsewhere.ToString(CultureInfo.InvariantCulture));
                        module.OnTick(null, EventArgs.Empty);
                        SetupReport seen = module._setupCache;
                        probe.Check("WITNESS (N-audit-1106-114) the tick reads argv.json naming the module's own port, answering, as answering",
                            sameAnswers);
                        probe.Check("(N-audit-1106-114) the tick reads argv.json naming another port as not answering, though VS Code "
                                    + "answers there (" + (seen != null ? seen.State.ToString() : "<none>") + ")",
                            !module._portAnswering && seen != null && seen.State == SetupState.OtherPort);
                    }
                }
            }
            catch (Exception exception)
            {
                probe.Check("(N-audit-1106-094) the inline tick ran (" + exception.GetType().Name + ": " + exception.Message + ")", false);
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

        /// <summary>
        /// N-cxr-w34-05: a Watch switch toggled while a transcript scan is in flight. The tick snapshots the switches on
        /// the UI thread and its worker scans by them, but the callback's Apply read them again, so unticking Watch Codex
        /// between the worker and its callback put "1 Codex" and "Codex not counted" in one row, and ticking it on dropped
        /// the qualification from a scan that never read Codex. One instance runs OnTick INLINE with a UI context that
        /// HOLDS the posted callback (QueueingSyncContext), so the switch can be flipped between the worker and its UI
        /// half, as an Apply on the Options window would; then the held callback runs. A synthetic Codex rollout in a
        /// temporary folder, an empty Claude root, and argv.json pointing at a file that does not exist, so the port
        /// answers NotFound without a socket. Nothing real is read.
        /// </summary>
        private static bool SelfCheckWatchToggledMidScan(SelfTestProbe probe)
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-midscan-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            string empty = System.IO.Path.Combine(root, "none");
            string codex = System.IO.Path.Combine(root, "codex");
            string homeWas = Environment.GetEnvironmentVariable(RuleLoader.HomeVariable);
            string claudeWas = Environment.GetEnvironmentVariable(TranscriptReader.ClaudeRootVariable);
            string codexWas = Environment.GetEnvironmentVariable(TranscriptReader.CodexRootVariable);
            AgentFlowModule module = null;
            try
            {
                System.IO.Directory.CreateDirectory(empty);
                System.IO.Directory.CreateDirectory(codex);
                Environment.SetEnvironmentVariable(RuleLoader.HomeVariable, empty);
                Environment.SetEnvironmentVariable(TranscriptReader.ClaudeRootVariable, empty);
                Environment.SetEnvironmentVariable(TranscriptReader.CodexRootVariable, codex);
                string stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                System.IO.File.WriteAllText(System.IO.Path.Combine(codex, "rollout-2026-11-07T00-00-00-midscan01.jsonl"),
                    "{\"timestamp\":\"" + stamp + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"midscan01\"}}\n",
                    new System.Text.UTF8Encoding(false));

                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow"))
                {
                    host.UseStorage("agentflow", storage);
                    var seeded = host.SettingsFor("agentflow");
                    seeded.Set(SettingMode, AgentMode.Notify);
                    seeded.Set(SettingWatchClaude, "true");
                    seeded.Set(SettingWatchCodex, "true");
                    seeded.Set(SettingArgvPath, System.IO.Path.Combine(root, "no-argv-here", "argv.json"));
                    module = new AgentFlowModule();
                    module.Init(host);   // no UI context, so Init starts no tick of its own (RA-036)
                    long clock = 0;
                    module.TickClockForSelfTest = () => clock;
                    module.RunTickInlineForSelfTest = true;
                    var ui = new QueueingSyncContext();
                    module.SetUiContextForSelfTest(ui);

                    // Forward: the scan reads Codex, then Watch Codex goes off before the callback runs.
                    module.OnTick(null, EventArgs.Empty);
                    int held = ui.Held;
                    bool heldBack = held > 0 && module._sessionsLine == "";
                    module._settings.Set(SettingWatchCodex, "false");
                    ui.RunHeld();
                    string forwardRow = module._sessionsLine, forwardCodex = module._codexLine;
                    probe.Check("WITNESS (N-cxr-w34-05) the inline tick's UI half was held until the switch moved, and the scan it "
                                + "carried read the Codex session (" + held.ToString(CultureInfo.InvariantCulture) + " held, "
                                + module._lastSessions.ToString(CultureInfo.InvariantCulture) + " session)",
                        heldBack && module._lastSessions == 1 && module.TicksEnteredForSelfTest == 1 && module._scanning == 0
                        && !module.WatchCodex);
                    probe.Check("(N-cxr-w34-05) a scan that read Codex, applied after Watch Codex went off, counts it without saying "
                                + "Codex was not counted (" + forwardRow + " | " + forwardCodex + ")",
                        forwardRow.StartsWith("1 active in the last 15 minutes: 1 Codex (app not named).", StringComparison.Ordinal)
                        && forwardRow.IndexOf("not counted", StringComparison.Ordinal) < 0
                        && forwardCodex != "Not read: Watch Codex is off.");

                    // Reverse: the next transcript beat scans without Codex, then Watch Codex comes back on.
                    clock = 20000;
                    module.OnTick(null, EventArgs.Empty);
                    held = ui.Held;
                    module._settings.Set(SettingWatchCodex, "true");
                    ui.RunHeld();
                    string reverseRow = module._sessionsLine, reverseCodex = module._codexLine;
                    probe.Check("WITNESS (N-cxr-w34-05) the second inline tick's UI half was held too, and its scan read nothing ("
                                + held.ToString(CultureInfo.InvariantCulture) + " held, "
                                + module._lastSessions.ToString(CultureInfo.InvariantCulture) + " sessions)",
                        held > 0 && module._lastSessions == 0 && module.TicksEnteredForSelfTest == 2 && module.WatchCodex);
                    probe.Check("(N-cxr-w34-05) a scan that did not read Codex, applied after Watch Codex came on, still says Codex was "
                                + "not read, in both rows (" + reverseRow + " | " + reverseCodex + ")",
                        reverseRow == "None active in the last 15 minutes. Codex not counted: Watch Codex is off."
                        && reverseCodex == "Not read: Watch Codex is off.");
                }
            }
            catch (Exception exception)
            {
                probe.Check("(N-cxr-w34-05) the inline ticks ran (" + exception.GetType().Name + ": " + exception.Message + ")", false);
            }
            finally
            {
                if (module != null)
                {
                    module.RunTickInlineForSelfTest = false;
                    module.SetUiContextForSelfTest(null);
                    module.Shutdown();
                }
                Environment.SetEnvironmentVariable(RuleLoader.HomeVariable, homeWas);
                Environment.SetEnvironmentVariable(TranscriptReader.ClaudeRootVariable, claudeWas);
                Environment.SetEnvironmentVariable(TranscriptReader.CodexRootVariable, codexWas);
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
            return true;
        }

        /// <summary>A SynchronizationContext that HOLDS every posted callback until the test runs them, so a test can act
        /// between a worker's Post and the UI half it posted, as the user can on a real message loop.</summary>
        private sealed class QueueingSyncContext : System.Threading.SynchronizationContext
        {
            private readonly List<KeyValuePair<System.Threading.SendOrPostCallback, object>> _held =
                new List<KeyValuePair<System.Threading.SendOrPostCallback, object>>();

            internal int Held { get { return _held.Count; } }

            public override void Post(System.Threading.SendOrPostCallback d, object state)
            {
                _held.Add(new KeyValuePair<System.Threading.SendOrPostCallback, object>(d, state));
            }

            internal void RunHeld()
            {
                var now = _held.ToArray();
                _held.Clear();
                foreach (KeyValuePair<System.Threading.SendOrPostCallback, object> callback in now) callback.Key(callback.Value);
            }
        }
    }
}
