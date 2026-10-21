using System.Security.Cryptography;

namespace DesktopAICompanion.AgentBroker.CodexRelay;

public sealed class RelayOptions
{
    public required string EnginePath { get; init; }
    public required string EngineSha256 { get; init; }
    public required string EngineVersion { get; init; }
    public required string PipeName { get; init; }
    public IReadOnlyList<string> EngineArguments { get; init; } = Array.Empty<string>();
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ExitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public static RelayOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0;
        for (; i < args.Length && args[i] != "--"; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--engine" or "--engine-sha256" or "--engine-version" or "--pipe")
                || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Invalid relay options.");
        }
        if (i == args.Length || values.Count != 4) throw new ArgumentException("Relay options and separator required.");
        return new RelayOptions
        {
            EnginePath = values["--engine"], EngineSha256 = values["--engine-sha256"],
            EngineVersion = values["--engine-version"], PipeName = values["--pipe"],
            EngineArguments = args.Skip(i + 1).ToArray(),
        };
    }

    internal void Validate()
    {
        if (!Path.IsPathFullyQualified(EnginePath) || !File.Exists(EnginePath)
            || EngineSha256.Length != 64 || EngineSha256.Any(c => !Uri.IsHexDigit(c))
            || string.IsNullOrWhiteSpace(EngineVersion) || EngineVersion.Any(char.IsWhiteSpace)
            || PipeName.Length is < 1 or > 128 || PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')
            || ResponseTimeout <= TimeSpan.Zero || ExitTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid relay configuration.");
    }

    internal FileStream VerifyEngine()
    {
        Validate();
        // Retain the non-write-sharing handle through Process.Start, reducing a hash/start replacement race.
        var file = new FileStream(EnginePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            byte[] actual = SHA256.HashData(file);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(EngineSha256)))
                throw new InvalidOperationException("Engine identity is not approved.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }
}
