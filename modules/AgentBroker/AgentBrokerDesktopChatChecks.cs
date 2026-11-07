using System.ComponentModel;
using System.Diagnostics;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    /// <summary>Documented-link and actual pane-action checks, with injected launchers. No app is opened.</summary>
    private static void RunDesktopChatChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string threadId = "12345678-abcd-4abc-9def-123456789abc";
        const string expectedLink = "codex://threads/" + threadId;
        check(CodexDesktopChatLink.TryCreate(threadId, CodexChatOrigin.Desktop, out var link) &&
            link.Uri.OriginalString == expectedLink && link.Uri.Query.Length == 0 && link.Uri.Fragment.Length == 0,
            "DESKTOP_CHAT_CANONICAL_LINK: an explicit desktop origin and canonical UUID create only the documented existing-chat link");
        foreach (CodexChatOrigin origin in new[] { CodexChatOrigin.Unspecified, CodexChatOrigin.VsCode, (CodexChatOrigin)99 })
            check(!CodexDesktopChatLink.TryCreate(threadId, origin, out _),
                "DESKTOP_CHAT_ORIGIN_REQUIRED: another or unknown original app cannot create a desktop link: " + origin);
        foreach (string? invalid in new string?[] { null, "", "00000000-0000-0000-0000-000000000000", threadId.ToUpperInvariant(),
            " " + threadId, threadId + " ", "{" + threadId + "}", threadId.Replace("-", "", StringComparison.Ordinal),
            expectedLink, threadId + "?prompt=send", threadId + "#fragment", threadId + "\n", "../" + threadId,
            "%31" + threadId[1..], "12345678-abcd-4abc-9def-123456789abg" })
            check(!CodexDesktopChatLink.TryCreate(invalid, CodexChatOrigin.Desktop, out _),
                "DESKTOP_CHAT_UUID_REQUIRED: a missing, noncanonical, empty or decorated ID is rejected before a URI can be opened");
        if (link is null) return;
        ProcessStartInfo start = CodexDesktopChatLauncher.StartInfo(link);
        check(start.FileName == expectedLink && start.UseShellExecute && start.Verb == "open" &&
            start.Arguments.Length == 0 && start.ArgumentList.Count == 0 && !start.ErrorDialog,
            "DESKTOP_CHAT_FIXED_ROUTE: shell activation receives the validated URI only, without a prompt or CLI arguments");
        var opened = new List<ProcessStartInfo>();
        var realLauncher = new CodexDesktopChatLauncher(info => { opened.Add(info); return null; });
        check(realLauncher.OpenAsync(link).GetAwaiter().GetResult() && opened.Count == 1 &&
            opened[0].FileName == expectedLink,
            "DESKTOP_CHAT_EXISTING_PROCESS: successful OS activation can return no process handle");
        var denied = new CodexDesktopChatLauncher(_ => throw new Win32Exception(2, "SYNTHETIC_DESKTOP_PRIVATE_DIAGNOSTIC"));
        check(!denied.OpenAsync(link).GetAwaiter().GetResult(),
            "DESKTOP_CHAT_LAUNCH_FAILURE: an OS activation failure is reported as a failure");

        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-desktop-chat-synthetic")));
        var control = new TestControl();
        var supervisor = new TestSupervisor();
        var launcher = new TestDesktopChatLauncher();
        var module = new AgentBrokerModule(control, supervisor) { DesktopChatLauncher = launcher };
        module.Init(host);
        try
        {
            OptionsPane pane = host.OptionsPanes.Single();
            PaneAction action = pane.Actions.Single(item => item.Label == "Open existing desktop chat");
            Task<string> Invoke(string? origin, string? id)
            {
                var values = new Dictionary<string, string>();
                if (origin is not null) values["desktopChatOrigin"] = origin;
                if (id is not null) values["desktopChatThreadId"] = id;
                return (action.InvokeWithPendingAsync ?? throw new InvalidOperationException("Desktop action pending delegate missing."))(values);
            }
            string Complete(Task<string> task) { Pump(() => task.IsCompleted, dispatcher); return task.GetAwaiter().GetResult(); }

            var defaults = pane.Load();
            check(defaults["desktopChatOrigin"] == DesktopChatOriginPrompt && defaults["desktopChatThreadId"] == "" &&
                pane.Schema.Single(field => field.Id == "desktopChatOrigin") is { Kind: SettingKind.Enum } origins &&
                origins.Options.SequenceEqual(new[] { DesktopChatOriginPrompt, DesktopChatOrigin, DesktopChatVsCodeOrigin }) &&
                pane.Schema.Single(field => field.Id == "desktopChatThreadId").EnabledWhen == "desktopChatOrigin=" + DesktopChatOrigin &&
                action.InvokeWithPendingAsync is not null && !action.ReloadPaneAfter,
                "DESKTOP_CHAT_PANE_SELECTION: the pane starts unselected and hands temporary on-screen origin and ID values to the action");
            foreach (string? origin in new[] { null, DesktopChatOriginPrompt, DesktopChatVsCodeOrigin, "desktop app" })
                check(Complete(Invoke(origin, threadId)).Contains("original VS Code window", StringComparison.Ordinal) && launcher.Links.Count == 0,
                    "DESKTOP_CHAT_PANE_ORIGIN: the action never infers desktop origin from a valid Codex UUID");
            check(Complete(Invoke(DesktopChatOrigin, expectedLink)).Contains("lowercase UUID", StringComparison.Ordinal) &&
                Complete(Invoke(DesktopChatOrigin, null)).Contains("lowercase UUID", StringComparison.Ordinal) && launcher.Links.Count == 0,
                "DESKTOP_CHAT_PANE_VALIDATION: malformed and missing input never reaches the launcher");
            string result = Complete(Invoke(DesktopChatOrigin, threadId));
            check(launcher.Links.Count == 1 && launcher.Links[0].Uri.OriginalString == expectedLink &&
                result.StartsWith("Windows accepted the request", StringComparison.Ordinal) &&
                result.Contains("Check that the existing chat loaded", StringComparison.Ordinal) &&
                result.Contains("reachability is checked separately", StringComparison.Ordinal) &&
                control.Requests.Count == 0 && supervisor.Launches.Count == 0 &&
                pane.Load()["codexPeerMessages"] == "false",
                "DESKTOP_CHAT_OPEN_IS_NOT_DELIVERY: the actual pane action opens only the selected chat and claims request acceptance without changing broker delivery");
            check(pane.Save(new Dictionary<string, string> { ["desktopChatOrigin"] = DesktopChatOrigin, ["desktopChatThreadId"] = threadId }) &&
                pane.Load()["desktopChatOrigin"] == DesktopChatOriginPrompt && pane.Load()["desktopChatThreadId"] == "" &&
                host.SettingsFor(Id).Get("desktopChatThreadId", "absent") == "absent" &&
                host.SettingsFor(Id).Get("desktopChatOrigin", "absent") == "absent",
                "DESKTOP_CHAT_TEMPORARY_INPUTS: applying settings never stores the thread ID or original-app selection");
            launcher.Succeed = false;
            check(Complete(Invoke(DesktopChatOrigin, threadId)).StartsWith("Windows could not request", StringComparison.Ordinal),
                "DESKTOP_CHAT_PANE_FAILED_REQUEST: a failed launcher never claims the chat opened");
            launcher.Failure = new InvalidOperationException("SYNTHETIC_DESKTOP_PRIVATE_DIAGNOSTIC");
            result = Complete(Invoke(DesktopChatOrigin, threadId));
            check(result.StartsWith("Windows could not request", StringComparison.Ordinal) &&
                !result.Contains("SYNTHETIC_DESKTOP_PRIVATE_DIAGNOSTIC", StringComparison.Ordinal) && !result.Contains(threadId, StringComparison.Ordinal),
                "DESKTOP_CHAT_FIXED_FAILURE: unexpected launcher diagnostics and the supplied ID are not disclosed");
            launcher.Failure = null; launcher.Succeed = true;
            launcher.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int beforePending = launcher.Links.Count;
            Task<string> first = Invoke(DesktopChatOrigin, threadId);
            check(!first.IsCompleted && Complete(Invoke(DesktopChatOrigin, threadId)).Contains("already running", StringComparison.Ordinal) &&
                launcher.Links.Count == beforePending + 1,
                "DESKTOP_CHAT_SINGLE_REQUEST: a second click cannot duplicate a pending OS open request");
            launcher.Completion.SetResult(true);
            Complete(first);
            launcher.Completion = null;
            Complete(Invoke(DesktopChatOrigin, threadId));
            check(launcher.Links.Count == beforePending + 2,
                "DESKTOP_CHAT_RETRY_AFTER_COMPLETION: a later explicit click can try again after the prior request finishes");
            launcher.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int beforeQueuedShutdown = launcher.Links.Count;
            Task<string> queuedAtShutdown = Invoke(DesktopChatOrigin, threadId);
            module.Shutdown();
            check(!queuedAtShutdown.IsCompleted && launcher.Links.Count == beforeQueuedShutdown + 1,
                "DESKTOP_CHAT_SHUTDOWN_IN_FLIGHT: shutdown does not create or repeat an activation the user already requested");
            int beforeShutdown = launcher.Links.Count;
            check(Complete(Invoke(DesktopChatOrigin, threadId)).Contains("shut down", StringComparison.Ordinal) &&
                launcher.Links.Count == beforeShutdown,
                "DESKTOP_CHAT_SHUTDOWN: an obsolete options action cannot open a chat after module shutdown");
            launcher.Completion.SetResult(true);
            result = Complete(queuedAtShutdown);
            check(result.StartsWith("Windows accepted the request", StringComparison.Ordinal) &&
                launcher.Links.Count == beforeQueuedShutdown + 1 && control.Requests.Count == 0 && supervisor.Launches.Count == 0,
                "DESKTOP_CHAT_SHUTDOWN_COMPLETION: an existing activation can finish after shutdown without another activation or broker work");
        }
        finally { module.Shutdown(); }
    }

    private sealed class TestDesktopChatLauncher : ICodexDesktopChatLauncher
    {
        internal List<CodexDesktopChatLink> Links { get; } = [];
        internal bool Succeed = true;
        internal Exception? Failure;
        internal TaskCompletionSource<bool>? Completion;

        public Task<bool> OpenAsync(CodexDesktopChatLink link)
        {
            Links.Add(link);
            return Failure is not null ? Task.FromException<bool>(Failure) : Completion?.Task ?? Task.FromResult(Succeed);
        }
    }
}
