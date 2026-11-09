using System.Security.Cryptography;

namespace AgentBroker.Worker;

// Explicit client output only. No peer-supplied path is passed to this class.
internal static class LocalOutputFile
{
    internal sealed record Retention(string Path, string Sha256, long Bytes);

    internal static string Validate(string destination)
    {
        if (!Path.IsPathFullyQualified(destination) || destination.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("invalid_output_path");
        RequireUnambiguousNames(destination);
        string full = Path.GetFullPath(destination);
        if (full.Length < 4 || full[1] != ':' || full[2] != Path.DirectorySeparatorChar ||
            full[3..].Contains(':') || full.Any(char.IsControl) || Path.GetFileName(full).Length == 0)
            throw new InvalidDataException("invalid_output_path");
        string parent = Path.GetDirectoryName(full) ?? throw new InvalidDataException("invalid_output_path");
        RequirePlainDirectories(parent);
        string name = Path.GetFileName(full), stem = name.Split('.')[0];
        if (name.EndsWith(' ') || name.EndsWith('.') || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')
            throw new InvalidDataException("invalid_output_path");
        try { _ = File.GetAttributes(full); throw new IOException("output_exists"); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return full;
    }

    private static void RequireUnambiguousNames(string destination)
    {
        // GetFullPath can remove trailing dots and navigation segments. Check the names the caller supplied first.
        string[] components = destination.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int index = 0; index < components.Length; index++)
        {
            string component = components[index];
            if (component is "." or "..") continue;
            if (component.EndsWith(' ') || component.EndsWith('.'))
                throw new InvalidDataException("invalid_output_path");
        }
    }

    private static void RequirePlainDirectories(string directory)
    {
        for (string? component = directory; component is not null; component = Path.GetDirectoryName(component))
        {
            var attributes = File.GetAttributes(component);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0 ||
                Path.GetFileName(component).EndsWith(' ') || Path.GetFileName(component).EndsWith('.'))
                throw new InvalidDataException("invalid_output_path");
        }
    }

    // The internal publication seam permits an actual destination-race check without timing-dependent test loops.
    internal static Retention Save(string destination, byte[] bytes, Action? beforePublish = null)
    {
        string full = Validate(destination);
        string parent = Path.GetDirectoryName(full)!;
        string temporary = Path.Combine(parent, ".agentbroker-output-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.WriteThrough))
            {
                owned = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            beforePublish?.Invoke();
            RequirePlainDirectories(parent);
            if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("invalid_output_path");
            File.Move(temporary, full, overwrite: false);
            owned = false;
            return new(full, Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength);
        }
        finally
        {
            // A changed parent no longer identifies our temporary. Never follow it to delete a same-named foreign file.
            if (owned && PlainDirectoriesForCleanup(parent) && File.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0)
                File.Delete(temporary);
        }
    }

    private static bool PlainDirectoriesForCleanup(string directory)
    {
        try { RequirePlainDirectories(directory); return true; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
    }
}
