namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    internal const string DesktopChatOriginPrompt = "Choose the original app";
    internal const string DesktopChatOrigin = "Desktop app";
    internal const string DesktopChatVsCodeOrigin = "VS Code";
    internal ICodexDesktopChatLauncher DesktopChatLauncher = new CodexDesktopChatLauncher();
    private bool desktopChatOpenPending;

    private async Task<string> OpenCodexDesktopChatAsync(IReadOnlyDictionary<string, string> values)
    {
        if (shutDown) return "AgentBroker has shut down. Reopen its options before opening a chat.";
        if (desktopChatOpenPending) return "A desktop chat open request is already running.";
        if (!values.TryGetValue("desktopChatOrigin", out string? origin) || origin != DesktopChatOrigin)
            return "Select Desktop app only for a chat that belongs there. Open VS Code chats in their original VS Code window.";
        values.TryGetValue("desktopChatThreadId", out string? threadId);
        if (!CodexDesktopChatLink.TryCreate(threadId, CodexChatOrigin.Desktop, out var link))
            return "Enter the thread ID from the desktop chat link: a lowercase UUID with hyphens.";
        desktopChatOpenPending = true;
        try
        {
            // An already requested activation may finish after shutdown; entry above refuses new invocations.
            bool requested = await DesktopChatLauncher.OpenAsync(link);
            return requested
                ? "Windows accepted the request to open this desktop chat. Check that the existing chat loaded. Broker reachability is checked separately."
                : "Windows could not request the desktop chat. Open the desktop app and try again.";
        }
        catch (Exception)
        {
            // A launcher failure is shown without exposing OS diagnostics or the supplied thread ID.
            return "Windows could not request the desktop chat. Open the desktop app and try again.";
        }
        finally { desktopChatOpenPending = false; }
    }
}
