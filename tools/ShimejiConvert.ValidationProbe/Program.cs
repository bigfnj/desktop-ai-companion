using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        CheckGeometry(path, document, results);
        CheckParityMap(path, document, results);
    }

    private sealed class FrameGeometry
    {
        public int X, Y, Width, Height;
    }

    private static object CheckMap(string name, string input, XDocument document, string map, bool expected, string diagnostic = null)
    {
        string error = null;
        try { ValidateMapGeometry(document, map); }
        catch (Exception failure) when (failure is InvalidDataException || failure is JsonException)
        { error = failure.Message; }
        bool accepted = error == null;
        bool pass = accepted == expected && (diagnostic == null || (error != null && error.Contains(diagnostic, StringComparison.Ordinal)));
        Console.WriteLine((pass ? "PASS " : "FAIL ") + name + ": expected=" + expected + ", mapGeometry=" + accepted);
        return new { name, input, expectedAccepted = expected, accepted, expectedDiagnostic = diagnostic, error, pass };
    }

    private static int Attribute(XElement element, string name, int fallback)
    {
        XAttribute attribute = element.Attribute(name);
        return attribute == null ? fallback : int.Parse(attribute.Value, CultureInfo.InvariantCulture);
    }

    private static void CellGeometry(XDocument document, out int width, out int height, out int x, out int y)
    {
        XElement image = document.Root.Element(Ns + "image");
        int columns = int.Parse(image.Element(Ns + "tilesx").Value, CultureInfo.InvariantCulture);
        int rows = int.Parse(image.Element(Ns + "tilesy").Value, CultureInfo.InvariantCulture);
        using (var bytes = new MemoryStream(Convert.FromBase64String(image.Element(Ns + "png").Value)))
        using (var sheet = Image.FromStream(bytes))
        {
            width = sheet.Width / columns;
            height = sheet.Height / rows;
        }
        x = image.Element(Ns + "anchorx") == null ? width / 2 : int.Parse(image.Element(Ns + "anchorx").Value, CultureInfo.InvariantCulture);
        y = image.Element(Ns + "anchory") == null ? height : int.Parse(image.Element(Ns + "anchory").Value, CultureInfo.InvariantCulture);
    }

    // The unscaled pose/frame rule in the host's ParityMap.CheckGeometry. This probe checks converter
    // output, whose maps must provide geometry for every visible frame. It does not stand in for a host run.
    private static void ValidateMapGeometry(XDocument document, string json)
    {
        var frames = new Dictionary<int, FrameGeometry>();
        using (JsonDocument map = JsonDocument.Parse(json))
        {
            if (map.RootElement.GetProperty("schema").GetString() != "shimeji-parity-map/1")
                throw new InvalidDataException("parity map has an unsupported schema");
            foreach (JsonElement frame in map.RootElement.GetProperty("frames").EnumerateArray())
            {
                int id = frame.GetProperty("frame").GetInt32();
                JsonElement anchor = frame.GetProperty("anchor"), size = frame.GetProperty("size");
                frames[id] = new FrameGeometry { X = anchor[0].GetInt32(), Y = anchor[1].GetInt32(),
                    Width = size[0].GetInt32(), Height = size[1].GetInt32() };
            }
        }
        int cellWidth, cellHeight, defaultX, defaultY;
        CellGeometry(document, out cellWidth, out cellHeight, out defaultX, out defaultY);
        foreach (XElement node in document.Root.Element(Ns + "animations").Elements(Ns + "animation"))
        {
            XElement act = node.Element(Ns + "act");
            if (act == null) continue;
            foreach (XElement pose in act.Descendants(Ns + "pose"))
            {
                int ax = Attribute(pose, "ax", defaultX), ay = Attribute(pose, "ay", defaultY);
                int width = Attribute(pose, "w", cellWidth), height = Attribute(pose, "h", cellHeight);
                CheckMapFrame(frames, node, Attribute(pose, "frame", -1), ax, ay, width, height);
                CheckMapFrame(frames, node, Attribute(pose, "right", -1), ax, ay,
                    Attribute(pose, "rw", width), Attribute(pose, "rh", height));
            }
        }
    }

    private static void CheckMapFrame(Dictionary<int, FrameGeometry> frames, XElement node, int index, int x, int y, int width, int height)
    {
        if (index < 0) return;
        FrameGeometry frame;
        if (!frames.TryGetValue(index, out frame))
            throw new InvalidDataException("parity map frame " + index + " is missing");
        string where = "parity map frame " + index + " for action node " + node.Attribute("id").Value + " '" + node.Element(Ns + "name").Value + "'";
        if (frame.X != x || frame.Y != y)
            throw new InvalidDataException(where + " anchor disagrees with pose: (" + frame.X + ", " + frame.Y + ") versus (" + x + ", " + y + ")");
        if (frame.Width != width || frame.Height != height)
            throw new InvalidDataException(where + " size disagrees with pose: " + frame.Width + "x" + frame.Height + " versus " + width + "x" + height);
    }

    private static void CheckParityMap(string path, XDocument document, List<object> results)
    {
        string mapPath = Path.Combine(Path.GetDirectoryName(path), "parity-map.json");
        string map = File.ReadAllText(mapPath);
        results.Add(CheckMap("emitted-parity-map", mapPath, document, map, true));
        JsonNode wrongSchema = JsonNode.Parse(map);
        wrongSchema["schema"] = "unsupported";
        results.Add(CheckMap("control-map-schema", mapPath, document, wrongSchema.ToJsonString(), false, "schema"));
        XElement visible = document.Descendants(Ns + "pose").First(p => Attribute(p, "frame", -1) >= 0);
        int frame = Attribute(visible, "frame", -1);
        JsonNode missingFrame = JsonNode.Parse(map);
        JsonArray frameList = missingFrame["frames"].AsArray();
        frameList.Remove(frameList.Single(f => f["frame"].GetValue<int>() == frame));
        results.Add(CheckMap("control-map-missing-frame", mapPath, document, missingFrame.ToJsonString(), false, "parity map frame " + frame));
        Action<string, string, int> control = (name, field, component) =>
        {
            JsonNode invalid = JsonNode.Parse(map);
            JsonNode target = invalid["frames"].AsArray().Single(f => f["frame"].GetValue<int>() == frame);
            target[field][component] = target[field][component].GetValue<int>() + 1;
            results.Add(CheckMap(name, mapPath, document, invalid.ToJsonString(), false, "parity map frame " + frame));
        };
        control("control-map-anchor-x", "anchor", 0);
        control("control-map-anchor-y", "anchor", 1);
        control("control-map-width", "size", 0);
        control("control-map-height", "size", 1);
        XElement alternate = document.Descendants(Ns + "pose").FirstOrDefault(p => Attribute(p, "right", -1) >= 0);
        if (alternate != null)
        {
            int right = Attribute(alternate, "right", -1);
            JsonNode invalid = JsonNode.Parse(map);
            JsonNode target = invalid["frames"].AsArray().Single(f => f["frame"].GetValue<int>() == right);
            target["anchor"][0] = target["anchor"][0].GetValue<int>() + 1;
            results.Add(CheckMap("control-map-right-anchor", mapPath, document, invalid.ToJsonString(), false, "parity map frame " + right));
        }
    }

    private static void CheckGeometry(string path, XDocument document, List<object> results)
    {
        XElement source = document.Descendants(Ns + "pose").FirstOrDefault(p =>
            p.Attribute("w") != null || p.Attribute("h") != null || p.Attribute("ax") != null);
        if (source == null) return;

        XElement image = document.Root.Element(Ns + "image");
        int columns = int.Parse(image.Element(Ns + "tilesx").Value, CultureInfo.InvariantCulture);
        int rows = int.Parse(image.Element(Ns + "tilesy").Value, CultureInfo.InvariantCulture);
        int cellWidth, cellHeight;
        using (var stream = new MemoryStream(Convert.FromBase64String(image.Element(Ns + "png").Value)))
        using (var sheet = Image.FromStream(stream))
        {
            cellWidth = sheet.Width / columns;
            cellHeight = sheet.Height / rows;
        }
        int poseIndex = document.Descendants(Ns + "pose").ToList().IndexOf(source);
        Action<string, Action<XElement>> control = (name, mutate) =>
        {
            XDocument invalid = new XDocument(document);
            XElement pose = invalid.Descendants(Ns + "pose").ElementAt(poseIndex);
            mutate(pose);
            results.Add(Check(name, path, invalid.ToString(), false));
        };
        control("control-pose-width-zero", p => p.SetAttributeValue("w", 0));
        control("control-pose-height-zero", p => p.SetAttributeValue("h", 0));
        control("control-pose-width-exceeds-cell", p => p.SetAttributeValue("w", checked(cellWidth + 1)));
        control("control-pose-height-exceeds-cell", p => p.SetAttributeValue("h", checked(cellHeight + 1)));
        control("control-pose-anchor-x-high", p => p.SetAttributeValue("ax", 4097));
        control("control-pose-anchor-y-low", p => p.SetAttributeValue("ay", -4097));
        control("control-pose-right-width-without-frame", p =>
        {
            p.Attribute("right")?.Remove();
            p.Attribute("rh")?.Remove();
            p.SetAttributeValue("rw", 1);
        });
        control("control-pose-right-height-without-frame", p =>
        {
            p.Attribute("right")?.Remove();
            p.Attribute("rw")?.Remove();
            p.SetAttributeValue("rh", 1);
        });
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
