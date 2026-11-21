namespace AgentBroker.TestSupport;

// Invocation-owned test data only. Never enumerate or remove another invocation's scratch directory.
internal sealed class OwnedScratch : IDisposable
{
    private readonly string parent;
    private bool disposed;
    internal string Root { get; }

    private OwnedScratch(string prefix)
    {
        if (prefix.Length == 0 || prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("A plain scratch prefix is required.");
        parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch"));
        Directory.CreateDirectory(parent);
        Plain(parent);
        Root = Path.Combine(parent, prefix + "-" + Guid.NewGuid().ToString("N"));
        if (Path.Exists(Root)) throw new IOException("The owned scratch path already exists.");
        Directory.CreateDirectory(Root);
    }

    internal static OwnedScratch Create(string prefix) => new(prefix);

    public void Dispose()
    {
        if (disposed) return;
        string resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Owned scratch cleanup escaped its parent.");
        Plain(parent);
        try { Plain(resolved); }
        catch (FileNotFoundException) { disposed = true; return; }
        catch (DirectoryNotFoundException) { disposed = true; return; }
        var files = new List<string>(); var directories = new List<string>();
        Collect(resolved);
        // Preflight the complete tree before deleting anything. Recheck every path before its own mutation.
        foreach (string file in files) { CheckParents(file); Plain(file); File.Delete(file); }
        foreach (string directory in directories.AsEnumerable().Reverse())
        { CheckParents(directory); Plain(directory); Directory.Delete(directory); }
        if (Path.Exists(resolved)) throw new IOException("Owned scratch cleanup left its root behind.");
        disposed = true;

        void Collect(string directory)
        {
            Plain(directory); directories.Add(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = Plain(entry);
                if ((attributes & FileAttributes.Directory) != 0) Collect(entry);
                else files.Add(entry);
            }
        }
        void CheckParents(string path)
        {
            string? current = Path.GetDirectoryName(path);
            while (current is not null && current.Length >= resolved.Length)
            { Plain(current); if (current == resolved) break; current = Path.GetDirectoryName(current); }
        }
    }

    private static FileAttributes Plain(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Owned scratch cleanup refuses linked paths.");
        return attributes;
    }
}

internal sealed class ScratchAudit : IDisposable
{
    internal bool PrimaryFailureReported { get; set; }
    private readonly string parent = Path.Combine(AppContext.BaseDirectory, "scratch");
    private readonly HashSet<string> before;
    internal ScratchAudit() => before = Entries();
    private HashSet<string> Entries() => Directory.Exists(parent)
        ? Directory.GetDirectories(parent).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    public void Dispose()
    {
        var remaining = Entries().Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
        if (remaining.Length != 0)
        {
            string message = "SCRATCH_INVOCATION_CLEAN: test-data roots remain: " + string.Join(", ", remaining.Select(Path.GetFileName));
            if (PrimaryFailureReported) { Console.Error.WriteLine("SCRATCH_RETAINED_AFTER_FAILURE: " + message); return; }
            throw new IOException(message);
        }
    }
}
