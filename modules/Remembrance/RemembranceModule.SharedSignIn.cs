using System;
using System.Collections.Generic;
using System.Threading;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Lane feature/host-abi-180 (BACKLOG N-cli-token-02): Remembrance on the host's shared Claude Code sign-in. The module
    /// declares ModulePermissions.ClaudeCodeSignIn; on a host that answers IHostCredentials its runner reads, saves and
    /// removes through it, and Init subscribes to the host's CredentialChanged, which Shutdown takes off. The runner's own
    /// migration and revision rules are in the shared self-check (SharedSignInSelfCheck), which this self-test runs too.
    /// No CLI is started (the runner gets the fake process seam before anything can reach one), nothing is recorded, and
    /// the storage location is a scratch folder, never the user's Documents.
    /// </summary>
    public sealed partial class RemembranceModule
    {
        /// <summary>A RecordingHost that also answers IHostCredentials, by a FakeHostCredentials (ModuleKit's RecordingHost
        /// stays as it is).</summary>
        private sealed class SharedSignInHost : DesktopAICompanion.ModuleKit.Testing.RecordingHost, IHostCredentials
        {
            internal readonly FakeHostCredentials Store = new FakeHostCredentials();

            public HostCredentialReading ReadCredential(string moduleId, HostCredentialId credential) { return Store.ReadCredential(moduleId, credential); }
            public HostCredentialCommit TrySaveCredential(string moduleId, HostCredentialId credential, string value) { return Store.TrySaveCredential(moduleId, credential, value); }
            public HostCredentialCommit ImportCredentialIfUnset(string moduleId, HostCredentialId credential, string value) { return Store.ImportCredentialIfUnset(moduleId, credential, value); }
            public HostCredentialCommit RemoveCredential(string moduleId, HostCredentialId credential) { return Store.RemoveCredential(moduleId, credential); }

            public event Action<HostCredentialId, long> CredentialChanged
            {
                add { Store.CredentialChanged += value; }
                remove { Store.CredentialChanged -= value; }
            }
        }

        private static void SelfCheckSharedSignIn(Action<string, bool> check)
        {
            check("remembrance shared sign-in: the module declares ClaudeCodeSignIn, the flag the host's store is gated on",
                (new RemembranceModule().Info.Permissions & ModulePermissions.ClaudeCodeSignIn) == ModulePermissions.ClaudeCodeSignIn);
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-sharedsignin-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            const string Token = "sk-ant-oat01-REMEMBRANCE-SHARED-SELFTEST-0123456789";
            try
            {
                System.IO.Directory.CreateDirectory(scratch);
                using (var cliScratch = new FakeCliScratch())
                {
                    var host = new SharedSignInHost();
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor(Id);
                    s.Set("storageLocation", System.IO.Path.Combine(scratch, "store"));
                    s.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    var module = new RemembranceModule();
                    module.Init(host);
                    bool live = true;
                    try
                    {
                        CodingAgentCli runner = module._cli;
                        var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("OK") };
                        if (runner != null)
                        {
                            runner.RunProcess = fake.RunAsync;
                            CliEnvironment environment = cliScratch.PathOnly;
                            runner.EnvironmentSource = delegate { return environment; };
                        }
                        check("remembrance shared sign-in: on a host that answers IHostCredentials the runner keeps the token in the host's shared store, and Init subscribes to its changes",
                            runner != null && runner.SharedSignIn && module.SubscribedToSharedSignInForDiagnostics && host.Store.HasSubscribers);

                        OptionsPane pane = host.OptionsPanes[0];
                        Dictionary<string, string> withToken = CopyOf(pane.Load());
                        withToken["cliToken"] = Token;
                        bool applied = pane.Save(withToken);
                        check("remembrance shared sign-in: Apply saves a typed token into the shared store, and the card then says one is saved",
                            applied && host.Store.Value == Token && host.Store.Saves == 1 && pane.Load()["cliToken"] == "set");

                        // BACKLOG N-audit-20261029-05: an Apply refused over the token says why on the Status line, which said
                        // it only in the log, and never the token; the next Apply clears it.
                        Dictionary<string, string> withRefused = CopyOf(pane.Load());
                        withRefused["cliToken"] = "sk-ant-api03-REMEMBRANCE-REFUSED-0123";
                        bool refusedApply = !pane.Save(withRefused);
                        string refusedStatus = pane.Load()["status"];
                        check("remembrance shared sign-in: an Apply refused over the sign-in token says why on the Status line, never the token: " + refusedStatus,
                            refusedApply && refusedStatus.Contains("✗ Not applied: That is an Anthropic API key") &&
                            !refusedStatus.Contains("REMEMBRANCE-REFUSED") && host.Store.Value == Token);
                        bool reapplied = pane.Save(CopyOf(pane.Load()));
                        check("remembrance shared sign-in: WITNESS the next Apply takes the refusal off the Status line",
                            reapplied && !pane.Load()["status"].Contains("✗ Not applied"));

                        int notified = 0;
                        Action counter = delegate { Interlocked.Increment(ref notified); };
                        pane.InfoChanged += counter;
                        int afterOwn, afterOther;
                        try
                        {
                            host.Store.Raise(host.Store.Revision);
                            afterOwn = Volatile.Read(ref notified);
                            host.Store.SaveElsewhere("sk-ant-oat01-REMEMBRANCE-SHARED-OTHER-0123456789");
                            host.Store.Raise(host.Store.Revision);
                            afterOther = Volatile.Read(ref notified);
                        }
                        finally { pane.InfoChanged -= counter; }
                        check("remembrance shared sign-in: a change another module made refreshes the open pane, and the event for this module's own save does not (" +
                              afterOwn + ", " + afterOther + ")",
                            afterOwn == 0 && afterOther == 1);

                        string removed = runner == null ? "(no runner)" : runner.RemoveClaudeToken();
                        check("remembrance shared sign-in: Remove token removes the shared token and says it was the shared one: " + removed,
                            removed == CodingAgentCliText.SharedTokenRemovedSentence && host.Store.Removed);

                        module.Shutdown();
                        live = false;
                        check("remembrance shared sign-in: Shutdown takes the module's handler off the host's CredentialChanged, and leaves the shared store alone",
                            !host.Store.HasSubscribers && !module.SubscribedToSharedSignInForDiagnostics && host.Store.RemovesCommitted == 1);
                    }
                    finally
                    {
                        if (live) { try { module.Shutdown(); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                check("remembrance shared sign-in: the check threw " + ex.GetType().Name + ": " + ex.Message, false);
            }
            finally
            {
                try { System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }
    }
}
