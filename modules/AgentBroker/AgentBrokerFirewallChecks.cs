using System.Diagnostics;
using AgentBroker.Firewall;
using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    /// <summary>Real consent and lifecycle guards, with a fake host and launcher. No dialog, child or firewall access.</summary>
    private static void RunFirewallChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        check(LanFirewallRequest.TryCreate(@"C:\synthetic module\AgentBroker.dll", 17433, out var request) &&
            request.Executable == @"C:\synthetic module\worker\AgentBroker.Worker.exe" && request.Port == 17433,
            "firewall: worker scope is the exact sibling payload and configured port");
        foreach (var invalid in new[] { ("AgentBroker.dll", 7433), (@"C:\synthetic\other.dll", 7433),
            (@"C:\synthetic\AgentBroker.dll", 0), (@"C:\synthetic\AgentBroker.dll", 65536) })
            check(!LanFirewallRequest.TryCreate(invalid.Item1, invalid.Item2, out _),
                "firewall: non-module or invalid port cannot create a launch request");
        var start = LanFirewallLauncher.StartInfo(request);
        check(start.FileName == request.Executable && start.UseShellExecute && start.Verb == "runas" &&
            start.WindowStyle == ProcessWindowStyle.Hidden && !start.ErrorDialog &&
            start.Arguments == "--allow-lan-access --port 17433" && start.ArgumentList.Count == 0 &&
            start.WorkingDirectory == Path.GetDirectoryName(request.Executable),
            "firewall: elevation launches only the packaged executable with fixed typed port arguments");
        var realLauncher = new LanFirewallLauncher();
        check(realLauncher.AllowAsync(new(@"C:\synthetic\worker\AgentBroker.Worker.exe", 7433), CancellationToken.None)
            .GetAwaiter().GetResult().Outcome == LanFirewallLaunchOutcome.InvalidScope,
            "firewall: another installation's worker is rejected before process or filesystem access");
        RunFirewallLauncherChecksAsync(check).GetAwaiter().GetResult();

        var fixtures = new List<(AgentBrokerModule Module, TestHost Host, TestFirewallLauncher Launcher, OptionsPane Pane)>();
        (AgentBrokerModule Module, TestHost Host, TestFirewallLauncher Launcher, OptionsPane Pane) Create(bool enabled = true)
        {
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-firewall-synthetic")));
            host.SettingsFor(Id).Set("enabled", enabled ? "true" : "false");
            host.SettingsFor(Id).Set("lanPort", "17433");
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.0.2.10:17433",
                [], [], [], [], new Dictionary<string, string>());
            var module = new AgentBrokerModule(new TestControl(), new StatusSupervisor(view));
            var launcher = new TestFirewallLauncher(); module.FirewallLauncher = launcher;
            module.Init(host);
            if (enabled) Pump(() => module.snapshot is not null, dispatcher);
            else Pump(() => module.pending.IsCompleted, dispatcher);
            var result = (module, host, launcher, host.OptionsPanes.Single()); fixtures.Add(result);
            return result;
        }
        string Complete(Task<string> action)
        {
            Pump(() => action.IsCompleted, dispatcher);
            return action.GetAwaiter().GetResult();
        }
        void Answer(TestHost host, TrayQuestionOutcome outcome, string choice = "allow") =>
            host.Asks.Last().Completion.SetResult(new TrayAnswer { Outcome = outcome, ChoiceId = choice });
        try
        {
            var disabled = Create(false);
            string text = Complete(disabled.Module.AllowLanAccessAsync());
            check(disabled.Host.Asks.Count == 0 && disabled.Launcher.Requests.Count == 0 && text.Contains("Start", StringComparison.Ordinal),
                "firewall: disabled broker never asks or elevates");

            var scope = Create();
            check(scope.Host.Asks.Count == 0 && scope.Launcher.Requests.Count == 0 &&
                scope.Pane.Actions.Count(a => a.Label == "Allow LAN access") == 1 &&
                scope.Host.TrayItems.Single().BuildChildren().Count(a => a.Label == "Allow LAN access") == 1 &&
                scope.Pane.Load()["lanAccess"].Contains("Private", StringComparison.Ordinal),
                "firewall: init and polling add explicit settings/tray actions without automatic consent or elevation");
            var standby = Create();
            standby.Module.ApplyStatus(new(true, false, "standby", standby.Module.snapshot!));
            var standbyAction = standby.Module.AllowLanAccessAsync();
            check(standby.Host.Asks.Count == 0 && standby.Launcher.Requests.Count == 0,
                "firewall: standby never opens a different installation's worker");
            if (standby.Host.Asks.Count != 0) Answer(standby.Host, TrayQuestionOutcome.Chosen, "cancel");
            Complete(standbyAction);
            var mismatch = Create();
            mismatch.Module.ApplyStatus(new(true, true, "running", mismatch.Module.snapshot! with { LanEndpoint = "https://192.0.2.10:17434" }));
            var mismatchAction = mismatch.Module.AllowLanAccessAsync();
            check(mismatch.Host.Asks.Count == 0 && mismatch.Launcher.Requests.Count == 0,
                "firewall: live endpoint must match the applied LAN port before consent");
            if (mismatch.Host.Asks.Count != 0) Answer(mismatch.Host, TrayQuestionOutcome.Chosen, "cancel");
            Complete(mismatchAction);
            var action = scope.Module.AllowLanAccessAsync();
            TrayQuestion question = scope.Host.Asks.Single().Question;
            text = Complete(scope.Module.AllowLanAccessAsync());
            check(scope.Host.Asks.Count == 1 && scope.Launcher.Requests.Count == 0 && text.Contains("already", StringComparison.Ordinal) &&
                question.Text.Contains("17433", StringComparison.Ordinal) && question.Text.Contains("Private", StringComparison.Ordinal) &&
                question.Text.Contains("local subnet", StringComparison.Ordinal) && question.Text.Contains("administrator approval", StringComparison.Ordinal) &&
                question.Text.Contains("adds no Public-network rule", StringComparison.Ordinal) &&
                question.Text.Length <= 240 && question.TimeoutChoiceId == "cancel" && !question.RunsWhileHeld,
                "firewall: one bounded consent card explains exact applied scope and the separate Windows approval");
            Answer(scope.Host, TrayQuestionOutcome.Chosen, "cancel");
            check(Complete(action).Contains("cancelled", StringComparison.Ordinal) && scope.Launcher.Requests.Count == 0,
                "firewall: explicit cancel never launches elevation");
            foreach (var outcome in new[] { TrayQuestionOutcome.TimedOut, TrayQuestionOutcome.Expired,
                TrayQuestionOutcome.Withdrawn, TrayQuestionOutcome.Cancelled, TrayQuestionOutcome.Refused })
            {
                action = scope.Module.AllowLanAccessAsync(); Answer(scope.Host, outcome);
                Complete(action);
                check(scope.Launcher.Requests.Count == 0, "firewall: only a chosen allow answer can elevate, never " + outcome);
            }
            scope.Host.ThrowNextAsk = true;
            text = Complete(scope.Module.AllowLanAccessAsync());
            check(scope.Launcher.Requests.Count == 0 && text.Contains("unavailable", StringComparison.Ordinal) &&
                !text.Contains("Synthetic", StringComparison.Ordinal), "firewall: question failure reports fixed text without elevation or diagnostics");

            action = scope.Pane.Actions.Single(a => a.Label == "Allow LAN access").InvokeAsync();
            Answer(scope.Host, TrayQuestionOutcome.Chosen);
            text = Complete(action);
            check(scope.Launcher.Requests.Count == 1 && scope.Launcher.Requests[0].Port == 17433 &&
                scope.Launcher.Requests[0].Executable == Path.Combine(Path.GetDirectoryName(typeof(AgentBrokerModule).Assembly.Location)!,
                    "worker", "AgentBroker.Worker.exe") && text.Contains("configured on this workstation", StringComparison.Ordinal) &&
                text.Contains("both PCs", StringComparison.Ordinal) && text.Contains("pairing remain unconfirmed", StringComparison.Ordinal),
                "firewall: chosen allow launches exact worker/port once and distinguishes local rule success from pairing");

            foreach (var failure in new[] {
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.Cancelled), "cancelled"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.InvalidScope), "redirected"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.TimedOut), "unconfirmed"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.Unavailable), "unavailable"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.AlreadyPending), "still pending"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.Exited, (int)FirewallSetupExitCode.AdministratorRequired), "required"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.Exited, (int)FirewallSetupExitCode.PolicyRestricted), "policy"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.Exited, (int)FirewallSetupExitCode.ConflictingRule), "conflicting"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.Exited, (int)FirewallSetupExitCode.VerificationFailed), "not verify"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.Exited, (int)FirewallSetupExitCode.ExistingBlock), "existing Windows Firewall block"),
                (new LanFirewallLaunchResult(LanFirewallLaunchOutcome.Exited, 999), "not confirm") })
            {
                scope.Launcher.Result = failure.Item1;
                action = scope.Module.AllowLanAccessAsync(); Answer(scope.Host, TrayQuestionOutcome.Chosen);
                text = Complete(action);
                check(text.Contains(failure.Item2, StringComparison.Ordinal) && !text.Contains("configured on this workstation", StringComparison.Ordinal),
                    "firewall: non-success result is truthful for " + failure.Item1);
            }
            scope.Launcher.Throw = true;
            action = scope.Module.AllowLanAccessAsync(); Answer(scope.Host, TrayQuestionOutcome.Chosen);
            text = Complete(action);
            check(text.Contains("unavailable", StringComparison.Ordinal) && !text.Contains("SYNTHETIC_PRIVATE", StringComparison.Ordinal),
                "firewall: launcher failure never discloses raw exception diagnostics");

            var stillPending = Create(); stillPending.Launcher.PendingState = true;
            text = Complete(stillPending.Module.AllowLanAccessAsync());
            check(stillPending.Host.Asks.Count == 0 && stillPending.Launcher.Requests.Count == 0 &&
                text.Contains("still pending", StringComparison.Ordinal),
                "firewall: an outstanding Windows operation does not open another consent card or UAC request");

            var changed = Create(); changed.Host.IgnoreQuestionCancellation = true;
            action = changed.Module.AllowLanAccessAsync();
            changed.Pane.Save(new Dictionary<string, string> { ["lanPort"] = "17434" });
            Answer(changed.Host, TrayQuestionOutcome.Chosen);
            text = Complete(action);
            check(changed.Launcher.Requests.Count == 0 && text.Contains("changed", StringComparison.Ordinal),
                "firewall: a late allow after settings generation changes cannot launch elevation");

            var stopped = Create(); stopped.Host.IgnoreQuestionCancellation = true;
            action = stopped.Module.AllowLanAccessAsync(); stopped.Module.Shutdown();
            Answer(stopped.Host, TrayQuestionOutcome.Chosen);
            text = Complete(action);
            check(stopped.Launcher.Requests.Count == 0 && text.Contains("changed", StringComparison.Ordinal),
                "firewall: a chosen answer racing shutdown cannot launch elevation");

            var late = Create();
            late.Launcher.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            action = late.Module.AllowLanAccessAsync(); Answer(late.Host, TrayQuestionOutcome.Chosen);
            Pump(() => late.Launcher.Requests.Count == 1, dispatcher);
            int notifications = 0; late.Pane.InfoChanged += () => notifications++;
            late.Module.Shutdown(); int before = notifications;
            late.Launcher.Pending.SetResult(new(LanFirewallLaunchOutcome.Exited, 0));
            text = Complete(action);
            check(notifications == before && text.Contains("changed", StringComparison.Ordinal) && text.Contains("unconfirmed", StringComparison.Ordinal),
                "firewall: an already-started Windows operation cannot restore stale UI or claim new pairing after shutdown");

            var cancelled = Create();
            cancelled.Launcher.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancelled.Launcher.ObserveCancellation = true;
            action = cancelled.Module.AllowLanAccessAsync(); Answer(cancelled.Host, TrayQuestionOutcome.Chosen);
            Pump(() => cancelled.Launcher.Requests.Count == 1, dispatcher);
            cancelled.Module.Shutdown();
            text = Complete(action);
            check(text.Contains("unconfirmed", StringComparison.Ordinal) && !text.Contains("cancelled", StringComparison.Ordinal) &&
                !text.Contains("No firewall rule", StringComparison.Ordinal),
                "firewall: cancellation after UAC submission never promises that Windows made no change");
            cancelled.Launcher.Pending.SetResult(new(LanFirewallLaunchOutcome.Exited, 0));
        }
        finally
        {
            foreach (var fixture in fixtures) fixture.Module.Shutdown();
            Pump(() => fixtures.All(f => f.Module.pending.IsCompleted), dispatcher);
        }
    }

    private sealed class TestFirewallLauncher : ILanFirewallLauncher
    {
        internal readonly List<LanFirewallRequest> Requests = [];
        internal LanFirewallLaunchResult Result = new(LanFirewallLaunchOutcome.Exited, 0);
        internal TaskCompletionSource<LanFirewallLaunchResult>? Pending;
        internal bool Throw;
        internal bool ObserveCancellation;
        internal bool PendingState;
        public bool IsPending => PendingState;
        public Task<LanFirewallLaunchResult> AllowAsync(LanFirewallRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request);
            if (Throw) throw new IOException("SYNTHETIC_PRIVATE_FIREWALL_DIAGNOSTIC");
            return Pending is null ? Task.FromResult(Result) : ObserveCancellation ? Pending.Task.WaitAsync(token) : Pending.Task;
        }
    }

    private static async Task RunFirewallLauncherChecksAsync(Action<bool, string> check)
    {
        if (!LanFirewallRequest.TryCreate(typeof(AgentBrokerModule).Assembly.Location, 17433, out var request))
            throw new InvalidOperationException("Synthetic launcher request scope could not be constructed.");
        async Task WaitForExit(LanFirewallLauncher launcher)
        {
            var watch = Stopwatch.StartNew();
            while (launcher.IsPending && watch.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(1).ConfigureAwait(false);
            if (launcher.IsPending) throw new TimeoutException("Synthetic elevated child did not complete.");
        }
        var runner = new TestFirewallElevationRunner();
        var launcher = new LanFirewallLauncher(runner, TimeSpan.FromMilliseconds(15), _ => true);
        var result = await launcher.AllowAsync(request, CancellationToken.None).ConfigureAwait(false);
        check(result.Outcome == LanFirewallLaunchOutcome.TimedOut && launcher.IsPending && runner.Calls == 1,
            "firewall: caller timeout retains the pending UAC or elevated child lifetime");
        result = await launcher.AllowAsync(request, CancellationToken.None).ConfigureAwait(false);
        bool timeoutRetryGuarded = result.Outcome == LanFirewallLaunchOutcome.AlreadyPending && runner.Calls == 1;
        int previousCalls = runner.Calls;
        runner.Pending.SetResult(new(LanFirewallLaunchOutcome.Exited, 0));
        await WaitForExit(launcher).ConfigureAwait(false);
        runner.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Pending.SetResult(new(LanFirewallLaunchOutcome.Exited, 0));
        result = await launcher.AllowAsync(request, CancellationToken.None).ConfigureAwait(false);
        check(result.Outcome == LanFirewallLaunchOutcome.Exited && result.ExitCode == 0 && !launcher.IsPending && runner.Calls == previousCalls + 1,
            "firewall: only actual child completion permits a later explicitly requested elevation");

        runner = new TestFirewallElevationRunner();
        launcher = new LanFirewallLauncher(runner, TimeSpan.FromMilliseconds(15), _ => true);
        using var cancelled = new CancellationTokenSource();
        var action = launcher.AllowAsync(request, cancelled.Token); cancelled.Cancel();
        bool observedCancellation = false;
        try { await action.ConfigureAwait(false); }
        catch (OperationCanceledException) { observedCancellation = true; }
        check(observedCancellation && launcher.IsPending && runner.Calls == 1,
            "firewall: caller cancellation does not discard an already-submitted UAC operation");
        result = await launcher.AllowAsync(request, CancellationToken.None).ConfigureAwait(false);
        check(timeoutRetryGuarded && result.Outcome == LanFirewallLaunchOutcome.AlreadyPending && runner.Calls == 1,
            "firewall: retries after caller timeout or cancellation cannot submit a second elevation");
        previousCalls = runner.Calls;
        runner.Pending.SetResult(new(LanFirewallLaunchOutcome.Cancelled));
        await WaitForExit(launcher).ConfigureAwait(false);
        bool preCancelled = false;
        try { await launcher.AllowAsync(request, cancelled.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { preCancelled = true; }
        check(preCancelled && runner.Calls == previousCalls && !launcher.IsPending,
            "firewall: an already-cancelled caller never starts a new elevation");
    }

    private sealed class TestFirewallElevationRunner : ILanFirewallElevationRunner
    {
        internal int Calls;
        internal TaskCompletionSource<LanFirewallLaunchResult> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LanFirewallLaunchResult> RunAsync(LanFirewallRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Pending.Task;
        }
    }
}
