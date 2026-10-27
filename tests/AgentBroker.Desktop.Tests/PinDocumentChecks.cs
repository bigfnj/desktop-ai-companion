using System.Text;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Pins;

// Lane broker-pins: the exact pins moved from compiled constants into ONE embedded JSON document. These checks run
// the loader compiled into the Codex adapter (the Claude adapter compiles the same file and embeds the same bytes).
internal static class PinDocumentChecks
{
    private static readonly string[] Cases = ["embedded", "unknown-member", "missing-member", "repeated-member", "schema",
        "formats", "bindings", "image-paths", "duplicates", "malformed", "missing-resource", "fail-closed"];

    private const string Hex = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string Claude = "{\"version\":\"2.1.300\",\"sha256\":\"" + Hex + "\"}";
    private const string Desktop = "{\"packageFullName\":\"OpenAI.Codex_26.2000.1.0_x64__2p2nqsd0c76g0\",\"applicationVersion\":\"26.2000.1.0\","
        + "\"executableSha256\":\"" + Hex + "\",\"archiveSha256\":\"" + Hex + "\",\"electronVersion\":\"156.0.1.2\"}";
    private const string VsCode = "{\"version\":\"26.2000.5\",\"directoryName\":\"openai.chatgpt-26.2000.5-win32-x64\",\"nativeImageSha256\":\"" + Hex
        + "\",\"manifestFunctionalSha256\":\"" + Hex + "\",\"images\":{\"out/extension.js\":\"" + Hex + "\"}}";
    private const string Router = "{\"productVersion\":\"1.150.0\",\"executableSha256\":\"" + Hex + "\"}";

    private static string Document(string claude = Claude, string desktop = Desktop, string vscode = VsCode, string router = Router,
        string schema = "1", string extra = "")
        => "{\"schema\":" + schema + ",\"claudeCode\":[" + claude + "],\"codexDesktop\":[" + desktop + "],\"codexVsCode\":[" + vscode
            + "],\"vscodeRouter\":[" + router + "]" + extra + "}";

    internal static void Run(Action<bool, string> check, string? only = null)
    {
        if (only is not null && !Cases.Contains(only)) throw new ArgumentException("Unknown pin document case.", nameof(only));
        foreach (string name in Cases.Where(name => only is null || name == only))
        {
            switch (name)
            {
                case "embedded":
                {
                    var current = PinDocument.Current;
                    check(current.Error is null, "PinDocumentChecks.cs: the embedded pin document validates: " + current.Error);
                    // The values the compiled constants held before the move, verbatim: every existing pin is unchanged.
                    check(current.ClaudeCode.Contains(new("2.1.292", "EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23")) &&
                        current.ClaudeCode.Contains(new("2.1.304", "019E5174D5D954585EDB1A946FE280E44A973050B4897034ADF0E16306F85FDD")) &&
                        current.ClaudeCode.Contains(new("2.1.306", "F6BC9DE38F507C36C9A3BB0085A59A27E32156E2208252BF1707C4E3EF309114")),
                        "PinDocumentChecks.cs: the document keeps the three historical Claude Code pins exactly");
                    check(current.VsCodeRouters.Contains(new("1.143.0", "F13F0179B0E6EC0251E22ADBD123FE5F252FA227421B2128CF21379CF5941D58")) &&
                        current.VsCodeRouters.Contains(new("1.143.1", "325FB30BA28D17FA6BA4DF58043727B2469ACA9D641793A65E413721B2110EC6")),
                        "PinDocumentChecks.cs: the document keeps both historical Code router pairs exactly");
                    var extension = current.CodexVsCode.SingleOrDefault(pin => pin.Version == "26.1007.21434");
                    check(extension is not null && extension.DirectoryName == "openai.chatgpt-26.1007.21434-win32-x64" &&
                        extension.NativeImageSha256 == "F5521B058CA08E249AFD3B25B4681E81100181D92E73AA6B94BB8E75C2AA487B" &&
                        extension.ManifestFunctionalSha256 == "C5C2262E3E36E38E4BF00F885811190F8DFE18B98D8FCB898F22A8600DE96F13" &&
                        extension.Images.SequenceEqual(new KeyValuePair<string, string>[]
                        {
                            new("out/extension.js", "A8069DE80C8F8141481D033F94C3A95E1B59F399143CB2554D3F68469DFBE5B8"),
                            new("webview/assets/app-main-92173aa1395c.js", "B4B080A8F62E22CFD67F2FAE85E39854DF5FF4ED8F898B8A7D8A6FE36886A357"),
                            new("webview/assets/app-initial-c014f9ee4429.js", "AABBFA7C366706365A239B090CA1C657B6A0213D7287E54EF0A32D1B2E5FF6A0"),
                            new("webview/assets/app-initial-97d3534ad35f.js", "47E9E488122CF37A6564ED57D11F499D9AC062B2E49DB729091FE3C163A64B49"),
                            new("webview/assets/app-initial-7a199c66e670.js", "59B909377A370E09CB8523AB99A1F4FF282A0126527BD339DB31E6D9AC7C9C26"),
                        }),
                        "PinDocumentChecks.cs: the document keeps the historical Codex VS Code profile, its manifest hash and all five images in order");
                    break;
                }
                case "unknown-member":
                {
                    Accepted(Document(), check, "PinDocumentChecks.cs: WITNESS: the synthetic complete document validates");
                    Refused(Document(extra: ",\"notes\":\"x\""), "unknown member 'notes'", check, "PinDocumentChecks.cs: an unknown root member refuses the document");
                    Refused(Document(claude: Claude[..^1] + ",\"client\":\"vscode\"}"), "claudeCode[0] has an unknown member 'client'", check,
                        "PinDocumentChecks.cs: an unknown pin member refuses the document");
                    Refused(Document(router: Router[..^1] + ",\"ExecutableSha256\":\"" + Hex + "\"}"), "unknown member 'ExecutableSha256'", check,
                        "PinDocumentChecks.cs: member names are case sensitive, so a differently cased name is unknown");
                    break;
                }
                case "missing-member":
                    Refused(Document(claude: "{\"version\":\"2.1.300\"}"), "claudeCode[0] is missing the member sha256", check,
                        "PinDocumentChecks.cs: a pin without one of its compared fields refuses the document");
                    Refused("{\"schema\":1,\"claudeCode\":[],\"codexDesktop\":[],\"codexVsCode\":[]}", "missing the member vscodeRouter", check,
                        "PinDocumentChecks.cs: a document without one of its pin lists is refused");
                    break;
                case "repeated-member":
                    Refused(Document(claude: "{\"version\":\"2.1.300\",\"sha256\":\"" + Hex + "\",\"sha256\":\"" + Hex.Replace('0', 'F') + "\"}"),
                        "repeats the member sha256", check, "PinDocumentChecks.cs: a repeated member refuses the document instead of keeping either value");
                    Refused(Document(extra: ",\"claudeCode\":[]"), "repeats the member claudeCode", check,
                        "PinDocumentChecks.cs: a repeated pin list refuses the document");
                    break;
                case "schema":
                    foreach (string schema in new[] { "2", "1.0", "\"1\"", "true" })
                        Refused(Document(schema: schema), "schema must be the number 1", check, "PinDocumentChecks.cs: only schema 1 is understood");
                    break;
                case "formats":
                    Refused(Document(claude: Claude.Replace(Hex, Hex.ToLowerInvariant())), "claudeCode[0].sha256 is not in the required format", check,
                        "PinDocumentChecks.cs: a lowercase hash is refused, since the Codex adapters compare uppercase hex exactly");
                    Refused(Document(claude: Claude.Replace(Hex, Hex[..63])), "claudeCode[0].sha256 is not in the required format", check,
                        "PinDocumentChecks.cs: a short hash is refused");
                    Refused(Document(claude: Claude.Replace("2.1.300", "2.1.300.0")), "claudeCode[0].version is not in the required format", check,
                        "PinDocumentChecks.cs: a Claude pin version has exactly three parts, as EngineProfile compares it");
                    Refused(Document(claude: Claude.Replace("2.1.300", "2.1.300\\n")), "claudeCode[0].version must be 1 to 256 printable ASCII", check,
                        "PinDocumentChecks.cs: a control character in a pin value is refused");
                    Refused(Document(router: Router.Replace("\"" + Hex + "\"", "1")), "vscodeRouter[0].executableSha256 must be a string", check,
                        "PinDocumentChecks.cs: a pin value of the wrong JSON type is refused");
                    Refused(Document(claude: "\"2.1.300\""), "claudeCode[0] must be an object", check, "PinDocumentChecks.cs: a pin that is not an object is refused");
                    break;
                case "bindings":
                    Refused(Document(desktop: Desktop.Replace("OpenAI.Codex_26.2000.1.0_x64__2p2nqsd0c76g0", "OpenAI.Codex_26.2000.1.0_x64__otherpublisher")),
                        "codexDesktop[0].packageFullName is not the OpenAI.Codex x64 package", check,
                        "PinDocumentChecks.cs: a desktop pin names only the OpenAI.Codex package of its own version and publisher ID");
                    Refused(Document(desktop: Desktop.Replace("\"applicationVersion\":\"26.2000.1.0\"", "\"applicationVersion\":\"26.2000.2.0\"")),
                        "codexDesktop[0].packageFullName is not the OpenAI.Codex x64 package", check,
                        "PinDocumentChecks.cs: a desktop pin's package name and version cannot disagree");
                    Refused(Document(vscode: VsCode.Replace("openai.chatgpt-26.2000.5-win32-x64", "openai.chatgpt-26.2000.4-win32-x64")),
                        "codexVsCode[0].directoryName is not the win32-x64 extension folder", check,
                        "PinDocumentChecks.cs: a VS Code pin's folder and version cannot disagree");
                    break;
                case "image-paths":
                    foreach (string path in new[] { "../out/extension.js", "out/../../x.js", "C:/out/extension.js", "/out/extension.js",
                        "out\\\\extension.js", ".hidden/x.js", "out//x.js" })
                        Refused(Document(vscode: VsCode.Replace("out/extension.js", path)), "codexVsCode[0].images has an image path that is not a plain relative path",
                            check, "PinDocumentChecks.cs: an image path that could leave the extension folder is refused: " + path);
                    Refused(Document(vscode: VsCode.Replace("{\"out/extension.js\":\"" + Hex + "\"}", "{}")), "codexVsCode[0].images must name 1 to", check,
                        "PinDocumentChecks.cs: a VS Code pin with no hashed image is refused");
                    Refused(Document(vscode: VsCode.Replace("{\"out/extension.js\":\"" + Hex + "\"}",
                        "{\"out/extension.js\":\"" + Hex + "\",\"OUT/Extension.js\":\"" + Hex + "\"}")), "codexVsCode[0].images repeats an image path", check,
                        "PinDocumentChecks.cs: two spellings of one Windows path are one repeated image");
                    break;
                case "duplicates":
                    Refused(Document(claude: Claude + "," + Claude.Replace("2.1.300", "2.1.301")), "claudeCode repeats a sha256", check,
                        "PinDocumentChecks.cs: one Claude image hash may name only one pin, since discovery selects by hash");
                    Refused(Document(desktop: Desktop + "," + Desktop), "codexDesktop repeats a packageFullName", check,
                        "PinDocumentChecks.cs: one desktop package may be pinned once");
                    Refused(Document(vscode: VsCode + "," + VsCode), "codexVsCode repeats a version", check,
                        "PinDocumentChecks.cs: one VS Code extension version may be pinned once");
                    Accepted(Document(router: Router + "," + Router.Replace("1.150.0", "1.150.1")), check,
                        "PinDocumentChecks.cs: WITNESS: two router versions with one hash stay two independent pairs");
                    break;
                case "malformed":
                    foreach (string invalid in new[] { "", "{", "[]", "null", Document() + ",", Document()[..^1] + ",}", "/*c*/" + Document(), Document() + Document() })
                        Refused(invalid, "the pin document", check, "PinDocumentChecks.cs: malformed JSON, comments and trailing data are refused");
                    var oversized = Encoding.UTF8.GetBytes(Document() + new string(' ', PinDocument.MaximumBytes));
                    check(PinDocument.Parse(oversized).Error == "the pin document exceeds " + PinDocument.MaximumBytes + " bytes",
                        "PinDocumentChecks.cs: an oversized document is refused before parsing");
                    break;
                case "missing-resource":
                {
                    var missing = PinDocument.Load(typeof(object).Assembly);
                    check(missing.Error == "the pin document is missing from the adapter assembly" && Empty(missing),
                        "PinDocumentChecks.cs: an assembly without the embedded document verifies nothing and says why");
                    break;
                }
                case "fail-closed":
                {
                    var refused = PinDocument.Parse(Encoding.UTF8.GetBytes(Document(extra: ",\"unexpected\":1")));
                    check(refused.Error is not null && Empty(refused), "PinDocumentChecks.cs: a refused document carries no pin at all");
                    var valid = PinDocument.Parse(Encoding.UTF8.GetBytes(Document()));
                    string desktopImage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps",
                        "OpenAI.Codex_26.2000.1.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");
                    check(DesktopCompatibilityProfile.SelectProfile(desktopImage, valid).ApplicationVersion == "26.2000.1.0",
                        "PinDocumentChecks.cs: WITNESS: a valid document's desktop pin selects its package");
                    RefusesCode("pin_document_invalid", () => DesktopCompatibilityProfile.SelectProfile(desktopImage, refused), check,
                        "PinDocumentChecks.cs: a refused document refuses the desktop router as pin_document_invalid");
                    VsCodeRouterCompatibilityProfile.ValidateMetadata(VsCodeRouterCompatibilityProfile.ExpectedImage, Hex, "1.150.0", valid);
                    check(true, "PinDocumentChecks.cs: WITNESS: a valid document's router pair is accepted");
                    RefusesCode("pin_document_invalid", () => VsCodeRouterCompatibilityProfile.ValidateMetadata(
                        VsCodeRouterCompatibilityProfile.ExpectedImage, Hex, "1.150.0", refused), check,
                        "PinDocumentChecks.cs: a refused document refuses the Code router as pin_document_invalid");
                    check(VsCodeCompatibilityProfile.ProfilesOf(valid).Single().Version == "26.2000.5",
                        "PinDocumentChecks.cs: WITNESS: a valid document's VS Code pin becomes its extension profile");
                    RefusesCode("pin_document_invalid", () => VsCodeCompatibilityProfile.ProfilesOf(refused), check,
                        "PinDocumentChecks.cs: a refused document refuses the VS Code extension profiles as pin_document_invalid");
                    // The refusal is not an exact-pin mismatch, so update-tolerant publisher evidence cannot stand in.
                    check(!DesktopCompatibilityProfile.IsExactPinMismatch("pin_document_invalid") &&
                        DesktopCompatibilityProfile.IsExactPinMismatch("unsupported_desktop_version"),
                        "PinDocumentChecks.cs: pin_document_invalid is never an exact-pin mismatch; WITNESS: a version miss is");
                    break;
                }
            }
        }
    }

    private static bool Empty(PinDocument document) => document.ClaudeCode.Count == 0 && document.CodexDesktop.Count == 0 &&
        document.CodexVsCode.Count == 0 && document.VsCodeRouters.Count == 0;

    private static void Accepted(string json, Action<bool, string> check, string label)
    {
        var parsed = PinDocument.Parse(Encoding.UTF8.GetBytes(json));
        check(parsed.Error is null && !Empty(parsed), label + " (" + parsed.Error + ")");
    }

    // Any exception escaping the loader fails the named check rather than the process, so a mutation names this file.
    private static void Refused(string json, string expected, Action<bool, string> check, string label)
    {
        PinDocument parsed;
        try { parsed = PinDocument.Parse(Encoding.UTF8.GetBytes(json)); }
        catch (Exception error) { check(false, label + " (the loader threw " + error.GetType().Name + ")"); return; }
        check(parsed.Error is not null && parsed.Error.Contains(expected, StringComparison.Ordinal) && Empty(parsed),
            label + " (error: " + (parsed.Error ?? "none") + ")");
    }

    private static void RefusesCode(string code, Action action, Action<bool, string> check, string label)
    {
        try { action(); }
        catch (DesktopAdapterException error) { check(error.Code == code, label + " (got " + error.Code + ")"); return; }
        check(false, label + " (no refusal)");
    }
}
