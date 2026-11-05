using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using DesktopAICompanion;
using XmlData;

internal static class Program
{
    private static readonly XNamespace Ns = "https://esheep.petrucci.ch/";

    private static object Check(string name, string input, string xml, bool expected, string diagnostic = null)
    {
        RootNode parsed;
        string error;
        bool accepted = CompanionXmlValidator.TryParseWithFeatures(xml, CompanionFormatFeatures.All, out parsed, out error);
        bool pass = accepted == expected && (diagnostic == null || (error != null && error.Contains(diagnostic, StringComparison.Ordinal)));
        Console.WriteLine((pass ? "PASS " : "FAIL ") + name + ": expected=" + expected + ", explicitAll=" + accepted);
        return new { name, input, expectedAccepted = expected, accepted, expectedDiagnostic = diagnostic, error, pass };
    }

    private static void CheckInput(string path, List<object> results)
    {
        string xml = File.ReadAllText(path);
        results.Add(Check("emitted-xml", path, xml, true));
        XDocument document = XDocument.Parse(xml);
        XElement header = document.Root.Element(Ns + "header");
        if (header == null || (string)header.Element(Ns + "clock") != "tick")
            throw new InvalidDataException("PROBE_REQUIRES_TICK_XML: " + path);

        XDocument undeclared = new XDocument(document);
        XElement features = undeclared.Root.Element(Ns + "header").Element(Ns + "features");
        if (features == null) throw new InvalidDataException("PROBE_REQUIRES_FEATURE_DECLARATION: " + path);
        features.Value = "";
        results.Add(Check("control-missing-feature-declarations", path, undeclared.ToString(), false));

        if (header.Element(Ns + "features").Value.Split(' ').Contains("packs"))
        {
            XDocument missingPacks = new XDocument(document);
            XElement declaration = missingPacks.Root.Element(Ns + "header").Element(Ns + "features");
            declaration.Value = string.Join(" ", declaration.Value.Split(' ').Where(token => token != "packs"));
            results.Add(Check("control-missing-packs", path, missingPacks.ToString(), false, "packs"));
        }

        XDocument badRoute = new XDocument(document);
        XElement[] animations = badRoute.Root.Element(Ns + "animations").Elements(Ns + "animation").ToArray();
        XElement route = animations.Single(a => a.Element(Ns + "route") != null);
        XElement first = route.Element(Ns + "sequence").Elements(Ns + "next").First();
        first.Value = route.Attribute("id").Value;
        results.Add(Check("control-route-to-route", path, badRoute.ToString(), false));
    }

    public static int Main(string[] args)
    {
        string resultPath = null;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--results" && i + 1 < args.Length && resultPath == null) resultPath = Path.GetFullPath(args[++i]);
            else if (args[i] == "--xml" && i + 1 < args.Length) inputs.Add(Path.GetFullPath(args[++i]));
            else throw new ArgumentException("Use --results <json> --xml <animations.xml> [--xml <animations.xml> ...].");
        }
        if (resultPath == null || inputs.Count == 0) throw new ArgumentException("Results path and at least one explicit XML path are required.");
        if (CompanionFormatFeatures.Supported.Count != 0)
            throw new InvalidOperationException("PROBE_RUNTIME_HOOK_UNEXPECTED: this build must exclude TickFeatures.cs.");

        var results = new List<object>();
        foreach (string path in inputs)
        {
            try { CheckInput(path, results); }
            catch (Exception error)
            {
                Console.WriteLine("FAIL probe-input: " + path + ": " + error.Message);
                results.Add(new { name = "probe-input", input = path, pass = false, error = error.ToString() });
            }
        }
        string encoded = JsonSerializer.Serialize(results);
        bool pass;
        using (JsonDocument check = JsonDocument.Parse(encoded))
            pass = check.RootElement.EnumerateArray().All(row => row.GetProperty("pass").GetBoolean());
        string artifact = typeof(Program).Assembly.Location;
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new {
            schema = "shimeji-validation-probe/1", status = pass ? "pass" : "fail",
            context = new { featureOverride = "CompanionFormatFeatures.All", coreOnly = true,
                runtimeHookLoaded = false, productionRuntimeProof = false, supportedFeatures = CompanionFormatFeatures.Supported.ToArray(),
                runsClassContext = "core validator default; TickFeatures.cs excluded" },
            artifactSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact))).ToLowerInvariant(),
            assemblyMvid = typeof(Program).Assembly.ManifestModule.ModuleVersionId, results
        }, new JsonSerializerOptions { WriteIndented = true }));
        return pass ? 0 : 1;
    }
}
