using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
            if (map.RootElement.GetProperty("schema").GetString() != "shimeji-parity-map/3")
                throw new InvalidDataException("parity map has an unsupported schema");
            ValidatePoseMap(document, map.RootElement);
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

    // Map/2 binds emitted positions to source positions, including image-less and unplayed poses.
    // Only decision 68's exact synthetic placeholder has no source pose to map.
    private static void ValidatePoseMap(XDocument document, JsonElement map)
    {
        JsonElement poses, imageSet;
        if (!map.TryGetProperty("poses", out poses) || poses.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("parity map poses must be an object");
        if (!map.TryGetProperty("imageSet", out imageSet) || imageSet.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("parity map imageSet must be a string");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in poses.EnumerateObject())
        {
            string key = property.Name;
            if (!keys.Add(key)) throw new InvalidDataException("parity map poses has a duplicate key: " + key);
            string[] positions = key.Split(':');
            int position;
            if (positions.Length != 3 || positions.Any(p => !Regex.IsMatch(p, @"\A(?:0|[1-9][0-9]*)\z")
                || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out position)))
                throw new InvalidDataException("parity map poses has a malformed key: " + key);
            JsonElement value = property.Value, reference, index;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2
                || !value.TryGetProperty("refKey", out reference) || reference.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("index", out index) || index.ValueKind != JsonValueKind.Number
                || !Regex.IsMatch(index.GetRawText(), @"\A(?:0|[1-9][0-9]*)\z") || !index.TryGetInt32(out position))
                throw new InvalidDataException("parity map poses has a malformed value: " + key);
            string[] parts = reference.GetString().Split('|');
            Match terminal = parts.Length == 3 ? Regex.Match(parts[2], @"(?:\A|/)[^/\[\]]+\[([1-9][0-9]*)\]\z") : Match.Empty;
            int ordinal;
            if (parts.Length != 3 || parts[0] != imageSet.GetString() || string.IsNullOrEmpty(parts[1])
                || !terminal.Success || !int.TryParse(terminal.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
                || position != ordinal - 1)
                throw new InvalidDataException("parity map poses has a malformed source identity or index: " + key);
        }
        foreach (XElement node in document.Root.Element(Ns + "animations").Elements(Ns + "animation"))
        {
            XElement act = node.Element(Ns + "act");
            if (act == null) continue;
            XElement variantsElement = act.Element(Ns + "variants");
            XElement[] variants = variantsElement == null ? new XElement[0] : variantsElement.Elements(Ns + "variant").ToArray();
            bool placeholder = Decision68Placeholder(act, variants);
            for (int vi = 0; vi < variants.Length; vi++)
            {
                XElement[] emitted = variants[vi].Elements(Ns + "pose").ToArray();
                for (int pi = 0; pi < emitted.Length; pi++)
                {
                    string key = node.Attribute("id").Value + ":" + vi.ToString(CultureInfo.InvariantCulture) + ":" + pi.ToString(CultureInfo.InvariantCulture);
                    if (!keys.Remove(key) && !placeholder)
                        throw new InvalidDataException("parity map poses is missing emitted pose: " + key);
                }
            }
        }
        if (keys.Count != 0) throw new InvalidDataException("parity map poses has a surplus key: " + keys.OrderBy(k => k, StringComparer.Ordinal).First());
    }

    private static bool Decision68Placeholder(XElement act, XElement[] variants)
    {
        string type = (string)act.Attribute("type"), cls = (string)act.Attribute("class");
        if (!(type == "Animate" || (type == "Embedded" && cls == "Interact")) || variants.Length != 1
            || (string)variants[0].Attribute("when") != "false") return false;
        XElement[] poses = variants[0].Elements(Ns + "pose").ToArray();
        return poses.Length == 1 && Attribute(poses[0], "frame", -2) == -1 && Attribute(poses[0], "ticks", 0) == 1
            && Attribute(poses[0], "dx", 0) == 0 && Attribute(poses[0], "dy", 0) == 0;
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
        JsonNode oldSchema = JsonNode.Parse(map);
        oldSchema["schema"] = "shimeji-parity-map/1";
        results.Add(CheckMap("control-map-v1-schema", mapPath, document, oldSchema.ToJsonString(), false, "schema"));
        JsonNode retiredSchema = JsonNode.Parse(map);
        retiredSchema["schema"] = "shimeji-parity-map/2";
        results.Add(CheckMap("control-map-v2-schema", mapPath, document, retiredSchema.ToJsonString(), false, "schema"));
        CheckPoseMapControls(mapPath, document, map, results);
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

    private static void CheckPoseMapControls(string mapPath, XDocument document, string map, List<object> results)
    {
        Action<string, Action<JsonObject>, string> control = (name, mutate, diagnostic) =>
        {
            JsonObject invalid = JsonNode.Parse(map).AsObject();
            mutate(invalid);
            results.Add(CheckMap(name, mapPath, document, invalid.ToJsonString(), false, diagnostic));
        };
        control("control-map-missing-poses", m => m.Remove("poses"), "poses must be an object");
        control("control-map-poses-not-object", m => m["poses"] = new JsonArray(), "poses must be an object");
        string key = JsonNode.Parse(map)["poses"].AsObject().First().Key;
        control("control-map-missing-pose", m => m["poses"].AsObject().Remove(key), "missing emitted pose");
        control("control-map-surplus-pose", m => m["poses"]["2147483647:0:0"] = m["poses"][key].DeepClone(), "surplus key");
        control("control-map-noncanonical-pose-key", m => m["poses"]["0" + key] = m["poses"][key].DeepClone(), "malformed key");
        control("control-map-pose-index", m => m["poses"][key]["index"] = -1, "malformed value");
        control("control-map-pose-source-index", m => m["poses"][key]["index"] = m["poses"][key]["index"].GetValue<int>() + 1,
            "source identity or index");
        control("control-map-pose-source-member", m => m["poses"][key]["refKey"] = "wrong|actions.xml|/Pose[1]", "source identity or index");
        JsonNode duplicate = JsonNode.Parse(map);
        duplicate["poses"] = null;
        string duplicateObject = "{" + JsonSerializer.Serialize(key) + ":" + JsonNode.Parse(map)["poses"][key].ToJsonString()
            + "," + JsonSerializer.Serialize(key) + ":" + JsonNode.Parse(map)["poses"][key].ToJsonString() + "}";
        string duplicated = duplicate.ToJsonString().Replace("\"poses\":null", "\"poses\":" + duplicateObject);
        results.Add(CheckMap("control-map-duplicate-pose-key", mapPath, document, duplicated, false, "duplicate key"));

        // This valid synthetic placeholder is exempt. The same when=false pose in Stay or MoveWithTurn is not.
        XDocument placeholder = new XDocument(document);
        XElement act = placeholder.Root.Element(Ns + "animations").Elements(Ns + "animation").First(a => a.Element(Ns + "act") != null).Element(Ns + "act");
        act.ReplaceAttributes(new XAttribute("type", "Animate"));
        act.Elements(Ns + "variants").Remove();
        act.Add(new XElement(Ns + "variants", new XElement(Ns + "variant", new XAttribute("when", "false"),
            new XElement(Ns + "pose", new XAttribute("frame", -1), new XAttribute("ticks", 1), new XAttribute("dx", 0), new XAttribute("dy", 0)))));
        XElement placeholderFeatures = placeholder.Root.Element(Ns + "header").Element(Ns + "features");
        if (!placeholderFeatures.Value.Split(' ').Contains(CompanionFormatFeatures.Expressions))
            placeholderFeatures.Value += " " + CompanionFormatFeatures.Expressions;
        string nodeId = act.Parent.Attribute("id").Value;
        JsonNode placeholderMap = JsonNode.Parse(map);
        foreach (string mapped in placeholderMap["poses"].AsObject().Select(p => p.Key).Where(k => k.StartsWith(nodeId + ":", StringComparison.Ordinal)).ToArray())
            placeholderMap["poses"].AsObject().Remove(mapped);
        string placeholderJson = placeholderMap.ToJsonString();
        XDocument exactPlaceholder = new XDocument(placeholder);
        results.Add(Check("witness-map-decision68-placeholder-xml", mapPath, placeholder.ToString(), true));
        results.Add(CheckMap("witness-map-decision68-placeholder", mapPath, placeholder, placeholderJson, true));
        XDocument interactPlaceholder = new XDocument(exactPlaceholder);
        XElement interactAct = interactPlaceholder.Descendants(Ns + "act").First();
        interactAct.SetAttributeValue("type", "Embedded");
        interactAct.SetAttributeValue("class", "Interact");
        XElement interactFeatures = interactPlaceholder.Root.Element(Ns + "header").Element(Ns + "features");
        if (!interactFeatures.Value.Split(' ').Contains(CompanionFormatFeatures.Social))
            interactFeatures.Value += " " + CompanionFormatFeatures.Social;
        results.Add(Check("witness-map-decision68-interact-placeholder-xml", mapPath, interactPlaceholder.ToString(), true));
        results.Add(CheckMap("witness-map-decision68-interact-placeholder", mapPath, interactPlaceholder, placeholderJson, true));
        act.SetAttributeValue("type", "Stay");
        results.Add(CheckMap("control-map-false-stay-unmapped", mapPath, placeholder, placeholderJson, false, "missing emitted pose"));
        act.SetAttributeValue("type", "Embedded");
        act.SetAttributeValue("class", "MoveWithTurn");
        results.Add(CheckMap("control-map-false-turn-unmapped", mapPath, placeholder, placeholderJson, false, "missing emitted pose"));

        // Each candidate changes one conjunct of the exact decision-68 shape. XML acceptance separately proves
        // that the map refusal protects an emitted pose, rather than passing because the XML is invalid.
        Action<string, Action<XElement>> shapeControl = (suffix, mutate) =>
        {
            XDocument candidate = new XDocument(exactPlaceholder);
            XElement candidateAct = candidate.Descendants(Ns + "act").First();
            mutate(candidateAct);
            results.Add(Check("witness-map-placeholder-" + suffix + "-xml", mapPath, candidate.ToString(), true));
            results.Add(CheckMap("control-map-placeholder-" + suffix + "-unmapped", mapPath, candidate, placeholderJson, false, "missing emitted pose"));
        };
        shapeControl("two-variants", candidate =>
        {
            XElement variants = candidate.Element(Ns + "variants");
            variants.Add(new XElement(variants.Element(Ns + "variant")));
        });
        shapeControl("two-poses", candidate =>
        {
            XElement variant = candidate.Element(Ns + "variants").Element(Ns + "variant");
            variant.Add(new XElement(variant.Element(Ns + "pose")));
        });
        XElement visiblePose = document.Descendants(Ns + "pose").First(p => Attribute(p, "frame", -1) >= 0);
        shapeControl("real-frame", candidate =>
        {
            // Keep the visible pose's geometry so a loosened placeholder rule cannot merely reject later for
            // an unrelated frame/anchor discrepancy. Only frame changes among the placeholder predicates.
            XElement pose = candidate.Descendants(Ns + "pose").First();
            pose.ReplaceAttributes(visiblePose.Attributes().Select(attribute => new XAttribute(attribute)));
            pose.SetAttributeValue("ticks", 1);
            pose.SetAttributeValue("dx", 0);
            pose.SetAttributeValue("dy", 0);
        });
        shapeControl("ticks", candidate => candidate.Descendants(Ns + "pose").First().SetAttributeValue("ticks", 2));
        shapeControl("dx", candidate => candidate.Descendants(Ns + "pose").First().SetAttributeValue("dx", 1));
        shapeControl("dy", candidate => candidate.Descendants(Ns + "pose").First().SetAttributeValue("dy", 1));
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
