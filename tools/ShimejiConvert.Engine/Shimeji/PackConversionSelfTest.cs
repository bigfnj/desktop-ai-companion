using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>Exercises the public in-memory API using original synthetic packs and the real validators.</summary>
    public static class PackConversionSelfTest
    {
        public static bool Run(out string detail) { return Run(out detail, null); }

        public static bool Run(out string detail, string onlyCase)
        {
            var output = new StringBuilder("pack-conversion self-test: executing public API witnesses\n");
            var cases = new[] {
                new { Name = "complete-pack-startup-and-geometry", Run = (Action)CompletePack },
                new { Name = "missing-member-warning", Run = (Action)MissingMemberWarning },
                new { Name = "missing-main-refusal", Run = (Action)MissingMain },
                new { Name = "unknown-option-refusal", Run = (Action)UnknownOption },
                new { Name = "startup-member-refusal", Run = (Action)StartupMember },
                new { Name = "startup-behaviour-refusal", Run = (Action)StartupBehaviour },
                new { Name = "egg-return-refusal", Run = (Action)EggReturn },
                new { Name = "format-validation-refusal", Run = (Action)FormatValidation },
                new { Name = "deep-copy-fault-refusal", Run = (Action)DeepCopyFault },
                new { Name = "fixed-point-fault-refusal", Run = (Action)FixedPointFault },
                new { Name = "member-refusal-no-write", Run = (Action)MemberRefusal },
                new { Name = "independent-results", Run = (Action)IndependentResults },
                new { Name = "unused-action-diagnostic", Run = (Action)UnusedAction },
                new { Name = "optional-parity-map", Run = (Action)OptionalParityMap },
                new { Name = "long-member-key", Run = (Action)LongMemberKey },
            };
            if (onlyCase != null && !cases.Any(c => c.Name == onlyCase))
            { detail = "pack-conversion unknown case: " + onlyCase; return false; }
            foreach (var item in cases.Where(c => onlyCase == null || c.Name == onlyCase))
            {
                try { item.Run(); output.AppendLine("  pack-conversion PASS " + item.Name); }
                catch (Exception error)
                {
                    output.AppendLine("  FAIL PackConversionSelfTest/" + item.Name + ": " + error.Message);
                    detail = output.ToString().TrimEnd(); return false;
                }
            }
            detail = output.ToString().TrimEnd(); return true;
        }

        private static readonly XNamespace Ns = "https://esheep.petrucci.ch/";
        private static void Require(bool condition, string witness)
        { if (!condition) throw new InvalidOperationException(witness); }

        private static PackMemberConversionResult Member(PackConversionResult result, string key)
        { return result.Members.Single(m => m.SourceKey == key); }
        private static XElement Node(XDocument document, string name)
        { return document.Root.Element(Ns + "animations").Elements(Ns + "animation").Single(n => (string)n.Element(Ns + "name") == name); }
        private static XElement Pack(XDocument document)
        { return document.Root.Element(Ns + "header").Element(Ns + "pack"); }
        private static XDocument Document(PackMemberConversionResult member)
        { return XDocument.Parse(member.EmittedXml); }

        private static PackConversionResult Accepted(Fixture fixture, PackConversionOptions options)
        {
            string before = fixture.Inventory();
            string error;
            PackConversionResult result = ShimejiEngine.ConvertPack(fixture.PackRoot, options, out error);
            Require(result != null && result.Accepted && error == null && result.Error == null && result.Errors.Count == 0,
                "public pack conversion refused a valid source: " + error);
            Require(result.Members.Count > 0 && result.Members.All(m => m.Valid && m.RoundTrips && m.Root != null
                && m.Disposition == PackMemberDisposition.Prepared && !string.IsNullOrEmpty(m.EmittedXml)),
                "accepted pack has an unvalidated or unusable member");
            Require(before == fixture.Inventory(), "public pack conversion wrote a file or changed source bytes");
            return result;
        }

        private static PackConversionResult Refused(Fixture fixture, PackConversionOptions options, string diagnostic)
        {
            string before = fixture.Inventory();
            string error;
            PackConversionResult result = ShimejiEngine.ConvertPack(fixture.PackRoot, options, out error);
            Require(result != null && !result.Accepted && result.Errors.Count > 0 && !string.IsNullOrEmpty(error)
                && error == result.Error, "invalid pack was accepted or did not return its diagnostic");
            Require(result.Errors.Any(e => e.IndexOf(diagnostic, StringComparison.OrdinalIgnoreCase) >= 0),
                "refusal did not identify " + diagnostic + ": " + error);
            Require(result.Members.Where(m => m.Disposition == PackMemberDisposition.Refused)
                .All(m => m.Root == null && m.EmittedXml == null && m.ParityMapJson == null),
                "refused member exposed a usable artifact");
            Require(before == fixture.Inventory(), "refused public conversion wrote files or changed source bytes");
            return result;
        }

        private static PackConversionOptions Options(bool map = false)
        { return new PackConversionOptions { MainMember = "Alpha", IncludeParityMaps = map }; }

        private static void CompletePack()
        {
            using (var fixture = new Fixture())
            {
                fixture.Member("Alpha", Actions(), Behaviours());
                fixture.Member("Egg (2)", Actions("Alpha"), Behaviours());
                fixture.Member(".Helper", Actions(), Behaviours());
                PackConversionOptions options = Options(true);
                options.Members = new Dictionary<string, PackMemberOptions> {
                    { "Egg (2)", new PackMemberOptions { Role = "egg", Hidden = true } },
                    { "Alpha", new PackMemberOptions { Role = "main", Hidden = false } }
                };
                options.Startup = new PackStartupOptions { Member = "Egg (2)", Behaviour = "Hatch" };
                PackConversionResult result = Accepted(fixture, options);
                Require(result.Members.Select(m => m.SourceKey).SequenceEqual(new[] { ".Helper", "Alpha", "Egg (2)" }),
                    "public members lost source ordinal order or exact keys");
                Require(!string.IsNullOrEmpty(result.PackId) && result.MainMember == "Alpha", "pack identity or selected main was lost");
                foreach (PackMemberConversionResult member in result.Members)
                {
                    XElement pack = Pack(Document(member));
                    Require((string)pack.Attribute("id") == result.PackId && (string)pack.Attribute("member") == member.SourceKey,
                        "member metadata does not share the source pack identity");
                    Require(member.Residue != null && member.Residue.Dropped.Count == 0 && member.Residue.Degraded.Count == 0,
                        "literal member was reported as dropped or degraded");
                }
                XElement main = Pack(Document(Member(result, "Alpha")));
                Require((string)main.Attribute("role") == "main" && (string)main.Attribute("hidden") == "false",
                    "explicit visible main metadata did not survive serialization");
                XElement helper = Pack(Document(Member(result, ".Helper")));
                Require((string)helper.Attribute("role") == "helper" && (string)helper.Attribute("hidden") == "true",
                    "source-hidden helper became a visible form");
                XElement egg = Pack(Document(Member(result, "Egg (2)")));
                Require((string)egg.Attribute("role") == "egg" && (string)egg.Attribute("hidden") == "true", "egg role or visibility lost");
                XElement startup = Document(Member(result, "Alpha")).Root.Element(Ns + "header").Element(Ns + "startup");
                Require(startup != null && (string)startup.Attribute("member") == "Egg (2)" && (string)startup.Attribute("behaviour") == "Hatch"
                    && result.Members.Where(m => m.SourceKey != "Alpha").All(m => Document(m).Root.Element(Ns + "header").Element(Ns + "startup") == null),
                    "startup metadata did not stay on the main member");
                XDocument document = Document(Member(result, "Alpha"));
                XElement[] poses = Node(document, "Stand").Element(Ns + "act").Element(Ns + "variants").Element(Ns + "variant").Elements(Ns + "pose").ToArray();
                Require(poses.Length == 2 && (int)poses[0].Attribute("w") == 4 && (int)poses[0].Attribute("h") == 6
                    && (int)poses[0].Attribute("rw") == 5 && (int)poses[0].Attribute("rh") == 9
                    && poses[0].Attribute("right") != null && (int)poses[1].Attribute("w") == 7 && (int)poses[1].Attribute("h") == 4,
                    "mixed left/right image geometry did not reach the public result");
                XElement child = Node(document, "Composite").Element(Ns + "act").Element(Ns + "children").Element(Ns + "child");
                Require((string)child.Attribute("animation") == "Stand" && child.Elements(Ns + "bind").Any(b =>
                    (string)b.Attribute("name") == "Duration" && (string)b.Attribute("value") == "7" && (string)b.Attribute("cache") == "none"),
                    "composite literal reference binding was lost");
                using (JsonDocument map = JsonDocument.Parse(Member(result, "Alpha").ParityMapJson))
                {
                    Require(map.RootElement.GetProperty("schema").GetString() == "shimeji-parity-map/1"
                        && map.RootElement.GetProperty("frames").GetArrayLength() == 3,
                        "requested public parity map was absent or lost the explicit right image");
                }
            }
        }

        private static void MissingMemberWarning()
        {
            using (var fixture = new Fixture())
            {
                fixture.Member("Alpha", Actions("Missing form"), Behaviours());
                PackConversionResult result = Accepted(fixture, Options());
                Require(result.Warnings.Any(w => w.IndexOf("Missing form", StringComparison.Ordinal) >= 0),
                    "missing target fallback did not produce a pack warning");
                XElement action = Node(Document(Member(result, "Alpha")), "Change").Element(Ns + "act");
                Require(action.Elements(Ns + "param").Any(p => (string)p.Attribute("name") == "TransformMascot"
                    && (string)p.Attribute("value") == "Missing form"), "missing-member warning erased the source reference");
            }
        }

        private static void MissingMain()
        {
            using (var fixture = Single())
            {
                Refused(fixture, new PackConversionOptions(), "TICK_PACK_MAIN_REQUIRED");
                Refused(fixture, new PackConversionOptions { MainMember = "alpha" }, "TICK_PACK_MAIN_UNKNOWN");
            }
        }
        private static void UnknownOption()
        {
            using (var fixture = Single())
            {
                PackConversionOptions options = Options();
                options.Members = new Dictionary<string, PackMemberOptions> { { "Absent", new PackMemberOptions { Role = "form" } } };
                Refused(fixture, options, "Absent");
                options.Members = new Dictionary<string, PackMemberOptions> { { "Alpha", new PackMemberOptions { Role = "mystery" } } };
                Refused(fixture, options, "role");
            }
        }
        private static void StartupMember()
        {
            using (var fixture = Single())
            {
                PackConversionOptions options = Options();
                options.Startup = new PackStartupOptions { Member = "Absent", Behaviour = "Main" };
                Refused(fixture, options, "Absent");
            }
        }
        private static void StartupBehaviour()
        {
            using (var fixture = Single())
            {
                PackConversionOptions options = Options();
                options.Startup = new PackStartupOptions { Member = "Alpha", Behaviour = "Absent" };
                Refused(fixture, options, "Absent");
            }
        }
        private static void EggReturn()
        {
            using (var fixture = Single())
            {
                fixture.Member("Egg (2)", Actions(), Behaviours());
                PackConversionOptions options = Options();
                options.Members = new Dictionary<string, PackMemberOptions> { { "Egg (2)", new PackMemberOptions { Role = "egg", Hidden = true } } };
                options.Startup = new PackStartupOptions { Member = "Egg (2)", Behaviour = "Hatch" };
                Refused(fixture, options, "egg");
            }
        }

        private static void MemberRefusal()
        {
            using (var fixture = Single())
            {
                XDocument invalid = Actions();
                invalid.Root.Element("ActionList").Elements("Action").Single(a => (string)a.Attribute("Name") == "Stand")
                    .SetAttributeValue("Duration", "${1 + 2}");
                fixture.Member("Bad", invalid, Behaviours());
                PackConversionResult result = Refused(fixture, Options(true), "Bad");
                Require(result.PackId == null, "partial source pack advertised an installable subset identity");
                Require(result.Members.Count == 2 && Member(result, "Bad").Disposition == PackMemberDisposition.Refused
                    && Member(result, "Alpha").Disposition == PackMemberDisposition.Prepared && Member(result, "Alpha").Root != null,
                    "one refused member erased successful diagnostic previews or disappeared from reports");
                Require(Member(result, "Bad").Residue != null && Member(result, "Bad").Residue.Notes.Count > 0,
                    "refused member lost its residue diagnostic");
            }
        }

        private static void FormatValidation()
        {
            using (var fixture = new Fixture())
            {
                XDocument source = Actions();
                source.Root.Element("ActionList").Elements("Action").Single(a => (string)a.Attribute("Name") == "Unused")
                    .SetAttributeValue("Name", " ");
                fixture.Member("Alpha", source, Behaviours());
                PackConversionResult result = Refused(fixture, Options(), "validation failed");
                Require(result.Members.Count == 1 && !result.Members[0].Valid && !result.Members[0].RoundTrips,
                    "the real format validator did not reject a blank action name");
            }
        }

        private static void DeepCopyFault()
        {
            using (var fixture = Single())
            {
                string before = fixture.Inventory(), error;
                PackConversionResult result = PackConverter.Convert(fixture.PackRoot, Options(), root =>
                {
                    XmlData.RootNode copied = XmlData.NodeCopy.Deep(root);
                    copied.Animations.Animation.First(n => n.Name == "Stand").Act.Variants.Variant[0].Pose[0].AnchorX++;
                    return copied;
                }, ShimejiEngine.Serialize, out error);
                Require(result != null && !result.Accepted && error != null && error.Contains("deep copy changed"),
                    "a still-valid but lossy deep copy escaped the equality guard");
                Require(result.Members.Count == 1 && result.Members[0].Valid && !result.Members[0].RoundTrips
                    && result.Members[0].Root == null && result.Members[0].EmittedXml == null,
                    "deep-copy refusal exposed a usable document or lost its validation result");
                Require(before == fixture.Inventory(), "deep-copy fault wrote files");
                Accepted(fixture, Options());
            }
        }

        private static void FixedPointFault()
        {
            using (var fixture = Single())
            {
                string before = fixture.Inventory(), error;
                int calls = 0;
                PackConversionResult result = PackConverter.Convert(fixture.PackRoot, Options(), XmlData.NodeCopy.Deep,
                    root =>
                    {
                        calls++;
                        if (calls != 3) return ShimejiEngine.Serialize(root);
                        XmlData.RootNode changed = XmlData.NodeCopy.Deep(root);
                        changed.Header.Version = "2.0";
                        return ShimejiEngine.Serialize(changed);
                    }, out error);
                Require(calls == 3 && result != null && !result.Accepted && error != null && error.Contains("not a fixed point"),
                    "a later serialization change escaped the fixed-point guard");
                Require(result.Members.Count == 1 && result.Members[0].Valid && !result.Members[0].RoundTrips
                    && result.Members[0].Root == null && result.Members[0].EmittedXml == null,
                    "fixed-point refusal exposed a usable document or lost its validation result");
                Require(before == fixture.Inventory(), "fixed-point fault wrote files");
                Accepted(fixture, Options());
            }
        }

        private static void IndependentResults()
        {
            using (var fixture = Single())
            {
                fixture.Member("Beta", Actions(), Behaviours());
                PackConversionOptions options = Options(true);
                options.Members = new Dictionary<string, PackMemberOptions> { { "Beta", new PackMemberOptions { Role = "helper", Hidden = true } } };
                PackConversionResult first = Accepted(fixture, options);
                PackConversionResult other = Accepted(fixture, options);
                PackMemberConversionResult alpha = Member(first, "Alpha"), beta = Member(first, "Beta");
                XmlData.PoseNode alphaPose = alpha.Root.Animations.Animation.First(n => n.Name == "Stand").Act.Variants.Variant[0].Pose[0];
                string originalXml = alpha.EmittedXml;
                alpha.Root.Header.Pack.Member = "Changed";
                alpha.Root.Header.Features = "changed";
                alphaPose.AnchorX = 999;
                alpha.Root.Animations.Animation.First(n => n.Name == "Composite").Act.Children.Child[0].Bind[0].Value = "999";
                options.MainMember = "Changed"; options.Members["Beta"].Role = "egg"; options.Members.Clear();
                Require(first.MainMember == "Alpha" && beta.Root.Header.Pack.Member == "Beta" && beta.Root.Header.Pack.Role == "helper"
                    && beta.Root.Animations.Animation.First(n => n.Name == "Stand").Act.Variants.Variant[0].Pose[0].AnchorX != 999,
                    "public roots or chosen metadata share mutable state across members or options");
                Require(alpha.EmittedXml == originalXml && Member(other, "Alpha").Root.Header.Pack.Member == "Alpha"
                    && Member(other, "Alpha").Root.Animations.Animation.First(n => n.Name == "Stand").Act.Variants.Variant[0].Pose[0].AnchorX != 999,
                    "mutating one preview changed immutable XML or a separate result");
                PackConversionResult later = Accepted(fixture, Options(true));
                Require(Member(later, "Alpha").EmittedXml == originalXml && Member(later, "Alpha").Root.Header.Pack.Member == "Alpha",
                    "later conversion reused a mutated document");
            }
        }

        private static void UnusedAction()
        {
            using (var fixture = Single())
            {
                PackMemberConversionResult member = Member(Accepted(fixture, Options()), "Alpha");
                int unused = (int)Node(Document(member), "Unused").Attribute("id");
                Require(member.Graph != null && member.Graph.Unreachable.Contains(unused),
                    "unused source action disappeared from the graph diagnostic");
                Require(member.Residue.Dropped.Count == 0 && member.Residue.Degraded.Count == 0,
                    "unused source action was mislabeled as lost");
            }
        }
        private static void OptionalParityMap()
        {
            using (var fixture = Single())
            {
                Require(Member(Accepted(fixture, Options()), "Alpha").ParityMapJson == null,
                    "unrequested parity map was exposed by the public API");
                Require(!string.IsNullOrEmpty(Member(Accepted(fixture, Options(true)), "Alpha").ParityMapJson),
                    "requested parity map was not returned");
            }
        }

        private static void LongMemberKey()
        {
            using (var fixture = new Fixture(true))
            {
                string key = new string('L', 130);
                Require(Path.Combine(fixture.PackRoot, "img", key, "p.png").Length > 260,
                    "long member sprite path witness did not exceed MAX_PATH");
                fixture.Member(key, Actions(), Behaviours());
                PackConversionResult result = Accepted(fixture, new PackConversionOptions { MainMember = key, IncludeParityMaps = true });
                PackMemberConversionResult member = Member(result, key);
                Require(result.MainMember == key && member.Root.Header.Pack.Member == key
                    && (string)Document(member).Root.Element(Ns + "header").Element(Ns + "title") == key,
                    "display truncation changed the exact source member identity");
                Require(member.Root.Header.Petname.Length == 128, "long source key did not produce a schema-sized display name");
                using (JsonDocument map = JsonDocument.Parse(member.ParityMapJson))
                    Require(map.RootElement.GetProperty("imageSet").GetString() == key, "display truncation changed the parity image-set key");
            }
        }

        private static XElement E(string name, params object[] content) { return new XElement(name, content); }
        private static XAttribute A(string name, object value) { return new XAttribute(name, value); }
        private static XElement Pose(string image, string anchor, int ticks, string right = null)
        {
            XElement pose = E("Pose", A("Image", image), A("ImageAnchor", anchor), A("Duration", ticks), A("Velocity", "0,0"));
            if (right != null) pose.Add(A("ImageRight", right));
            return pose;
        }
        private static XDocument Actions(string target = null)
        {
            XElement actions = E("ActionList",
                E("Action", A("Name", "Composite"), A("Type", "Sequence"), E("ActionReference", A("Name", "Stand"), A("Duration", 7))),
                E("Action", A("Name", "Stand"), A("Type", "Stay"), E("Animation", Pose("/p.png", "2,3", 2, "/r.png"), Pose("/q.png", "-2,4", 3))),
                E("Action", A("Name", "Unused"), A("Type", "Stay"), E("Animation", Pose("/q.png", "-2,4", 1))));
            if (target != null) actions.Add(E("Action", A("Name", "Change"), A("Type", "Embedded"),
                A("Class", "com.group_finity.mascot.action.Transform"), A("TransformMascot", target), A("TransformBehavior", "Main"),
                A("Duration", 2), E("Animation", Pose("/p.png", "2,3", 2))));
            return new XDocument(E("Mascot", actions));
        }
        private static XDocument Behaviours()
        {
            return new XDocument(E("Mascot", E("BehaviorList",
                E("Behavior", A("Name", "Main"), A("Action", "Composite"), A("Frequency", 1)),
                E("Behavior", A("Name", "Hatch"), A("Action", "Stand"), A("Frequency", 0)))));
        }
        private static Fixture Single()
        {
            var fixture = new Fixture();
            try { fixture.Member("Alpha", Actions(), Behaviours()); return fixture; }
            catch { fixture.Dispose(); throw; }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _root;
            internal readonly string PackRoot;
            private readonly List<string> _files = new List<string>();
            private readonly List<string> _directories = new List<string>();
            private readonly bool _longPaths;
            internal Fixture(bool longPaths = false)
            {
                _longPaths = longPaths;
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                _root = Path.Combine(temp, "pack-api-" + Guid.NewGuid().ToString("N"));
                Require(string.Equals(Path.GetDirectoryName(Path.GetFullPath(_root)), temp, StringComparison.OrdinalIgnoreCase), "pack fixture escaped task temp");
                Preflight(_root);
                PackRoot = longPaths ? Path.Combine(_root, new string('N', 120), "source") : Path.Combine(_root, "source");
                MakeDirectory(PackRoot);
            }
            private static void Preflight(string path)
            {
                string full = Path.GetFullPath(path), current = Path.GetPathRoot(full), previous = "";
                foreach (string part in full.Substring(current.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string leaf = part.ToLowerInvariant();
                    Require(!(leaf.EndsWith(".env", StringComparison.Ordinal) || leaf.StartsWith(".env.", StringComparison.Ordinal)
                        || leaf == ".npmrc" || (previous == ".git" && leaf == "credentials") || leaf.EndsWith(".pem", StringComparison.Ordinal)
                        || leaf.EndsWith(".key", StringComparison.Ordinal) || leaf.EndsWith(".p12", StringComparison.Ordinal)
                        || leaf.EndsWith(".pfx", StringComparison.Ordinal) || leaf.EndsWith(".keystore", StringComparison.Ordinal)
                        || leaf.StartsWith("privatekey", StringComparison.Ordinal)), "pack fixture protected path refused");
                    current = Path.Combine(current, part);
                    if (File.Exists(current) || Directory.Exists(current))
                        Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "pack fixture reparse path refused");
                    previous = leaf;
                }
            }
            private string Inside(string path)
            {
                string full = Path.GetFullPath(path);
                Require(full == _root || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "pack fixture operation escaped root");
                Preflight(full); return full;
            }
            private void MakeDirectory(string path)
            {
                Inside(path);
                if (Directory.Exists(path)) return;
                string parent = Path.GetDirectoryName(path);
                if (path != _root && !Directory.Exists(parent)) MakeDirectory(parent);
                Directory.CreateDirectory(path); _directories.Add(path);
            }
            internal void Member(string key, XDocument actions, XDocument behaviours)
            {
                string member = Path.Combine(PackRoot, "img", key), conf = Path.Combine(member, "conf");
                MakeDirectory(conf);
                Write(Path.Combine(conf, "actions.xml"), actions.ToString());
                if (actions.Root.Element("ActionList").Elements("Action").Any(a => (string)a.Attribute("Name") == "Change"))
                    behaviours.Root.Element("BehaviorList").Elements("Behavior").Single(b => (string)b.Attribute("Name") == "Hatch").SetAttributeValue("Action", "Change");
                Write(Path.Combine(conf, "behaviors.xml"), behaviours.ToString());
                Image(Path.Combine(member, "p.png"), 4, 6, Color.Magenta);
                Image(Path.Combine(member, "q.png"), 7, 4, Color.Cyan);
                Image(Path.Combine(member, "r.png"), 5, 9, Color.Orange);
            }
            private void Write(string path, string text)
            { Inside(path); File.WriteAllText(path, text, new UTF8Encoding(false)); _files.Add(path); }
            private void Image(string path, int width, int height, Color color)
            {
                Inside(path);
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    bitmap.SetPixel(0, 0, color); bitmap.SetPixel(width - 1, height - 1, Color.Lime);
                    if (_longPaths)
                    {
                        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                            bitmap.Save(stream, ImageFormat.Png);
                    }
                    else bitmap.Save(path, ImageFormat.Png);
                }
                _files.Add(path);
            }
            internal string Inventory()
            {
                var entries = new List<string>();
                var pending = new Stack<string>(); pending.Push(_root);
                using (SHA256 hash = SHA256.Create())
                {
                    while (pending.Count > 0)
                        foreach (string path in Directory.EnumerateFileSystemEntries(Inside(pending.Pop())))
                        {
                            Inside(path);
                            if (Directory.Exists(path)) { entries.Add("D:" + path); pending.Push(path); }
                            else entries.Add("F:" + path + ":" + Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path))));
                        }
                }
                entries.Sort(StringComparer.Ordinal); return string.Join("\n", entries);
            }
            public void Dispose()
            {
                foreach (string path in _files) if (File.Exists(Inside(path))) File.Delete(path);
                foreach (string path in _directories.OrderByDescending(p => p.Length))
                    if (Directory.Exists(Inside(path))) Directory.Delete(path, false);
                Require(!Directory.Exists(_root), "temporary pack API fixture remains");
            }
        }
    }
}
