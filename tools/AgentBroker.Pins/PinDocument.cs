using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBroker.Pins;

// The reviewed exact vendor pins, as data (lane broker-pins, owner decision 2026-10-27). ONE document,
// agentbroker-pins.json beside this file, is embedded as a resource in BOTH adapter assemblies
// (AgentBroker.ClaudeAdapter and AgentBroker.CodexDesktopAdapter), and this file is compiled into both. Each
// copy validates the whole document, so the two adapters cannot disagree about it. The resource lives inside
// the worker payload the module catalog hashes; a loose file in a user-writable folder could be changed by any
// process running as the user, and the pin is the only check that a new Claude build still treats broker input
// as untrusted. A re-pin therefore arrives as an AgentBroker module update, never as a local edit.
//
// Every type here is internal: the adapters expose their own typed views, so an assembly that references both
// adapters (the worker) never sees two copies of one public type.
internal sealed record ClaudeCodePin(string Version, string Sha256);

internal sealed record CodexDesktopPin(string PackageFullName, string ApplicationVersion, string ExecutableSha256,
    string ArchiveSha256, string ElectronVersion);

internal sealed record CodexVsCodePin(string Version, string DirectoryName, string NativeImageSha256,
    string ManifestFunctionalSha256, IReadOnlyList<KeyValuePair<string, string>> Images);

internal sealed record VsCodeRouterPin(string ProductVersion, string ExecutableSha256);

internal sealed class PinDocumentException(string message) : Exception(message);

/// <summary>
/// The validated pin document. Loading never throws: a missing, oversized, malformed or schema-violating
/// document yields <see cref="Error"/> and four EMPTY lists, so every exact-pin check fails closed and the
/// Native status can say why. Each pin carries exactly what the adapters compared before the move to data.
/// </summary>
internal sealed class PinDocument
{
    internal const string ResourceName = "AgentBroker.Pins.agentbroker-pins.json";
    internal const int MaximumBytes = 256 * 1024;
    internal const int MaximumPinsPerList = 64;
    internal const int MaximumImagesPerPin = 32;
    private const string CodexPublisherId = "2p2nqsd0c76g0";

    private static readonly Lazy<PinDocument> current =
        new(() => Load(typeof(PinDocument).Assembly), LazyThreadSafetyMode.ExecutionAndPublication);

    // \A and \z, never ^ and $: .NET's $ also matches before a final newline, which would admit "...\n".
    private static readonly Regex Hex64 = new(@"\A[0-9A-F]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ThreePartVersion = new(@"\A(0|[1-9][0-9]{0,8})(\.(0|[1-9][0-9]{0,8})){2}\z", RegexOptions.CultureInvariant);
    private static readonly Regex FourPartVersion = new(@"\A(0|[1-9][0-9]{0,8})(\.(0|[1-9][0-9]{0,8})){3}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ElectronVersion = new(@"\A(0|[1-9][0-9]{0,8})(\.(0|[1-9][0-9]{0,8})){1,3}\z", RegexOptions.CultureInvariant);
    private static readonly Regex RelativeImage = new(@"\A[A-Za-z0-9_-][A-Za-z0-9._-]{0,127}(/[A-Za-z0-9_-][A-Za-z0-9._-]{0,127}){0,7}\z", RegexOptions.CultureInvariant);

    private static readonly string[] RootMembers = ["schema", "claudeCode", "codexDesktop", "codexVsCode", "vscodeRouter"];
    private static readonly string[] ClaudeMembers = ["version", "sha256"];
    private static readonly string[] DesktopMembers = ["packageFullName", "applicationVersion", "executableSha256", "archiveSha256", "electronVersion"];
    private static readonly string[] VsCodeMembers = ["version", "directoryName", "nativeImageSha256", "manifestFunctionalSha256", "images"];
    private static readonly string[] RouterMembers = ["productVersion", "executableSha256"];

    private PinDocument(string? error, IReadOnlyList<ClaudeCodePin> claude, IReadOnlyList<CodexDesktopPin> desktop,
        IReadOnlyList<CodexVsCodePin> vscode, IReadOnlyList<VsCodeRouterPin> routers)
    { Error = error; ClaudeCode = claude; CodexDesktop = desktop; CodexVsCode = vscode; VsCodeRouters = routers; }

    /// <summary>The document embedded in this assembly, loaded and validated once.</summary>
    internal static PinDocument Current => current.Value;

    /// <summary>Why the document was refused, or null when every member validated.</summary>
    internal string? Error { get; }
    internal IReadOnlyList<ClaudeCodePin> ClaudeCode { get; }
    internal IReadOnlyList<CodexDesktopPin> CodexDesktop { get; }
    internal IReadOnlyList<CodexVsCodePin> CodexVsCode { get; }
    internal IReadOnlyList<VsCodeRouterPin> VsCodeRouters { get; }

    internal static PinDocument Refused(string error) => new(error, [], [], [], []);

    /// <summary>
    /// Reporting only, never trust: true when <paramref name="candidate"/> parses as a version and is newer than every
    /// pinned version. Missing parts count as zero, so "2.1.310.0" is the pinned "2.1.310", not newer than it. With no
    /// pin at all (a refused document) nothing is "newer": the refusal is reported instead.
    /// </summary>
    internal static bool IsNewer(string? candidate, IEnumerable<string> pinned)
    {
        if (!System.Version.TryParse(candidate, out var parsed)) return false;
        var installed = Normalize(parsed);
        bool any = false;
        foreach (string version in pinned)
        {
            if (!System.Version.TryParse(version, out var pin)) continue;
            any = true;
            if (Normalize(pin) >= installed) return false;
        }
        return any;
    }

    private static System.Version Normalize(System.Version value)
        => new(value.Major, value.Minor, Math.Max(value.Build, 0), Math.Max(value.Revision, 0));

    internal static PinDocument Load(Assembly assembly)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return Refused("the pin document is missing from the adapter assembly");
            using var contents = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (contents.Length + read > MaximumBytes) return Refused("the pin document exceeds " + MaximumBytes + " bytes");
                contents.Write(buffer, 0, read);
            }
            return Parse(contents.ToArray());
        }
        catch (IOException) { return Refused("the pin document could not be read from the adapter assembly"); }
    }

    internal static PinDocument Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) return Refused("the pin document exceeds " + MaximumBytes + " bytes");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            var root = Members(document.RootElement, "the pin document", RootMembers);
            var schema = root["schema"];
            if (schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != "1")
                throw new PinDocumentException("schema must be the number 1");
            var claude = List(root["claudeCode"], "claudeCode", (item, at) =>
            {
                var m = Members(item, at, ClaudeMembers);
                return new ClaudeCodePin(Text(m["version"], at + ".version", ThreePartVersion), Text(m["sha256"], at + ".sha256", Hex64));
            });
            var desktop = List(root["codexDesktop"], "codexDesktop", (item, at) =>
            {
                var m = Members(item, at, DesktopMembers);
                var pin = new CodexDesktopPin(Text(m["packageFullName"], at + ".packageFullName", null),
                    Text(m["applicationVersion"], at + ".applicationVersion", FourPartVersion),
                    Text(m["executableSha256"], at + ".executableSha256", Hex64), Text(m["archiveSha256"], at + ".archiveSha256", Hex64),
                    Text(m["electronVersion"], at + ".electronVersion", ElectronVersion));
                // The package name is the protected WindowsApps directory the adapter opens; it must be exactly
                // the OpenAI.Codex x64 package of this version from the OpenAI publisher ID.
                if (pin.PackageFullName != "OpenAI.Codex_" + pin.ApplicationVersion + "_x64__" + CodexPublisherId)
                    throw new PinDocumentException(at + ".packageFullName is not the OpenAI.Codex x64 package of its applicationVersion");
                return pin;
            });
            var vscode = List(root["codexVsCode"], "codexVsCode", (item, at) =>
            {
                var m = Members(item, at, VsCodeMembers);
                string version = Text(m["version"], at + ".version", ThreePartVersion);
                string directory = Text(m["directoryName"], at + ".directoryName", null);
                if (directory != "openai.chatgpt-" + version + "-win32-x64")
                    throw new PinDocumentException(at + ".directoryName is not the win32-x64 extension folder of its version");
                return new CodexVsCodePin(version, directory, Text(m["nativeImageSha256"], at + ".nativeImageSha256", Hex64),
                    Text(m["manifestFunctionalSha256"], at + ".manifestFunctionalSha256", Hex64), Images(m["images"], at + ".images"));
            });
            var routers = List(root["vscodeRouter"], "vscodeRouter", (item, at) =>
            {
                var m = Members(item, at, RouterMembers);
                return new VsCodeRouterPin(Text(m["productVersion"], at + ".productVersion", ThreePartVersion),
                    Text(m["executableSha256"], at + ".executableSha256", Hex64));
            });
            // Discovery selects a Claude profile by image hash alone, so one hash may name only one pin.
            Unique(claude.Select(pin => pin.Sha256), "claudeCode", "sha256");
            Unique(desktop.Select(pin => pin.PackageFullName), "codexDesktop", "packageFullName");
            Unique(vscode.Select(pin => pin.Version), "codexVsCode", "version");
            Unique(routers.Select(pin => pin.ProductVersion + "|" + pin.ExecutableSha256), "vscodeRouter", "productVersion and executableSha256 pair");
            return new(null, claude, desktop, vscode, routers);
        }
        catch (JsonException) { return Refused("the pin document is not valid JSON"); }
        catch (PinDocumentException error) { return Refused(error.Message); }
        catch (InvalidOperationException) { return Refused("the pin document has an unexpected JSON shape"); }
    }

    // Exactly the expected members: an unknown, missing or repeated name refuses the whole document.
    private static Dictionary<string, JsonElement> Members(JsonElement value, string at, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new PinDocumentException(at + " must be an object");
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                throw new PinDocumentException(at + " has an unknown member " + Describe(property.Name));
            if (!members.TryAdd(property.Name, property.Value))
                throw new PinDocumentException(at + " repeats the member " + property.Name);
        }
        foreach (string name in expected)
            if (!members.ContainsKey(name)) throw new PinDocumentException(at + " is missing the member " + name);
        return members;
    }

    private static IReadOnlyList<T> List<T>(JsonElement value, string at, Func<JsonElement, string, T> read)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new PinDocumentException(at + " must be an array");
        if (value.GetArrayLength() > MaximumPinsPerList) throw new PinDocumentException(at + " has more than " + MaximumPinsPerList + " pins");
        var items = new List<T>();
        int index = 0;
        foreach (var item in value.EnumerateArray()) items.Add(read(item, at + "[" + index++ + "]"));
        return items.AsReadOnly();
    }

    private static string Text(JsonElement value, string at, Regex? format)
    {
        if (value.ValueKind != JsonValueKind.String) throw new PinDocumentException(at + " must be a string");
        string text = value.GetString()!;
        if (text.Length is 0 or > 256 || text.Any(character => character is < ' ' or > '~'))
            throw new PinDocumentException(at + " must be 1 to 256 printable ASCII characters");
        if (format is not null && !format.IsMatch(text)) throw new PinDocumentException(at + " is not in the required format");
        return text;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Images(JsonElement value, string at)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new PinDocumentException(at + " must be an object");
        var images = new List<KeyValuePair<string, string>>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            // Relative forward-slash paths inside the extension folder only. Every segment starts with a letter,
            // digit, '_' or '-', so no rooted path, drive, "." or ".." segment can match.
            if (!RelativeImage.IsMatch(property.Name))
                throw new PinDocumentException(at + " has an image path that is not a plain relative path");
            // Windows paths compare without case, so two spellings of one file are one repeated image.
            if (!names.Add(property.Name)) throw new PinDocumentException(at + " repeats an image path");
            images.Add(new(property.Name, Text(property.Value, at + "[" + property.Name + "]", Hex64)));
        }
        if (images.Count is 0 or > MaximumImagesPerPin)
            throw new PinDocumentException(at + " must name 1 to " + MaximumImagesPerPin + " images");
        return images.AsReadOnly();
    }

    private static void Unique(IEnumerable<string> keys, string list, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in keys)
            if (!seen.Add(key)) throw new PinDocumentException(list + " repeats a " + what);
    }

    // An unknown name is echoed only when it is short printable ASCII; otherwise it is described, never printed.
    private static string Describe(string name) => name.Length is > 0 and <= 48 && name.All(character => character is >= ' ' and <= '~')
        ? "'" + name + "'" : "(not printable)";
}
