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
                new { Name = "missing-img-refusal", Run = (Action)MissingImages },
                new { Name = "empty-img-refusal", Run = (Action)EmptyImages },
                new { Name = "invalid-data-member-refusal", Run = (Action)InvalidDataMember },
                new { Name = "hidden-main-refusal", Run = (Action)HiddenMain },
                new { Name = "source-hidden-main-visible", Run = (Action)SourceHiddenMain },
                new { Name = "runtime-entry-warnings", Run = (Action)RuntimeEntryWarnings },
                new { Name = "prepared-member-warnings", Run = (Action)PreparedMemberWarnings },
                new { Name = "startup-schema-mapping", Run = (Action)StartupSchemaMapping },
                new { Name = "startup-unmapped-name", Run = (Action)StartupUnmappedName },
                new { Name = "startup-ambiguous-name", Run = (Action)StartupAmbiguousName },
                new { Name = "missing-next-recovery-option", Run = (Action)MissingNextRecoveryOption },
                new { Name = "skipped-member-api-controls", Run = (Action)SkippedMemberApiControls },
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
                    .SetAttributeValue("Unknown", "1");   // an attribute the source schema does not name: always refused
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

        private static void MissingImages()
        {
            using (var fixture = new Fixture()) Refused(fixture, Options(), "TICK_PACK_REFUSED: Tick pack needs an img directory");
        }

        private static void EmptyImages()
        {
            using (var fixture = new Fixture())
            {
                fixture.EmptyImages();
                Refused(fixture, Options(), "TICK_PACK_REFUSED: Tick pack has no image-set members");
            }
        }

        private static void InvalidDataMember()
        {
            using (var fixture = Single())
            {
                string before = fixture.Inventory(), error;
                PackConversionResult result = PackConverter.Convert(fixture.PackRoot, Options(), XmlData.NodeCopy.Deep,
                    root => { throw new InvalidDataException("synthetic invalid member data"); }, out error);
                Require(result != null && !result.Accepted && error != null && error.Contains("synthetic invalid member data")
                    && result.Members.Count == 1 && result.Members[0].Disposition == PackMemberDisposition.Refused
                    && result.Members[0].Root == null, "InvalidDataException escaped the per-member refusal boundary");
                Require(before == fixture.Inventory(), "invalid-data refusal changed source files");
            }
        }

        private static void HiddenMain()
        {
            foreach (string key in new[] { "Alpha", ".Alpha", "unused" })
                using (var fixture = new Fixture())
                {
                    fixture.Member(key, Actions(), Behaviours());
                    fixture.Member(".Helper", Actions(), Behaviours());
                    var options = new PackConversionOptions { MainMember = key, Members = new Dictionary<string, PackMemberOptions> {
                        { key, new PackMemberOptions { Role = "main", Hidden = true } },
                        { ".Helper", new PackMemberOptions { Role = "helper", Hidden = true } }
                    } };
                    Refused(fixture, options, "TICK_PACK_MAIN_HIDDEN");
                }
        }

        private static void SourceHiddenMain()
        {
            foreach (string key in new[] { ".Alpha", "unused", "UNUSED" })
                using (var fixture = new Fixture())
                {
                    fixture.Member(key, Actions(), Behaviours());
                    fixture.Member(".Helper", Actions(), Behaviours());
                    var options = new PackConversionOptions { MainMember = key };
                    PackConversionResult result = Accepted(fixture, options);
                    Require(!Member(result, key).Root.Header.Pack.Hidden && Member(result, key).Root.Header.Pack.Role == "main"
                        && Member(result, ".Helper").Root.Header.Pack.Hidden,
                        "source-hidden selected main stayed hidden or made another helper visible");
                    options.Members = new Dictionary<string, PackMemberOptions> { { key, new PackMemberOptions { Hidden = false } } };
                    Require(!Member(Accepted(fixture, options), key).Root.Header.Pack.Hidden,
                        "an explicit visible main was refused or hidden");
                }
        }

        private static void RuntimeEntryWarnings()
        {
            foreach (string missing in new[] { "Fall", "Dragged", "Thrown", "all", "none" })
                using (var fixture = new Fixture())
                {
                    XDocument behaviours = Behaviours();
                    foreach (string name in new[] { "Fall", "Dragged", "Thrown" })
                        if (missing != "all" && missing != name)
                            behaviours.Root.Element("BehaviorList").Add(E("Behavior", A("Name", name), A("Action", "Stand"), A("Frequency", 0)));
                    fixture.Member("Alpha", Actions(), behaviours);
                    fixture.Member("Beta", Actions(), Behaviours());
                    PackConversionResult result = Accepted(fixture, Options());
                    foreach (string name in new[] { "Fall", "Dragged", "Thrown" })
                    {
                        string diagnostic = "Image set 'Alpha' has no " + name + " behaviour;";
                        bool expected = missing == "all" || missing == name;
                        Require(result.Warnings.Any(w => w.StartsWith(diagnostic, StringComparison.Ordinal)) == expected,
                            "runtime entry warning did not distinguish absent " + name + " from a present zero-frequency root");
                        Require(Member(result, "Alpha").Residue.Notes.Any(w => w.StartsWith(diagnostic, StringComparison.Ordinal)) == expected,
                            "member residue lost runtime entry warning for " + name);
                        Require(result.Warnings.Any(w => w.StartsWith("Image set 'Beta' has no " + name + " behaviour;", StringComparison.Ordinal)),
                            "a sibling's missing runtime entry warning was lost for " + name);
                    }
                    Require(result.Warnings.Where(w => w.Contains("has no ") && w.Contains(" behaviour;"))
                        .All(w => w.Contains("dispos")), "runtime entry warning did not state the companion is disposed");
                }
        }

        private static void PreparedMemberWarnings()
        {
            using (var fixture = new Fixture())
            {
                foreach (string key in new[] { "Alpha", "Beta" })
                {
                    XDocument behaviours = Behaviours();
                    behaviours.Root.Element("BehaviorList").Elements("Behavior").First().Add(
                        E("NextBehaviorList", A("Add", true), E("BehaviorReference", A("Name", "Missing-" + key),
                            A("Action", "Stand"), A("Frequency", 1))));
                    fixture.Member(key, Actions(), behaviours);
                }
                // Decision 64: the drop is a policy divergence the caller selects; this witness selects it.
                PackConversionOptions recovering = Options();
                recovering.RecoverMissingNext = true;
                PackConversionResult result = Accepted(fixture, recovering);
                CheckPreparedWarnings(result);

                // Preparation succeeded, but a later copy fault must preserve its diagnostics.
                PackConversionOptions options = Options();
                options.RecoverMissingNext = true;
                string before = fixture.Inventory(), error;
                PackConversionResult refused = PackConverter.Convert(fixture.PackRoot, options,
                    root => { throw new InvalidDataException("Injected copy refusal after source preparation."); }, ShimejiEngine.Serialize, out error);
                Require(!refused.Accepted && error != null && refused.Members.All(m => m.Disposition == PackMemberDisposition.Refused),
                    "injected post-preparation refusal did not refuse the pack");
                CheckPreparedWarnings(refused);
                Require(before == fixture.Inventory(), "refused warning conversion changed source files");
            }
        }

        /// <summary>Decision 64: RecoverMissingNext is the caller's choice for one call. Omitted or false refuses a next
        /// entry that names no root behaviour, by name (TICK_NEXT_MISSING: image set, behaviour, entry); true drops it with
        /// one warning in the result and one in the member's residue. Calls do not leak into each other, the caller's
        /// options are not changed, and an entry under a scripted condition (its own or an enclosing one) refuses under
        /// both settings.</summary>
        private static void MissingNextRecoveryOption()
        {
            using (var fixture = new Fixture())
            {
                XDocument behaviours = Behaviours();
                behaviours.Root.Element("BehaviorList").Elements("Behavior").First().Add(
                    E("NextBehaviorList", A("Add", true), E("BehaviorReference", A("Name", "Ghost"), A("Action", "Stand"), A("Frequency", 1))));
                fixture.Member("Alpha", Actions(), behaviours);
                const string Strict = "TICK_NEXT_MISSING: image set 'Alpha', behaviour 'Main': the next entry 'Ghost'";
                PackConversionOptions omitted = Options();
                Require(!omitted.RecoverMissingNext, "RecoverMissingNext does not default to false");
                Refused(fixture, omitted, Strict);
                PackConversionOptions strict = Options();
                strict.RecoverMissingNext = false;
                Refused(fixture, strict, Strict);
                PackConversionOptions recovering = Options();
                recovering.RecoverMissingNext = true;
                PackConversionResult recovered = Accepted(fixture, recovering);
                string dropped = "Image set 'Alpha': Behaviour 'Main': the next entry 'Ghost' names no root behaviour and was dropped";
                Require(recovered.Warnings.Count(w => w.StartsWith(dropped, StringComparison.Ordinal)) == 1
                    && Member(recovered, "Alpha").Residue.Notes.Count(w => w.StartsWith(dropped, StringComparison.Ordinal)) == 1,
                    "a recovered conversion did not report its dropped entry exactly once in the result and once in the residue");
                // Independent calls: strict after recovery refuses, recovery after strict accepts; options unchanged.
                Refused(fixture, strict, Strict);
                Require(Accepted(fixture, recovering).Accepted && recovering.RecoverMissingNext && !strict.RecoverMissingNext && !omitted.RecoverMissingNext,
                    "a conversion changed its caller's RecoverMissingNext, or one call's choice reached another");
            }
            foreach (bool recover in new[] { false, true })
                foreach (bool enclosing in new[] { false, true })
                    using (var fixture = new Fixture())
                    {
                        XElement entry = E("BehaviorReference", A("Name", "Ghost"), A("Action", "Stand"), A("Frequency", 1));
                        if (!enclosing) entry.Add(A("Condition", "${mascot.lookRight}"));
                        XDocument behaviours = Behaviours();
                        behaviours.Root.Element("BehaviorList").Elements("Behavior").First().Add(E("NextBehaviorList", A("Add", true),
                            enclosing ? E("Condition", A("Condition", "${mascot.lookRight}"), entry) : entry));
                        fixture.Member("Alpha", Actions(), behaviours);
                        PackConversionOptions options = Options();
                        options.RecoverMissingNext = recover;
                        Refused(fixture, options, "TICK_NEXT_MISSING_SCRIPTED");
                    }
        }

        private static void CheckPreparedWarnings(PackConversionResult result)
        {
            foreach (string key in new[] { "Alpha", "Beta" })
            {
                string prefix = "Image set '" + key + "': Behaviour 'Main': the next entry 'Missing-" + key + "'";
                Require(result.Warnings.Count(w => w.StartsWith(prefix, StringComparison.Ordinal) && w.Contains("was dropped")) == 1,
                    "prepared-member warning missing, duplicated or missing its member identity: " + key);
                Require(Member(result, key).Residue.Notes.Count(w => w.StartsWith(prefix, StringComparison.Ordinal)) == 1,
                    "member residue lost its prepared warning: " + key);
                Require(!Member(result, key).Residue.Notes.Any(w => w.Contains("Missing-" + (key == "Alpha" ? "Beta" : "Alpha"))),
                    "prepared-member warning leaked into a sibling's residue: " + key);
            }
        }

        private static XDocument JapaneseBehaviours()
        {
            XElement list = E("行動リスト", E("行動", A("名前", "Main"), A("動作", "Stand"), A("頻度", 1)));
            foreach (string name in new[] { "落下する", "ドラッグされる", "投げられる" })
                list.Add(E("行動", A("名前", name), A("動作", "Stand"), A("頻度", 0)));
            return new XDocument(E("Mascot", list));
        }

        private static void StartupSchemaMapping()
        {
            using (var fixture = new Fixture())
            {
                fixture.Member("Alpha", Actions(), Behaviours());
                fixture.Member("Beta", Actions(), JapaneseBehaviours());
                string[] sourceNames = { "落下する", "ドラッグされる", "投げられる", "Main", "Fall" };
                string[] emittedNames = { "Fall", "Dragged", "Thrown", "Main", "Fall" };
                for (int index = 0; index < sourceNames.Length; index++)
                {
                    PackConversionOptions options = Options();
                    options.Startup = new PackStartupOptions { Member = "Beta", Behaviour = sourceNames[index] };
                    PackConversionResult result = Accepted(fixture, options);
                    XElement startup = Document(Member(result, "Alpha")).Root.Element(Ns + "header").Element(Ns + "startup");
                    Require((string)startup.Attribute("member") == "Beta" && (string)startup.Attribute("behaviour") == emittedNames[index],
                        "startup did not use the target member's emitted root name: " + sourceNames[index]);
                    Require(options.Startup.Member == "Beta" && options.Startup.Behaviour == sourceNames[index],
                        "startup mapping mutated caller-owned options");
                }
            }
        }

        private static void StartupUnmappedName()
        {
            using (var fixture = new Fixture())
            {
                fixture.Member("Alpha", Actions(), Behaviours());
                fixture.Member("Beta", Actions(), JapaneseBehaviours());
                foreach (var request in new[] { new { Member = "Beta", Name = "Absent" }, new { Member = "Alpha", Name = "落下する" },
                    new { Member = "Beta", Name = "fall" } })
                {
                    PackConversionOptions options = Options();
                    options.Startup = new PackStartupOptions { Member = request.Member, Behaviour = request.Name };
                    PackConversionResult result = Refused(fixture, options, "TICK_PACK_STARTUP_UNMAPPED");
                    Require(result.Errors.Any(e => e.Contains("image set '" + request.Member + "'") && e.Contains("'" + request.Name + "'")),
                        "unmapped startup refusal did not name both member and requested behaviour");
                }
            }
        }

        private static void StartupAmbiguousName()
        {
            // The emitter rejects its present schema collisions. Exercise the API guard independently so a future
            // mapping cannot make an emitted alias silently select another source root.
            var target = new Emit.TickPackEmitter.PreparedMember { Name = "Beta", BehaviourNames = new Dictionary<string, string> {
                { "First", "Second" }, { "Second", "Third" }
            } };
            string error;
            string mapped = PackConverter.ResolveStartupBehaviour(target, "Second", out error);
            Require(mapped == null && error != null && error.Contains("TICK_PACK_STARTUP_AMBIGUOUS") && error.Contains("'Beta'") && error.Contains("'Second'"),
                "ambiguous startup alias did not refuse with member and requested name");
            Require(PackConverter.ResolveStartupBehaviour(target, "First", out error) == "Second" && error == null,
                "an unambiguous source name was refused");
            Require(PackConverter.ResolveStartupBehaviour(target, "Third", out error) == "Third" && error == null,
                "an unambiguous emitted name was refused");
        }

        // Decision 69: written by Codex in its decision 64/69 re-check (Codex-SkippedApiCases.cs.txt); renamed and
        // registered here, otherwise as written.
        private static void SkippedMemberApiControls()
        {
            using (var fixture = new Fixture())
            {
                fixture.Member("Alpha", Actions(), Behaviours());
                fixture.Member("Beta", Actions(), Behaviours());
                fixture.Member("Gamma", Actions(), Behaviours());
                CodexRemoveConfiguration(fixture, "Beta");
                CodexRemoveConfiguration(fixture, "Gamma");
                PackConversionOptions options = Options();
                var betaOptions = new PackMemberOptions { Role = "helper", Hidden = true };
                options.Members = new Dictionary<string, PackMemberOptions> { { "Beta", betaOptions } };
                PackConversionResult accepted = Accepted(fixture, options);
                Require(accepted.Members.Count == 1 && accepted.Members[0].SourceKey == "Alpha" && accepted.PackId != null,
                    "skipped folders leaked a usable member or lost the loaded pack identity");
                CodexSkippedWarning(accepted, "Beta");
                CodexSkippedWarning(accepted, "Gamma");
                Require(options.Members.Count == 1 && ReferenceEquals(options.Members["Beta"], betaOptions)
                    && betaOptions.Role == "helper" && betaOptions.Hidden == true && !options.RecoverMissingNext,
                    "skip handling changed caller metadata or recovery choice");

                PackConversionOptions main = new PackConversionOptions { MainMember = "Beta" };
                PackConversionResult mainResult = Refused(fixture, main, "TICK_PACK_MAIN_SKIPPED");
                Require(!mainResult.Errors.Any(e => e.Contains("TICK_PACK_MAIN_UNKNOWN")), "skipped main was also reported unknown");
                CodexSkippedWarning(mainResult, "Beta");
                PackConversionOptions startup = Options();
                startup.Startup = new PackStartupOptions { Member = "Beta", Behaviour = "Main" };
                PackConversionResult startupResult = Refused(fixture, startup, "TICK_PACK_STARTUP_SKIPPED");
                CodexSkippedWarning(startupResult, "Beta");
                Require(startup.Startup.Member == "Beta" && startup.Startup.Behaviour == "Main", "skipped startup changed caller options");

                PackConversionOptions unknown = Options();
                unknown.Members = new Dictionary<string, PackMemberOptions> { { "NeverPresent", new PackMemberOptions() } };
                Refused(fixture, unknown, "TICK_PACK_OPTIONS_UNKNOWN");
                unknown.Members = new Dictionary<string, PackMemberOptions> { { "beta", new PackMemberOptions() } };
                Refused(fixture, unknown, "TICK_PACK_OPTIONS_UNKNOWN");
                Refused(fixture, new PackConversionOptions { MainMember = "NeverPresent" }, "TICK_PACK_MAIN_UNKNOWN");
            }
            using (var fixture = new Fixture())
            {
                fixture.Member("Alpha", Actions(), Behaviours());
                CodexRemoveConfiguration(fixture, "Alpha");
                PackConversionResult result = Refused(fixture, Options(), "TICK_PACK_MAIN_SKIPPED");
                Require(result.PackId == null && result.Members.Count == 0, "an all-skipped pack exposed a usable identity or member");
                CodexSkippedWarning(result, "Alpha");
            }
            using (var fixture = new Fixture())
            {
                fixture.Member("Alpha", Actions(), Behaviours());
                fixture.Member("Beta", Actions(), Behaviours());
                CodexRemoveConfiguration(fixture, "Beta");
                XDocument bad = Actions();
                bad.Root.Element("ActionList").Elements("Action").First().SetAttributeValue("Type", "CodexUnsupportedType");
                fixture.Member("Gamma", bad, Behaviours());
                PackConversionResult result = Refused(fixture, Options(), "Gamma");
                Require(Member(result, "Gamma").Disposition == PackMemberDisposition.Refused && result.PackId == null,
                    "a malformed configured sibling was silently skipped or assigned a complete pack identity");
                CodexSkippedWarning(result, "Beta");
            }
            using (var fixture = new Fixture())
            {
                fixture.Member("Alpha", Actions("Beta"), Behaviours());
                fixture.Member("Beta", Actions(), Behaviours());
                CodexRemoveConfiguration(fixture, "Beta");
                PackConversionResult result = Accepted(fixture, Options());
                CodexSkippedWarning(result, "Beta");
                Require(result.Members.All(m => m.SourceKey != "Beta")
                    && result.Warnings.Any(w => w.Contains("Beta") && !w.Contains("TICK_MEMBER_SKIPPED")),
                    "a loaded member's reference to a skipped sibling lost the missing-member diagnostic or gained a placeholder");
            }
        }

        private static void CodexSkippedWarning(PackConversionResult result, string key)
        {
            Require(result.Warnings.Count(w => w.StartsWith("Image set '" + key + "':", StringComparison.Ordinal)
                && w.Contains("TICK_MEMBER_SKIPPED")) == 1, "skipped warning lost or duplicated: " + key);
        }

        private static void CodexRemoveConfiguration(Fixture fixture, string key)
        {
            string conf = Path.GetFullPath(Path.Combine(fixture.PackRoot, "img", key, "conf"));
            Require(conf.StartsWith(Path.GetFullPath(fixture.PackRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "synthetic configuration removal escaped its fixture");
            File.Delete(Path.Combine(conf, "actions.xml"));
            File.Delete(Path.Combine(conf, "behaviors.xml"));
            Directory.Delete(conf, false);
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
            internal void EmptyImages() { MakeDirectory(Path.Combine(PackRoot, "img")); }
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
