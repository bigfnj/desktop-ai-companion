namespace AgentBroker.ClaudeAdapter;

/// <summary>Pure policy when given an in-memory attribute provider; production disk probes remain protected.</summary>
public static class CredentialPathPolicy
{
    public static bool IsBoundedCredentialFile(FileAttributes attributes, uint linkCount, ulong byteLength)
        => linkCount == 1 && (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) == 0 && byteLength <= 4096;

    public static void RejectReparseComponents(string path, Func<string, FileAttributes> attributes)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Absolute credential target required.");
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
            if ((attributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse credential targets are refused.");
    }
}
