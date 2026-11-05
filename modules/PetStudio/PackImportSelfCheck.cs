using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;
using DesktopAICompanion.Tools.ShimejiConvert;
using DesktopAICompanion.Tools.ShimejiConvert.Shimeji;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// The whole-pack import's checks (1.3.0), run by PetStudioModule.SelfTest. The rules are driven through
    /// <see cref="PackImport"/> with a counting converter and a recording ICompanionManager; the real converter runs on
    /// a SYNTHETIC pack written under this run's TEMP (three image sets of generated pixel blocks and a minimal conf,
    /// the shape of the converter's own PackConversionSelfTest fixture), never on a real skin. Every label is unique
    /// and its variable part comes last, because tests/mutate-selftest-guards.py matches labels by substring.
    /// </summary>
    internal static class PackImportSelfCheck
    {
        /// <summary>The host's id pattern, SecureDownload.SafeIdPattern (src/dotNet/SecureDownload.cs), which a module
        /// cannot call; the device names are checked separately through PackImport.IsDeviceName's own list.</summary>
        private static readonly Regex HostIdPattern =
            new Regex(@"\A[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9])?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        internal static void SelfCheckPackImport(SelfTestProbe probe)
        {
            CheckOptions(probe);
            CheckGuard(probe);
            CheckIds(probe);
            CheckWithSyntheticPack(probe);
        }

        // ---- options: main required, roles defaulted, source-hidden kept, startup only when chosen ----

        private static void CheckOptions(SelfTestProbe probe)
        {
            string[] keys = { "Egg (2)", "Alpha", ".Helper", "unused" };
            List<PackMemberChoice> rows = PackImport.DefaultChoices(keys, "Alpha");
            Func<List<PackMemberChoice>, string, PackMemberChoice> row = delegate (List<PackMemberChoice> list, string key)
            {
                return list.FirstOrDefault(r => r.Key == key);
            };
            probe.Check("WITNESS the pack dialog lists every member in the converter's order: " + string.Join(", ", rows.Select(r => r.Key)),
                rows.Select(r => r.Key).SequenceEqual(new[] { ".Helper", "Alpha", "Egg (2)", "unused" }));
            probe.Check("pack options: the highlighted skin is the main member, and visible",
                row(rows, "Alpha") != null && row(rows, "Alpha").Role == "main" && !row(rows, "Alpha").Hidden);
            probe.Check("pack options: every other member defaults to a visible form, nothing guessed from its name",
                row(rows, "Egg (2)") != null && row(rows, "Egg (2)").Role == "form" && !row(rows, "Egg (2)").Hidden);
            probe.Check("pack options: a member the source chooser hides keeps the source's default, a hidden helper",
                row(rows, ".Helper") != null && row(rows, ".Helper").Role == "helper" && row(rows, ".Helper").Hidden
                && row(rows, "unused") != null && row(rows, "unused").Role == "helper" && row(rows, "unused").Hidden);
            probe.Check("WITNESS changing a role resets Hidden to the converter's default: an egg hides, a form shows",
                PackImport.DefaultHidden("Egg (2)", "egg") && !PackImport.DefaultHidden("Egg (2)", "form")
                && PackImport.DefaultHidden(".Helper", "form") && !PackImport.DefaultHidden(".Main", "main"));

            string refusal;
            PackConversionOptions options = PackImport.SelectPackOptions(rows, "Alpha", null, null, out refusal);
            probe.Check("pack options: every member is sent with an explicit role and visibility (" + (refusal ?? "built") + ")",
                options != null && options.MainMember == "Alpha" && options.Members.Count == 4
                && options.Members.Values.All(m => m.Role != null && m.Hidden.HasValue)
                && options.Members["Alpha"].Role == "main" && options.Members["Alpha"].Hidden == false);
            PackConversionOptions behaviourOnly = PackImport.SelectPackOptions(rows, "Alpha", null, "Hatch", out refusal);
            probe.Check("pack options: no startup is written when none was chosen, even with a behaviour typed",
                options != null && options.Startup == null && behaviourOnly != null && behaviourOnly.Startup == null);
            PackConversionOptions started = PackImport.SelectPackOptions(rows, "Alpha", "Egg (2)", "  Hatch ", out refusal);
            probe.Check("pack options: the startup is written when the author chose one, behaviour trimmed (" + (refusal ?? "built") + ")",
                started != null && started.Startup != null && started.Startup.Member == "Egg (2)" && started.Startup.Behaviour == "Hatch");
            PackConversionOptions noBehaviour = PackImport.SelectPackOptions(rows, "Alpha", "Egg (2)", " ", out refusal);
            probe.Check("WITNESS a starting member with no behaviour is refused by name: " + refusal,
                noBehaviour == null && refusal != null && refusal.IndexOf("'Egg (2)'", StringComparison.Ordinal) >= 0);

            string noMain, unknownMain;
            PackConversionOptions none = PackImport.SelectPackOptions(rows, "", null, null, out noMain);
            PackConversionOptions unknown = PackImport.SelectPackOptions(rows, "Gamma", null, null, out unknownMain);
            probe.Check("pack options: a main member is required, and must be one of the pack's image sets: " + noMain + " / " + unknownMain,
                none == null && unknown == null && !string.IsNullOrEmpty(noMain) && !string.IsNullOrEmpty(unknownMain));

            List<PackMemberChoice> hiddenMain = PackImport.DefaultChoices(keys, "Alpha");
            row(hiddenMain, "Alpha").Hidden = true;
            string hiddenRefusal;
            PackConversionOptions refused = PackImport.SelectPackOptions(hiddenMain, "Alpha", null, null, out hiddenRefusal);
            probe.Check("pack options: a main member marked hidden is refused before the converter is called: " + hiddenRefusal,
                refused == null && hiddenRefusal != null && hiddenRefusal.IndexOf("cannot be hidden", StringComparison.Ordinal) >= 0);

            List<PackMemberChoice> dotted = PackImport.DefaultChoices(new[] { ".Alpha", "Beta" }, ".Alpha");
            PackConversionOptions dottedOptions = PackImport.SelectPackOptions(dotted, ".Alpha", null, null, out refusal);
            probe.Check("pack options: a main member the source chooser would hide is sent explicitly visible (review M2)",
                dottedOptions != null && dottedOptions.Members[".Alpha"].Hidden == false && dottedOptions.Members[".Alpha"].Role == "main");

            List<PackMemberChoice> twoMains = PackImport.DefaultChoices(keys, "Alpha");
            row(twoMains, "Egg (2)").Role = "main";
            probe.Check("WITNESS a second member given the main role is refused",
                PackImport.SelectPackOptions(twoMains, "Alpha", null, null, out refusal) == null && refusal != null);
        }

        // ---- the guard around the converter: a hidden main and an exception are refusals ----

        private static void CheckGuard(SelfTestProbe probe)
        {
            int calls = 0;
            Func<string, PackConversionOptions, PackConversionResult> counting = delegate (string root, PackConversionOptions o)
            {
                calls++;
                return null;
            };
            Func<bool?, PackConversionOptions> withMain = delegate (bool? hidden)
            {
                return new PackConversionOptions
                {
                    MainMember = "Alpha",
                    Members = new Dictionary<string, PackMemberOptions>(StringComparer.Ordinal)
                    {
                        { "Alpha", new PackMemberOptions { Role = "main", Hidden = hidden } },
                    },
                };
            };

            string refusal;
            PackConversionResult result = PackImport.ConvertGuarded("R", withMain(true), counting, out refusal);
            probe.Check("the converter guard refuses a hidden main member without calling the converter (calls " + calls + ")",
                result == null && calls == 0 && refusal != null && refusal.IndexOf("cannot be hidden", StringComparison.Ordinal) >= 0);
            calls = 0;
            result = PackImport.ConvertGuarded("R", withMain(null), counting, out refusal);
            probe.Check("the converter guard refuses a main member whose visibility is left to the converter to guess (calls " + calls + ")",
                result == null && calls == 0 && refusal != null);
            calls = 0;
            result = PackImport.ConvertGuarded("R", withMain(false), counting, out refusal);
            probe.Check("WITNESS a visible main member reaches the converter exactly once (calls " + calls + ")",
                calls == 1 && refusal != null && refusal.IndexOf("no result", StringComparison.Ordinal) >= 0);

            // Review M1: ConvertPack threw InvalidDataException for a root with no img folder. Driven with a converter
            // that throws, so the check holds whether or not the converter lane's fix has landed.
            string thrownRefusal = null, escaped = null;
            try
            {
                result = PackImport.ConvertGuarded("R", withMain(false),
                    delegate (string root, PackConversionOptions o) { throw new InvalidDataException("Tick pack needs an img directory."); },
                    out thrownRefusal);
            }
            catch (Exception ex) { escaped = ex.GetType().Name; result = null; }
            probe.Check("a converter that throws is reported as a refusal, not an exception: " + (escaped ?? thrownRefusal),
                escaped == null && result == null && thrownRefusal != null
                && thrownRefusal.IndexOf("Tick pack needs an img directory.", StringComparison.Ordinal) >= 0);
        }

        // ---- installed ids: safe for every key the host accepts, unique ----

        private static void CheckIds(SelfTestProbe probe)
        {
            string longKey = new string('L', 255);
            string longSibling = new string('L', 254) + "M";
            string[] keys = { "A+B", "AB", "Egg (2)", longKey, longSibling, "日本", "+++" };
            Dictionary<string, string> ids = PackImport.PackInstallIds("My Pack", keys, new[] { "MY-PACK-EGG-2" });
            List<string> all = keys.Select(k => ids.ContainsKey(k) ? ids[k] : "").ToList();
            probe.Check("every installed id meets the host's id rule (1-64 characters, letters, digits, '.', '_', '-', alphanumeric ends, no device name): "
                        + string.Join(" ", all),
                all.All(id => HostIdPattern.IsMatch(id) && !PackImport.IsDeviceName(id)));
            probe.Check("installed ids are unique within the pack and against the installed companions, ignoring case: " + string.Join(" ", all),
                all.Distinct(StringComparer.OrdinalIgnoreCase).Count() == keys.Length
                && !all.Contains("my-pack-egg-2", StringComparer.OrdinalIgnoreCase));
            probe.Check("WITNESS the ids are the studio's slug of title and key, suffixed only on a clash: "
                        + ids["A+B"] + " " + ids["AB"] + " " + ids["Egg (2)"],
                ids["A+B"] == "my-pack-ab" && ids["AB"] == "my-pack-ab-2" && ids["Egg (2)"] == "my-pack-egg-2-2"
                && ids[longKey].Length == 64 && ids[longSibling].Length <= 64);
            Dictionary<string, string> device = PackImport.PackInstallIds("", new[] { "CON", "lpt1" }, null);
            probe.Check("a key that slugs to a Windows device name is never an id: " + device["CON"] + " " + device["lpt1"],
                device["CON"] == "con-2" && device["lpt1"] == "lpt1-2");
        }

        // ---- the real converter on a synthetic pack, the install gate and a partial failure ----

        private static void CheckWithSyntheticPack(SelfTestProbe probe)
        {
            string scratch = Path.Combine(Path.GetTempPath(), "dp-petstudio-pack-" + Guid.NewGuid().ToString("N"));
            try
            {
                string packRoot = Path.Combine(scratch, "Synthetic Pack");
                WriteMember(packRoot, "Alpha", null);
                WriteMember(packRoot, "Egg (2)", "Alpha");
                WriteMember(packRoot, ".Helper", null);

                var alpha = new DetectedSkin { Name = "Alpha", ImgDir = Path.Combine(packRoot, "img", "Alpha") + Path.DirectorySeparatorChar };
                List<string> keys = PackImport.ListMemberKeys(packRoot);
                probe.Check("WITNESS the pack root and member key come from the skin's own folders: " + PackImport.PackRootOf(alpha) + " | " + string.Join(", ", keys),
                    string.Equals(PackImport.PackRootOf(alpha), packRoot, StringComparison.OrdinalIgnoreCase)
                    && PackImport.MemberKeyOf(alpha) == "Alpha" && keys.SequenceEqual(new[] { ".Helper", "Alpha", "Egg (2)" }));
                probe.Check("WITNESS a skin whose sprites are not under an img folder has no pack root",
                    PackImport.PackRootOf(new DetectedSkin { ImgDir = packRoot }) == null);
                probe.Check("WITNESS the pack title is the pack folder's name, or the author's when the pack is the import root itself",
                    PackImport.PackTitle(packRoot, scratch, "archive") == "Synthetic Pack"
                    && PackImport.PackTitle(packRoot, packRoot, "archive") == "archive");

                List<PackMemberChoice> rows = PackImport.DefaultChoices(keys, "Alpha");
                PackMemberChoice egg = rows.First(r => r.Key == "Egg (2)");
                egg.Role = "egg";
                egg.Hidden = PackImport.DefaultHidden(egg.Key, egg.Role);
                string refusal;
                PackConversionOptions options = PackImport.SelectPackOptions(rows, "Alpha", "Egg (2)", "Hatch", out refusal);
                PackConversionResult accepted = options == null ? null
                    : PackImport.ConvertGuarded(packRoot, options, PackImport.ConvertWithEngine, out refusal);
                bool ok = accepted != null && accepted.Accepted && accepted.Members.Count == 3;
                probe.Check("WITNESS the synthetic three-member pack converts and is accepted: "
                            + (accepted == null ? refusal : accepted.Accepted ? "accepted" : accepted.Error),
                    ok);
                if (!ok) return;
                PackMemberConversionResult eggResult = accepted.Members.First(m => m.SourceKey == "Egg (2)");
                probe.Check("WITNESS each document keeps its exact member key and the chosen role in its pack header",
                    eggResult.Root != null && eggResult.Root.Header.Pack.Member == "Egg (2)" && eggResult.Root.Header.Pack.Role == "egg");

                // The install gate. Options naming an image set the pack lacks make the converter refuse the PACK while
                // every member it has is still prepared: exactly the result that must install nothing.
                var unknownOption = new PackConversionOptions
                {
                    MainMember = "Alpha",
                    Members = new Dictionary<string, PackMemberOptions>(StringComparer.Ordinal)
                    {
                        { "Alpha", new PackMemberOptions { Role = "main", Hidden = false } },
                        { "Gamma", new PackMemberOptions { Role = "form", Hidden = false } },
                    },
                };
                PackConversionResult notAccepted = PackImport.ConvertGuarded(packRoot, unknownOption, PackImport.ConvertWithEngine, out refusal);
                int preparedInRefused = notAccepted == null ? 0
                    : notAccepted.Members.Count(m => m.Disposition == PackMemberDisposition.Prepared && m.EmittedXml != null);
                probe.Check("WITNESS the refused pack still carries prepared members, so only the gate stops them (" + preparedInRefused + " prepared)",
                    notAccepted != null && !notAccepted.Accepted && preparedInRefused > 0);
                var gateHost = new RecordingCompanionManager();
                PackInstallOutcome gated = PackImport.InstallPack(notAccepted, "Synthetic Pack", gateHost);
                probe.Check("the install gate: a pack the converter did not accept installs nothing (installs " + gateHost.Installs.Count + ")",
                    gateHost.Installs.Count == 0 && !gated.Complete && gated.InstalledKeys.Count == 0);
                probe.Check("Install pack is offered only for an accepted pack with a host to install into",
                    !PackImport.CanInstall(notAccepted, true) && PackImport.CanInstall(accepted, true) && !PackImport.CanInstall(accepted, false)
                    && !PackImport.CanInstall(null, true));

                var host = new RecordingCompanionManager();
                host.Installed.Add("synthetic-pack-alpha");
                PackInstallOutcome whole = PackImport.InstallPack(accepted, "Synthetic Pack", host);
                string order = string.Join(", ", host.Installs.Select(i => i.Key));
                probe.Check("an accepted pack installs every member, the main member last: " + order,
                    whole.Complete && host.Installs.Count == 3 && host.Installs[2].Key == "synthetic-pack-alpha-2"
                    && host.Installs.Take(2).All(i => i.Key != "synthetic-pack-alpha-2"));
                probe.Check("each member is installed with the converter's EmittedXml, unchanged",
                    host.Installs.Count == 3 && accepted.Members.All(m => host.Installs.Any(i => ReferenceEquals(i.Value, m.EmittedXml)
                        || string.Equals(i.Value, m.EmittedXml, StringComparison.Ordinal))));
                probe.Check("WITNESS a whole install names the main member's id: " + whole.Message,
                    whole.Message.IndexOf("'synthetic-pack-alpha-2'", StringComparison.Ordinal) >= 0);

                // A partial failure: the egg's install fails. .Helper went first, the main member was never attempted.
                var failing = new RecordingCompanionManager { FailId = "synthetic-pack-egg-2" };
                PackInstallOutcome partial = PackImport.InstallPack(accepted, "Synthetic Pack", failing);
                probe.Check("a member that fails to install stops the install and names the members already installed: " + partial.Message,
                    !partial.Complete && partial.FailedKey == "Egg (2)" && failing.Installs.Count == 1
                    && partial.InstalledKeys.SequenceEqual(new[] { ".Helper" })
                    && partial.Message.IndexOf("Already installed: '.Helper' as 'synthetic-pack-helper'", StringComparison.Ordinal) >= 0
                    && partial.Message.IndexOf("The main member was not installed", StringComparison.Ordinal) >= 0);
                var throwing = new RecordingCompanionManager { FailId = "synthetic-pack-helper", Throw = true };
                PackInstallOutcome thrown = PackImport.InstallPack(accepted, "Synthetic Pack", throwing);
                probe.Check("WITNESS an InstallType that throws is a stop with nothing installed, not an exception: " + thrown.Message,
                    !thrown.Complete && thrown.FailedKey == ".Helper" && thrown.InstalledKeys.Count == 0
                    && thrown.Message.IndexOf("Nothing was installed", StringComparison.Ordinal) >= 0);

                probe.Check("WITNESS the pack report names the verdict and every member: " + PackImport.MemberRow(eggResult, options),
                    PackImport.Summary(accepted, "Synthetic Pack").StartsWith("✓ Pack 'Synthetic Pack': accepted, 3 member(s).", StringComparison.Ordinal)
                    && PackImport.Summary(notAccepted, "Synthetic Pack").StartsWith("✗ Pack 'Synthetic Pack': not accepted", StringComparison.Ordinal)
                    && PackImport.MemberRow(eggResult, options).StartsWith("Egg (2)   (egg, hidden)   ✓ prepared", StringComparison.Ordinal));

                // The real converter on a folder with no img folder: refused whichever way the converter says it (it threw
                // until the converter lane's fix for review M1; either way nothing escapes and nothing is accepted).
                string bare = Path.Combine(scratch, "no-img");
                Directory.CreateDirectory(bare);
                string bareRefusal = null, bareEscaped = null;
                PackConversionResult bareResult = null;
                try { bareResult = PackImport.ConvertGuarded(bare, options, PackImport.ConvertWithEngine, out bareRefusal); }
                catch (Exception ex) { bareEscaped = ex.GetType().Name; }
                probe.Check("WITNESS the real converter on a folder with no img folder ends in a refusal: "
                            + (bareEscaped ?? bareRefusal ?? (bareResult != null ? bareResult.Error : "")),
                    bareEscaped == null && (bareResult == null ? bareRefusal != null : !bareResult.Accepted));
            }
            catch (Exception ex) { probe.Check("the pack import checks ran without an exception: " + ex.GetType().Name + ": " + ex.Message, false); }
            finally
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>One synthetic image set: three generated sprites and a conf the converter's literal lowering accepts.
        /// With <paramref name="transformTo"/>, the member's Hatch behaviour transforms into that member, which is what an
        /// egg must do (the converter refuses an egg with no way back).</summary>
        private static void WriteMember(string packRoot, string key, string transformTo)
        {
            string member = Path.Combine(packRoot, "img", key);
            string conf = Path.Combine(member, "conf");
            Directory.CreateDirectory(conf);
            Func<string, object[], XElement> e = delegate (string name, object[] content) { return new XElement(name, content); };
            Func<string, string, int, string, XElement> pose = delegate (string image, string anchor, int ticks, string right)
            {
                var p = new XElement("Pose", new XAttribute("Image", image), new XAttribute("ImageAnchor", anchor),
                    new XAttribute("Duration", ticks), new XAttribute("Velocity", "0,0"));
                if (right != null) p.Add(new XAttribute("ImageRight", right));
                return p;
            };
            var actions = new XElement("ActionList",
                e("Action", new object[] { new XAttribute("Name", "Composite"), new XAttribute("Type", "Sequence"),
                    new XElement("ActionReference", new XAttribute("Name", "Stand"), new XAttribute("Duration", 7)) }),
                e("Action", new object[] { new XAttribute("Name", "Stand"), new XAttribute("Type", "Stay"),
                    new XElement("Animation", pose("/p.png", "2,3", 2, "/r.png"), pose("/q.png", "-2,4", 3, null)) }));
            if (transformTo != null)
                actions.Add(e("Action", new object[] { new XAttribute("Name", "Change"), new XAttribute("Type", "Embedded"),
                    new XAttribute("Class", "com.group_finity.mascot.action.Transform"), new XAttribute("TransformMascot", transformTo),
                    new XAttribute("TransformBehavior", "Main"), new XAttribute("Duration", 2),
                    new XElement("Animation", pose("/p.png", "2,3", 2, null)) }));
            var behaviours = new XElement("BehaviorList",
                new XElement("Behavior", new XAttribute("Name", "Main"), new XAttribute("Action", "Composite"), new XAttribute("Frequency", 1)),
                new XElement("Behavior", new XAttribute("Name", "Hatch"), new XAttribute("Action", transformTo != null ? "Change" : "Stand"),
                    new XAttribute("Frequency", 0)));
            File.WriteAllText(Path.Combine(conf, "actions.xml"), new XDocument(new XElement("Mascot", actions)).ToString());
            File.WriteAllText(Path.Combine(conf, "behaviors.xml"), new XDocument(new XElement("Mascot", behaviours)).ToString());
            WriteSprite(Path.Combine(member, "p.png"), 4, 6, Color.Magenta);
            WriteSprite(Path.Combine(member, "q.png"), 7, 4, Color.Cyan);
            WriteSprite(Path.Combine(member, "r.png"), 5, 9, Color.Orange);
        }

        private static void WriteSprite(string path, int width, int height, Color color)
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                bitmap.SetPixel(0, 0, color);
                bitmap.SetPixel(width - 1, height - 1, Color.Lime);
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>A module-local ICompanionManager double: records every InstallType, reports a fixed installed list,
        /// and fails (or throws on) one id when told to. ModuleKit.Testing ships only the denying manager.</summary>
        private sealed class RecordingCompanionManager : ICompanionManager
        {
            public readonly List<string> Installed = new List<string>();
            public readonly List<KeyValuePair<string, string>> Installs = new List<KeyValuePair<string, string>>();
            public string FailId;
            public bool Throw;

            public string CompanionsDirectory { get { return ""; } }
            public IReadOnlyList<CompanionTypeInfo> InstalledTypes()
            {
                return Installed.Select(id => new CompanionTypeInfo { TypeId = id, DisplayName = id }).ToList();
            }
            public bool TryReadTypeXml(string typeId, out string animationsXml, out string error) { animationsXml = null; error = "not recorded"; return false; }
            public IReadOnlyList<CompanionCount> OnScreenMix() { return new List<CompanionCount>(); }
            public int MaxCompanions { get { return 8; } }
            public bool IsAtMax { get { return false; } }
            public bool SpawnOne(string typeId) { return false; }
            public bool RemoveOne(string typeId) { return false; }
            public bool ValidateXml(string animationsXml, out string error) { error = null; return true; }
            public ICompanionPreview SpawnPreview(string animationsXml, out string error) { error = "not recorded"; return null; }
            public bool InstallType(string typeId, string animationsXml, out string error)
            {
                error = null;
                if (typeId == FailId)
                {
                    if (Throw) throw new IOException("The disk is full.");
                    error = "Pet file too large.";
                    return false;
                }
                Installs.Add(new KeyValuePair<string, string>(typeId, animationsXml));
                Installed.Add(typeId);
                return true;
            }
            public bool UninstallType(string typeId, out string error) { error = null; return Installed.Remove(typeId); }
        }
    }
}
