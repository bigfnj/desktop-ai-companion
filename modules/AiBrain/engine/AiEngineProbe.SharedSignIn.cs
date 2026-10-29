using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DesktopAICompanion.Ai;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.Modules;
using DesktopAICompanion.ModuleKit.Testing;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Lane feature/host-abi-180 (BACKLOG N-cli-token-02): AI Brain on the host's shared Claude Code sign-in. The module
    /// declares ModulePermissions.ClaudeCodeSignIn; on a host that answers IHostCredentials its runner reads, saves and
    /// removes through it, and Init subscribes to the host's CredentialChanged, which Shutdown takes off; on one that
    /// answers none (a test double), the runner keeps its own file. The runner's own migration and revision rules are in
    /// the shared self-check (SharedSignInSelfCheck). No CLI is started: the module's runner gets the fake process seam
    /// before anything can reach a CLI, and the brain's local slot points at a port nothing listens on.
    /// </summary>
    public static partial class AiEngineProbe
    {
        /// <summary>A RecordingHost that also answers IHostCredentials, by a FakeHostCredentials, as the module's own double
        /// (ModuleKit's RecordingHost stays as it is, so no module's payload changes for this).</summary>
        internal sealed class SharedSignInHost : RecordingHost, IHostCredentials
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

        private const string SharedSelfTestToken = "sk-ant-oat01-SELFTEST-aibrain-shared-0123456789";

        private static bool CheckSharedSignInLane(StringBuilder sb)
        {
            bool ok = true;
            ok &= Check(sb, "aibrain shared sign-in: the module declares ClaudeCodeSignIn, the flag the host's store is gated on",
                (new AiBrainModule().Info.Permissions & ModulePermissions.ClaudeCodeSignIn) == ModulePermissions.ClaudeCodeSignIn);

            Action<string> previousSink = AiBrain.LogSink;
            string previousRoot = AiPaths.CurrentRootForDiagnostics;
            var storage = new TempModuleStorage("aibrain-shared-signin");
            var scratch = new FakeCliScratch();
            var host = new SharedSignInHost();
            var module = new AiBrainModule();
            bool live = false;
            try
            {
                SeedOfflineModuleSettings(storage);
                host.UseStorage("aibrain", storage);
                host.Declared = module.Info.Permissions;
                module.Init(host);
                live = true;
                CodingAgentCli runner = module.CliRunnerForDiagnostics;
                var fake = new FakeCliProcess { Respond = FakeCliProcess.Answering("OK") };
                if (runner != null)
                {
                    runner.RunProcess = fake.RunAsync;
                    CliEnvironment environment = scratch.PathOnly;
                    runner.EnvironmentSource = delegate { return environment; };
                }
                ok &= Check(sb, "aibrain shared sign-in: on a host that answers IHostCredentials the runner keeps the token in the host's shared store, and Init subscribes to its changes",
                    runner != null && runner.SharedSignIn && module.SubscribedToSharedSignInForDiagnostics && host.Store.HasSubscribers);

                OptionsPane pane = host.OptionsPanes.Count == 1 ? host.OptionsPanes[0] : null;
                bool applied = pane != null && pane.Save(new Dictionary<string, string>(StringComparer.Ordinal) { { "cliToken", SharedSelfTestToken } });
                ok &= Check(sb, "aibrain shared sign-in: Apply saves a typed token into the shared store, and the card then says one is saved",
                    applied && host.Store.Value == SharedSelfTestToken && host.Store.Saves == 1 && pane.Load()["cliToken"] == "set");

                int notified = 0;
                Action counter = delegate { Interlocked.Increment(ref notified); };
                pane.InfoChanged += counter;
                try
                {
                    host.Store.Raise(host.Store.Revision);
                    int afterOwn = Volatile.Read(ref notified);
                    host.Store.SaveElsewhere("sk-ant-oat01-SELFTEST-aibrain-other-0123456789");
                    host.Store.Raise(host.Store.Revision);
                    int afterOther = Volatile.Read(ref notified);
                    ok &= Check(sb, "aibrain shared sign-in: a change another module made refreshes the open pane, and the event for this module's own save does not (" +
                        afterOwn + ", " + afterOther + ")",
                        afterOwn == 0 && afterOther == 1);
                }
                finally { pane.InfoChanged -= counter; }

                PaneAction remove = null;
                if (pane != null && pane.Actions != null)
                    foreach (PaneAction a in pane.Actions)
                        if (a != null && a.Label == "Remove token") remove = a;
                string removed = remove == null ? "(no button)" : remove.InvokeAsync().GetAwaiter().GetResult();
                ok &= Check(sb, "aibrain shared sign-in: Remove token removes the shared token and says it was the shared one: " + removed,
                    removed == CodingAgentCliText.SharedTokenRemovedSentence && host.Store.Removed && pane.Load()["cliToken"] == "");

                module.Shutdown();
                live = false;
                ok &= Check(sb, "aibrain shared sign-in: Shutdown takes the module's handler off the host's CredentialChanged, and leaves the shared store alone",
                    !host.Store.HasSubscribers && !module.SubscribedToSharedSignInForDiagnostics && host.Store.RemovesCommitted == 1);
            }
            finally
            {
                try { if (live) module.Shutdown(); } catch { }
                AiBrain.LogSink = previousSink;
                AiPaths.SwapRoot(previousRoot);
                try { storage.Dispose(); } catch { }
                scratch.Dispose();
            }

            // A host that answers no IHostCredentials (the test doubles the rest of this probe uses): the runner's own file.
            previousSink = AiBrain.LogSink;
            previousRoot = AiPaths.CurrentRootForDiagnostics;
            var plain = new AiBrainModule();
            var plainHost = new StandDownHost();
            var plainStorage = new TempModuleStorage("aibrain-shared-signin-plain");
            try
            {
                SeedOfflineModuleSettings(plainStorage);
                plainHost.UseStorage("aibrain", plainStorage);
                plainHost.Declared = plain.Info.Permissions;
                plain.Init(plainHost);
                ok &= Check(sb, "WITNESS aibrain shared sign-in: on a host that answers none the runner keeps its own file and nothing is subscribed",
                    plain.CliRunnerForDiagnostics != null && !plain.CliRunnerForDiagnostics.SharedSignIn && !plain.SubscribedToSharedSignInForDiagnostics);
            }
            finally
            {
                try { plain.Shutdown(); } catch { }
                AiBrain.LogSink = previousSink;
                AiPaths.SwapRoot(previousRoot);
                try { plainStorage.Dispose(); } catch { }
            }
            return ok;
        }
    }
}
