using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DesktopAICompanion.AgentBroker;

internal enum CodexChatOrigin { Unspecified, Desktop, VsCode }

/// <summary>A documented desktop-chat link, constructed only from an explicit origin and canonical ID.</summary>
internal sealed class CodexDesktopChatLink
{
    private CodexDesktopChatLink(Guid threadId) => Uri = new Uri("codex://threads/" + threadId.ToString("D"));

    internal Uri Uri { get; }

    internal static bool TryCreate(string? threadId, CodexChatOrigin origin,
        [NotNullWhen(true)] out CodexDesktopChatLink? link)
    {
        link = null;
        if (origin != CodexChatOrigin.Desktop) return false;
        Guid parsed = Guid.Empty;
        bool valid = threadId is { Length: 36 } && Guid.TryParseExact(threadId, "D", out parsed) &&
            parsed != Guid.Empty && threadId == parsed.ToString("D");
        if (!valid) return false;
        link = new(parsed);
        return true;
    }
}

internal interface ICodexDesktopChatLauncher
{
    Task<bool> OpenAsync(CodexDesktopChatLink link);
}

/// <summary>Requests OS URI activation only. It never submits input or starts a Codex engine itself.</summary>
internal sealed class CodexDesktopChatLauncher(Func<ProcessStartInfo, Process?>? start = null) : ICodexDesktopChatLauncher
{
    private readonly Func<ProcessStartInfo, Process?> startProcess = start ?? Process.Start;

    internal static ProcessStartInfo StartInfo(CodexDesktopChatLink link) => new()
    {
        FileName = link.Uri.AbsoluteUri,
        UseShellExecute = true,
        Verb = "open",
        ErrorDialog = false
    };

    public Task<bool> OpenAsync(CodexDesktopChatLink link) => Task.Run(() =>
    {
        try
        {
            // Activating an existing app can succeed without returning a process handle.
            using Process? process = startProcess(StartInfo(link));
            return true;
        }
        catch (Exception) { return false; }
    });
}
