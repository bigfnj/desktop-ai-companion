using AgentBroker.CodexDesktopAdapter;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

internal static class VsCodeManifestChecks
{
    private static readonly string[] Cases = ["canonical", "metadata", "functional", "duplicates", "malformed", "invalid-encoding", "read-bound", "hash-bound", "required-hash", "installed-manifest-required", "router-pairs"];

    internal static void Run(Action<bool, string> check, string? only = null)
    {
        if (only is not null && !Cases.Contains(only)) throw new ArgumentException("Unknown manifest case.", nameof(only));
        foreach (string name in Cases.Where(name => only is null || name == only))
        {
            switch (name)
            {
                case "canonical":
                {
                    const string input = "{\"z\":true,\"a\":{\"y\":\"v\",\"x\":null},\"array\":[1,2],\"__metadata\":{\"when\":1}}";
                    const string expected = "{\"a\":{\"x\":null,\"y\":\"v\"},\"array\":[1,2],\"z\":true}";
                    check(Hash(input) == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))),
                        "VsCodeManifestChecks.cs: canonical bytes sort every object while preserving array order and scalar values.");
                    check(Hash(input) == Hash(" { \"array\" : [1, 2], \"a\" : {\"x\":null, \"y\":\"\\u0076\"}, \"z\":true } "),
                        "VsCodeManifestChecks.cs: whitespace, property order and equivalent string escapes do not change functional content.");
                    var previous = CultureInfo.CurrentCulture;
                    try
                    {
                        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                        check(Hash("{\"i\":2,\"I\":1}") == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("{\"I\":1,\"i\":2}"))),
                            "VsCodeManifestChecks.cs: property order is ordinal even under a nondefault culture.");
                    }
                    finally { CultureInfo.CurrentCulture = previous; }
                    break;
                }
                case "metadata":
                {
                    string expected = Hash("{\"main\":\"index.js\",\"nested\":{\"__metadata\":1}}");
                    foreach (string metadata in new[] { "{}", "{\"installedTimestamp\":123,\"size\":100,\"targetPlatform\":\"win32-x64\"}", "null", "[1,true,\"x\"]" })
                        check(Hash("{\"__metadata\":" + metadata + ",\"nested\":{\"__metadata\":1},\"main\":\"index.js\"}") == expected,
                            "VsCodeManifestChecks.cs: only root installation metadata can vary without changing the functional hash.");
                    check(Hash("{\"main\":\"index.js\",\"nested\":{\"__metadata\":2}}") != expected,
                        "VsCodeManifestChecks.cs: nested metadata remains functional content.");
                    check(Hash("{\"main\":\"index.js\",\"nested\":{\"__metadata\":1},\"__Metadata\":1}") != expected,
                        "VsCodeManifestChecks.cs: the exclusion is case sensitive and does not cover similar property names.");
                    break;
                }
                case "functional":
                {
                    const string baseline = "{\"main\":\"./out/extension.js\",\"version\":\"26.1007.21434\",\"contributes\":{\"commands\":[\"first\",\"second\"],\"enabled\":true,\"count\":2,\"optional\":null}}";
                    foreach (string changed in new[]
                    {
                        baseline.Replace("./out/extension.js", "./out/other.js"),
                        baseline.Replace("26.1007.21434", "26.1007.21435"),
                        baseline.Replace("\"first\",\"second\"", "\"second\",\"first\""),
                        baseline.Replace("\"enabled\":true", "\"enabled\":false"),
                        baseline.Replace("\"count\":2", "\"count\":3"),
                        baseline.Replace("\"optional\":null", "\"optional\":0"),
                        baseline.Replace("\"commands\"", "\"otherCommands\""),
                        baseline[..^1] + ",\"newFunctionalField\":1}"
                    })
                        check(Hash(changed) != Hash(baseline),
                            "VsCodeManifestChecks.cs: entrypoint, version, contributions, array order and every other functional value remain hashed.");
                    break;
                }
                case "duplicates":
                {
                    foreach (string duplicate in new[]
                    {
                        "{\"main\":1,\"main\":2}", "{\"m\\u0061in\":1,\"main\":2}",
                        "{\"contributes\":{\"x\":1,\"x\":2}}", "{\"items\":[{\"x\":1,\"x\":2}]}",
                        "{\"__metadata\":{\"x\":1,\"x\":2}}", "{\"__metadata\":1,\"__metadata\":2}"
                    }) Refuses("vscode_manifest_invalid", () => Hash(duplicate), check);
                    check(Hash("{\"main\":1,\"Main\":2}").Length == 64,
                        "VsCodeManifestChecks.cs: distinct case sensitive property names remain valid.");
                    break;
                }
                case "malformed":
                {
                    foreach (string invalid in new[] { "", "{", "[]", "null", "true", "{\"x\":1,}", "{/*comment*/\"x\":1}", "{}{}", new string('[', 65) + new string(']', 65) })
                        Refuses("vscode_manifest_invalid", () => Hash(invalid), check);
                    break;
                }
                case "invalid-encoding":
                {
                    foreach (string prefix in new[] { "{\"__metadata\":\"", "{\"field\":\"", "{\"" })
                    {
                        string suffix = prefix == "{\"" ? "\":1}" : "\"}";
                        byte[] bytes = Encoding.UTF8.GetBytes(prefix).Concat(new byte[] { 0xff }).Concat(Encoding.UTF8.GetBytes(suffix)).ToArray();
                        Refuses("vscode_manifest_invalid", () => VsCodeManifestProfile.ComputeSha256(bytes), check);
                    }
                    break;
                }
                case "read-bound":
                {
                    using var source = new MemoryStream(OversizedValidJson());
                    Refuses("vscode_manifest_invalid", () => VsCodeManifestProfile.ReadBounded(source), check);
                    check(source.Position <= VsCodeManifestProfile.MaximumBytes + 8192,
                        "VsCodeManifestChecks.cs: oversized manifests stop after one bounded extra read.");
                    using var valid = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
                    check(Encoding.UTF8.GetString(VsCodeManifestProfile.ReadBounded(valid)) == "{}",
                        "VsCodeManifestChecks.cs: bounded reader returns valid public manifest bytes unchanged.");
                    break;
                }
                case "hash-bound": Refuses("vscode_manifest_invalid", () => VsCodeManifestProfile.ComputeSha256(OversizedValidJson()), check); break;
                case "required-hash": Refuses("vscode_image_mismatch", () => VsCodeManifestProfile.Verify(Encoding.UTF8.GetBytes("{}")), check); break;
                case "installed-manifest-required":
                {
                    string root = Path.Combine(Path.GetTempPath(), "dac-manifest-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(root);
                    string manifest = Path.Combine(root, "package.json");
                    try
                    {
                        File.WriteAllText(manifest, "{}", new UTF8Encoding(false));
                        Refuses("vscode_image_mismatch", () => VsCodeCompatibilityProfile.VerifyInstalledAt(root), check);
                    }
                    finally { File.Delete(manifest); Directory.Delete(root); }
                    break;
                }
                case "router-pairs":
                {
                    string image = VsCodeRouterCompatibilityProfile.ExpectedImage;
                    VsCodeRouterCompatibilityProfile.ValidateMetadata(image, VsCodeRouterCompatibilityProfile.ExecutableSha256, VsCodeRouterCompatibilityProfile.ProductVersion);
                    VsCodeRouterCompatibilityProfile.ValidateMetadata(image, VsCodeRouterCompatibilityProfile.AdditionalExecutableSha256, VsCodeRouterCompatibilityProfile.AdditionalProductVersion);
                    check(true, "VsCodeManifestChecks.cs: both independently pinned old and current Code executable pairs remain supported.");
                    foreach (var invalid in new[]
                    {
                        (VsCodeRouterCompatibilityProfile.ExecutableSha256, VsCodeRouterCompatibilityProfile.AdditionalProductVersion),
                        (VsCodeRouterCompatibilityProfile.AdditionalExecutableSha256, VsCodeRouterCompatibilityProfile.ProductVersion),
                        (VsCodeRouterCompatibilityProfile.AdditionalExecutableSha256, "1.143.2"),
                        (new string('0', 64), VsCodeRouterCompatibilityProfile.AdditionalProductVersion)
                    }) Refuses("unsupported_vscode_router", () => VsCodeRouterCompatibilityProfile.ValidateMetadata(image, invalid.Item1, invalid.Item2), check);
                    break;
                }
            }
        }
    }

    private static byte[] OversizedValidJson()
    {
        byte[] bytes = new byte[VsCodeManifestProfile.MaximumBytes + 1];
        Array.Fill(bytes, (byte)' ');
        bytes[0] = (byte)'{'; bytes[1] = (byte)'}';
        return bytes;
    }
    private static string Hash(string json) => VsCodeManifestProfile.ComputeSha256(Encoding.UTF8.GetBytes(json));
    private static void Refuses(string code, Action action, Action<bool, string> check)
    {
        try { action(); throw new InvalidOperationException("VsCodeManifestChecks.cs: expected refusal " + code); }
        catch (DesktopAdapterException error) { check(error.Code == code, "VsCodeManifestChecks.cs: expected " + code + ", received " + error.Code); }
    }
}
