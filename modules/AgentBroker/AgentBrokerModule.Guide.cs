using System.Diagnostics;

namespace DesktopAICompanion.AgentBroker;

/// <summary>
/// PANE-TEXT (1.10.5): the offline AgentBroker guide. It ships inside AgentBroker.dll as an embedded resource, so it adds no
/// file to the release output, always matches the installed version and opens without a network. The action copies it into a
/// fixed subfolder of module storage, refuses a target outside storage or behind a reparse point, and asks the shell to open
/// that copy through an injectable starter (the shape of CodexDesktopChatLauncher). It uses the module's existing
/// LaunchProcess permission and needs no host change.
/// </summary>
public sealed partial class AgentBrokerModule
{
    internal const string GuideActionLabel = "Open the AgentBroker guide";
    internal const string GuideResourceName = "agentbroker-guide.html";
    internal const string GuideFolder = "guide";
    // The section ids the guide carries for the pane's summaries; GUIDE_EMBEDDED holds the compiled resource to each one.
    internal static readonly string[] GuideSections = ["setup", "clients", "wake", "pairing", "waiting-mail", "troubleshooting"];
    private const string GuideRefused = "The guide was not opened: its folder in module storage is a link or lies outside module storage.";
    // Tests replace the starter; the default asks the shell to open the copy and claims no more than that request.
    internal Func<string, bool> GuideOpener = OpenWithShell;
    private int guideOpenPending;

    internal static byte[]? GuideBytes() =>
        DesktopAICompanion.ModuleKit.EmbeddedResources.LoadBytes(typeof(AgentBrokerModule).Assembly, GuideResourceName);

    /// <summary>The guide copy's full path inside <paramref name="storage"/>, or null when <paramref name="folder"/> would
    /// place it anywhere else.</summary>
    internal static string? GuideTarget(string storage, string folder)
    {
        string root = Path.GetFullPath(storage);
        string target = Path.GetFullPath(Path.Combine(root, folder, GuideResourceName));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    // Storage itself and every existing component below it down to the target; a link anywhere means the write could land
    // outside storage, so the action refuses.
    private static bool GuidePathHasReparsePoint(string storage, string target)
    {
        string current = Path.GetFullPath(storage);
        if (IsReparsePoint(current)) return true;
        foreach (string part in Path.GetRelativePath(current, target).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (IsReparsePoint(current)) return true;
        }
        return false;

        static bool IsReparsePoint(string path) =>
            (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static bool OpenWithShell(string path)
    {
        // A running browser can take the request without returning a process handle.
        using Process? process = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true, Verb = "open", ErrorDialog = false });
        return true;
    }

    private Task<string> OpenGuideAsync()
    {
        if (shutDown) return Task.FromResult("AgentBroker has shut down. Reopen its options before opening the guide.");
        if (dataDirectory is null) return Task.FromResult("Module storage is unavailable, so the guide cannot be copied there.");
        byte[]? guide = GuideBytes();
        if (guide is null || guide.Length == 0) return Task.FromResult("This AgentBroker build carries no guide.");
        if (Interlocked.Exchange(ref guideOpenPending, 1) == 1) return Task.FromResult("The guide is already opening.");
        string storage = dataDirectory;
        Func<string, bool> opener = GuideOpener;
        return Task.Run(() =>
        {
            try { return CopyAndOpenGuide(storage, guide, opener); }
            finally { Volatile.Write(ref guideOpenPending, 0); }
        });
    }

    private static string CopyAndOpenGuide(string storage, byte[] guide, Func<string, bool> opener)
    {
        string? target = GuideTarget(storage, GuideFolder);
        if (target is null) return GuideRefused;
        try
        {
            if (GuidePathHasReparsePoint(storage, target)) return GuideRefused;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Checked again after the folder exists, so a link made in between is refused too.
            if (GuidePathHasReparsePoint(storage, target)) return GuideRefused;
            File.WriteAllBytes(target, guide);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "The guide could not be copied into module storage.";
        }
        string notOpened = "Windows could not open the guide. Its copy is at " + target;
        try { return opener(target) ? "Asked Windows to open the guide copy at " + target : notOpened; }
        catch (Exception) { return notOpened; }
    }
}
