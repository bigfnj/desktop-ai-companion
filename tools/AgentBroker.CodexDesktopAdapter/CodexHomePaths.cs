using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentBroker.CodexDesktopAdapter;

/// <summary>Explicit local observation roots, never an inference from another process's environment.</summary>
public static class CodexHomePaths
{
    public const int MaximumAdditionalHomes = 4;
    public const int MaximumPathCharacters = 512;

    public static string[] NormalizeAdditional(IEnumerable<string> paths)
    {
        var result = new List<string>();
        int supplied = 0;
        foreach (string path in paths)
        {
            if (++supplied > MaximumAdditionalHomes || string.IsNullOrWhiteSpace(path) ||
                path.Length > MaximumPathCharacters || path.Any(char.IsControl) ||
                !Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
                path[1] != ':' || path[2] is not ('\\' or '/') ||
                path.Skip(2).Any(c => c is ':' or '"' or '<' or '>' or '|' or '?' or '*') ||
                path.Replace('/', '\\').Split('\\').Any(component => component is "." or ".."))
                throw new ArgumentException("Additional Codex homes must be at most four absolute local folders of at most 512 characters.");
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase)) result.Add(normalized);
        }
        return result.ToArray();
    }

    public static bool IsValidOptional(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        try { NormalizeAdditional([path]); return true; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
