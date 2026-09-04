using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion
{
    /// <summary>Dependency-free security regression checks run by CI and release packaging.</summary>
    internal static class SecuritySelfTest
    {
        public static bool Run(TextWriter output)
        {
            output = output ?? TextWriter.Null;
            int failures = 0;

            CheckCrossSessionLock(ref failures, output);

            Check(SecureDownload.IsSafeId("dadjokes"), "safe catalog id", ref failures, output);
            Check(!SecureDownload.IsSafeId("../escape"), "path traversal id rejected", ref failures, output);
            Check(!SecureDownload.IsSafeId("CON"), "Windows device id rejected", ref failures, output);

            Uri pinned;
            string uriError;
            Check(SecureDownload.TryValidatePinnedRawGitHubUrl(
                    "https://raw.githubusercontent.com/bigfnj/desktop-ai-companion/" +
                    "b52c11d184532364019ffc1756f3f0868ec99997/packs/dadjokes.txt",
                    "bigfnj", "desktop-ai-companion", out pinned, out uriError),
                "commit-pinned catalog URL", ref failures, output);
            Check(!SecureDownload.TryValidatePinnedRawGitHubUrl(
                    "https://raw.githubusercontent.com/bigfnj/desktop-ai-companion/master/packs/dadjokes.txt",
                    "bigfnj", "desktop-ai-companion", out pinned, out uriError),
                "mutable catalog URL rejected", ref failures, output);

            CheckExpression("1+2*3", 7, ref failures, output);
            CheckExpression("Convert(101/2,System.Int32)%30", 20, ref failures, output);
            CheckExpression("(screenW-imageW)/2", 40, ref failures, output);
            string expressionError;
            Check(!SafeExpression.IsValid("IIF(1=1,1,0)", out expressionError),
                "DataTable expression functions rejected", ref failures, output);
            Check(!SafeExpression.IsValid("1/0", out expressionError),
                "division by zero rejected", ref failures, output);
            Check(SafeExpression.IsValid("1/(screenW-17)", out expressionError),
                "variable-dependent divisor is syntax-valid independent of screen state",
                ref failures, output);
            Check(SafeExpression.IsValid("1/(screenW-3440)", out expressionError),
                "runtime-zero divisor is not fabricated during validation",
                ref failures, output);
            Check(!SafeExpression.IsValid("missingVariable+1", out expressionError),
                "unknown expression variable rejected", ref failures, output);
            Check(!SafeExpression.IsValid("2147483648", out expressionError),
                "known constant result overflow rejected", ref failures, output);
            Check(Throws<DivideByZeroException>(delegate
                {
                    SafeExpression.Evaluate(
                        "1/(screenW-3440)",
                        delegate(string name)
                        {
                            if (name == "screenW") return 3440.0;
                            return 17.0;
                        });
                }),
                "runtime expression still rejects the actual zero divisor",
                ref failures, output);

            XmlData.RootNode parsed;
            string xmlError;
            string defaultXml = Properties.Resources.animations;
            Check(CompanionXmlValidator.TryParse(defaultXml, out parsed, out xmlError),
                "bundled pet XML validates" + FormatError(xmlError), ref failures, output);
            string canonicalPetPath;
            string petPathError;
            Check(
                !CompanionXmlValidator.TryResolveLocalXmlFile(
                    @"\\attacker.invalid\share\pet.xml",
                    out canonicalPetPath,
                    out petPathError) &&
                !CompanionXmlValidator.TryResolveLocalXmlFile(
                    @"\\?\UNC\attacker.invalid\share\pet.xml",
                    out canonicalPetPath,
                    out petPathError),
                "UNC and device pet XML paths rejected before probing",
                ref failures,
                output);

            string variableDivisorXml = new Regex(
                @"<x>\s*screenW\+10\s*</x>").Replace(
                    defaultXml, "<x>1/(screenW-17)</x>", 1);
            Check(CompanionXmlValidator.TryParse(
                    variableDivisorXml, out parsed, out xmlError),
                "pet XML admits variable-dependent divisors" + FormatError(xmlError),
                ref failures, output);

            string constantZeroDivisorXml = new Regex(
                @"<x>\s*screenW\+10\s*</x>").Replace(
                    defaultXml, "<x>1/0</x>", 1);
            Check(!CompanionXmlValidator.TryParse(
                    constantZeroDivisorXml, out parsed, out xmlError),
                "pet XML rejects known zero divisors",
                ref failures, output);

            string dtd = "<?xml version=\"1.0\"?><!DOCTYPE animations [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]>" +
                         "<animations xmlns=\"https://esheep.petrucci.ch/\"><header><author>&xxe;</author></header></animations>";
            Check(!CompanionXmlValidator.TryParse(dtd, out parsed, out xmlError),
                "DTD input rejected", ref failures, output);

            string zeroTiles = new Regex(@"<tilesx>\s*\d+\s*</tilesx>").Replace(
                defaultXml, "<tilesx>0</tilesx>", 1);
            Check(!CompanionXmlValidator.TryParse(zeroTiles, out parsed, out xmlError),
                "zero tile count rejected", ref failures, output);

            CheckRetainedLocalXmlAdmission(ref failures, output);
            CheckIconDirectoryPreflight(defaultXml, ref failures, output);
            CheckPetXmlResourceLimits(defaultXml, ref failures, output);
            CheckAnimationReachability(defaultXml, ref failures, output);
            CheckAudioValidation(defaultXml, ref failures, output);
            CheckAboutLinkPolicy(ref failures, output);
            CheckSharedSpriteFrameOwnership(ref failures, output);
            // Smart-fortune lifecycle tests moved to the Fortunes module with the engine (S3d);
            // exercised there via --fortunes-engine-selftest. The idle-schedule test stays (AI-brain).
            CheckIdleScheduleGeneration(ref failures, output);
            CheckSecureDownloadDeadline(ref failures, output);
            CheckRestartLifecycle(ref failures, output);

            output.WriteLine(failures == 0
                ? "Security self-test: PASS"
                : "Security self-test: FAIL (" + failures + " checks)");
            return failures == 0;
        }

        private static void CheckRetainedLocalXmlAdmission(
            ref int failures,
            TextWriter output)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-retained-xml-" +
                    Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "pet");
            string movedDirectory = Path.Combine(root, "pet-moved");
            string path = Path.Combine(directory, "animations.xml");
            const string expected = "<animations />";
            bool writeBlocked = false;
            bool directorySwapBlocked = false;
            CompanionXmlValidator.RetainedLocalXmlFile retained = null;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    path,
                    expected,
                    new UTF8Encoding(false, true));
                CompanionXmlValidator.LocalXmlHandleOpenedForDiagnostics =
                    delegate(string retainedPath)
                    {
                        try
                        {
                            File.WriteAllText(
                                retainedPath,
                                "<attacker />",
                                new UTF8Encoding(false, true));
                        }
                        catch (IOException)
                        {
                            writeBlocked = true;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            writeBlocked = true;
                        }

                        try
                        {
                            Directory.Move(directory, movedDirectory);
                        }
                        catch (IOException)
                        {
                            directorySwapBlocked = true;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            directorySwapBlocked = true;
                        }
                    };

                string error;
                bool opened = CompanionXmlValidator.TryOpenLocalXmlFile(
                    path,
                    out retained,
                    out error);
                string observed = null;
                if (opened)
                {
                    using (retained)
                    using (var stream = retained.OpenRead(4096))
                    using (var reader = new StreamReader(
                        stream,
                        new UTF8Encoding(false, true),
                        true,
                        1024))
                        observed = reader.ReadToEnd();
                    retained = null;
                }
                Check(
                    opened &&
                    writeBlocked &&
                    directorySwapBlocked &&
                    observed == expected,
                    "local pet admission retains the validated file and directory chain" +
                        FormatError(error),
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "retained local pet admission threw " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                CompanionXmlValidator.LocalXmlHandleOpenedForDiagnostics = null;
                if (retained != null) retained.Dispose();
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch { }
            }
        }

        private static void CheckIconDirectoryPreflight(
            string defaultXml,
            ref int failures,
            TextWriter output)
        {
            byte[] hugePngHeader = BuildPngHeader(8192, 8192);
            byte[] onePixelPngHeader = BuildPngHeader(1, 1);

            CheckRejectedIcon(
                defaultXml,
                BuildIcon(
                    new[] { 1 },
                    new[] { 1 },
                    new[] { hugePngHeader },
                    null),
                "ICO embedded dimensions cannot exceed their directory declaration",
                ref failures,
                output);

            byte[] excessiveEntries = new byte[22];
            excessiveEntries[2] = 1;
            WriteLittleEndianUInt16(
                excessiveEntries,
                4,
                CompanionXmlValidator.MaximumIconEntries + 1);
            CheckRejectedIcon(
                defaultXml,
                excessiveEntries,
                "ICO entry count is bounded before decoding",
                ref failures,
                output);

            int sharedOffset = 6 + 2 * 16;
            CheckRejectedIcon(
                defaultXml,
                BuildIcon(
                    new[] { 1, 1 },
                    new[] { 1, 1 },
                    new[] { onePixelPngHeader, onePixelPngHeader },
                    new[] { sharedOffset, sharedOffset }),
                "overlapping ICO payloads are rejected before decoding",
                ref failures,
                output);

            CheckRejectedIcon(
                defaultXml,
                BuildIcon(
                    new[] { 1 },
                    new[] { 1 },
                    new[] { onePixelPngHeader },
                    new[] { 4096 }),
                "out-of-range ICO payloads are rejected before decoding",
                ref failures,
                output);
        }

        private static void CheckRejectedIcon(
            string defaultXml,
            byte[] icon,
            string name,
            ref int failures,
            TextWriter output)
        {
            string replacement =
                "<icon><![CDATA[" +
                Convert.ToBase64String(icon) +
                "]]></icon>";
            string xml = new Regex(
                @"<icon\b[^>]*>.*?</icon>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase).Replace(
                    defaultXml,
                    replacement,
                    1);
            XmlData.RootNode parsed;
            string error;
            Check(
                !CompanionXmlValidator.TryParse(
                    xml,
                    out parsed,
                    out error) &&
                !string.IsNullOrWhiteSpace(error) &&
                error.IndexOf(
                    "ICO",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                name + FormatError(error),
                ref failures,
                output);
        }

        private static byte[] BuildIcon(
            int[] widths,
            int[] heights,
            byte[][] payloads,
            int[] forcedOffsets)
        {
            int count = payloads.Length;
            int directoryBytes = checked(6 + count * 16);
            int payloadBytes = 0;
            for (int index = 0; index < count; index++)
                payloadBytes = checked(payloadBytes + payloads[index].Length);
            var icon = new byte[checked(directoryBytes + payloadBytes)];
            icon[2] = 1;
            WriteLittleEndianUInt16(icon, 4, count);
            int nextOffset = directoryBytes;
            for (int index = 0; index < count; index++)
            {
                int entry = 6 + index * 16;
                icon[entry] = widths[index] == 256
                    ? (byte)0
                    : (byte)widths[index];
                icon[entry + 1] = heights[index] == 256
                    ? (byte)0
                    : (byte)heights[index];
                icon[entry + 4] = 1;
                icon[entry + 6] = 32;
                WriteLittleEndianUInt32(
                    icon,
                    entry + 8,
                    (uint)payloads[index].Length);
                int declaredOffset =
                    forcedOffsets == null
                        ? nextOffset
                        : forcedOffsets[index];
                WriteLittleEndianUInt32(
                    icon,
                    entry + 12,
                    (uint)declaredOffset);
                Buffer.BlockCopy(
                    payloads[index],
                    0,
                    icon,
                    nextOffset,
                    payloads[index].Length);
                nextOffset += payloads[index].Length;
            }
            return icon;
        }

        private static byte[] BuildPngHeader(int width, int height)
        {
            var header = new byte[24];
            byte[] signature =
            {
                0x89, 0x50, 0x4e, 0x47,
                0x0d, 0x0a, 0x1a, 0x0a
            };
            Buffer.BlockCopy(signature, 0, header, 0, signature.Length);
            header[11] = 13;
            header[12] = 0x49;
            header[13] = 0x48;
            header[14] = 0x44;
            header[15] = 0x52;
            WriteBigEndianInt32(header, 16, width);
            WriteBigEndianInt32(header, 20, height);
            return header;
        }

        private static void WriteLittleEndianUInt16(
            byte[] bytes,
            int offset,
            int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteLittleEndianUInt32(
            byte[] bytes,
            int offset,
            uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteBigEndianInt32(
            byte[] bytes,
            int offset,
            int value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static void CheckAboutLinkPolicy(
            ref int failures,
            TextWriter output)
        {
            string normalized;
            bool allowed = WebLinks.TryNormalizeHttpsLink(
                "https://example.com/pet",
                out normalized);
            bool httpRejected = !WebLinks.TryNormalizeHttpsLink(
                "http://example.com/pet",
                out normalized);
            bool userInfoRejected = !WebLinks.TryNormalizeHttpsLink(
                "https://user:secret@example.com/pet",
                out normalized);
            bool nonWebRejected = !WebLinks.TryNormalizeHttpsLink(
                "file:///C:/Windows/win.ini",
                out normalized);
            Check(
                allowed &&
                httpRejected &&
                userInfoRejected &&
                nonWebRejected,
                "pet-supplied About links allow only HTTPS without userinfo",
                ref failures,
                output);
        }

        private static void CheckPetXmlResourceLimits(
            string defaultXml,
            ref int failures,
            TextWriter output)
        {
            Check(
                CompanionXmlValidator.MaximumSpriteTiles ==
                SpriteFrameStore.MaximumFrames,
                "pet validator and runtime share the sprite-tile limit",
                ref failures,
                output);

            XmlData.RootNode parsed;
            string error;
            string excessiveTiles = new Regex(
                @"<tilesx>\s*\d+\s*</tilesx>").Replace(
                    defaultXml,
                    "<tilesx>256</tilesx>",
                    1);
            excessiveTiles = new Regex(
                @"<tilesy>\s*\d+\s*</tilesy>").Replace(
                    excessiveTiles,
                    "<tilesy>5</tilesy>",
                    1);
            Check(
                !CompanionXmlValidator.TryParse(
                    excessiveTiles,
                    out parsed,
                    out error) &&
                error != null &&
                error.IndexOf(
                    "too many tiles",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                "pet XML rejects sprite sheets over the runtime's 1,024-tile limit",
                ref failures,
                output);

            string maximumTransitions = BuildTransitionLimitXml(
                defaultXml,
                CompanionXmlValidator.MaximumTransitions);
            Check(
                CompanionXmlValidator.TryParse(
                    maximumTransitions,
                    out parsed,
                    out error),
                "pet XML accepts exactly 256 transitions" + FormatError(error),
                ref failures,
                output);

            string excessiveTransitions = BuildTransitionLimitXml(
                defaultXml,
                CompanionXmlValidator.MaximumTransitions + 1);
            Check(
                !CompanionXmlValidator.TryParse(
                    excessiveTransitions,
                    out parsed,
                    out error) &&
                error != null &&
                error.IndexOf(
                    "too many transitions",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                "pet XML rejects transition sets over 256 entries",
                ref failures,
                output);
        }

        /// <summary>
        /// Animation reachability: which animations can never play. Salvaged from the PetTester tool, whose
        /// copy of this walk was fused to a WinForms form and so was never run by CI. The two rules asserted
        /// here are the ones a naive graph walk gets wrong, and both silently HIDE a dead animation from the
        /// pet's author rather than failing loudly.
        /// </summary>
        private static void CheckAnimationReachability(
            string defaultXml,
            ref int failures,
            TextWriter output)
        {
            XmlData.RootNode parsed;
            string error;

            // The shipped pet must have no dead animations: this is also the baseline that proves the walk
            // is not simply declaring everything unreachable.
            if (CompanionXmlValidator.TryParse(defaultXml, out parsed, out error))
            {
                using (var xml = new Xml(1))
                using (var animations = new Animations(xml))
                {
                    xml.TryReadXml(defaultXml, out error);
                    xml.LoadAnimations(animations);
                    Check(
                        AnimationReachability.FindUnreachable(parsed, animations).Count == 0,
                        "every animation in the bundled pet is reachable",
                        ref failures,
                        output);
                }
            }
            else
            {
                Check(false, "bundled pet XML parses for the reachability walk" + FormatError(error), ref failures, output);
            }

            // Rule 1: a child edge is parent-gated. Animation 1000 is an orphan, and 1001 is reachable ONLY
            // through 1000's child link, so BOTH must be reported -- a walk that seeds child targets as roots
            // would call 1001 reachable and hide half a broken pet.
            string childFixture = BuildUnreachablePairXml(defaultXml, 1000, 1001, true);
            if (CompanionXmlValidator.TryParse(childFixture, out parsed, out error))
            {
                using (var xml = new Xml(1))
                using (var animations = new Animations(xml))
                {
                    xml.TryReadXml(childFixture, out error);
                    xml.LoadAnimations(animations);
                    List<int> dead = AnimationReachability.FindUnreachable(parsed, animations);
                    Check(
                        dead.Contains(1000) && dead.Contains(1001),
                        "a child target stays unreachable until its parent is reachable",
                        ref failures,
                        output);
                }
            }
            else
            {
                Check(false, "the parent-gated child fixture parses" + FormatError(error), ref failures, output);
            }

            // Rule 2: a probability-0 transition is written but can never be taken, so it is not an edge.
            string zeroFixture = BuildUnreachablePairXml(defaultXml, 1002, 0, false);
            if (CompanionXmlValidator.TryParse(zeroFixture, out parsed, out error))
            {
                using (var xml = new Xml(1))
                using (var animations = new Animations(xml))
                {
                    xml.TryReadXml(zeroFixture, out error);
                    xml.LoadAnimations(animations);
                    Check(
                        AnimationReachability.FindUnreachable(parsed, animations).Contains(1002),
                        "a zero-probability transition does not make its target reachable",
                        ref failures,
                        output);
                }
            }
            else
            {
                Check(false, "the zero-probability fixture parses" + FormatError(error), ref failures, output);
            }
        }

        /// <summary>
        /// Clone the first animation under new ids to build a reachability fixture. Every cloned transition is
        /// self-looped so the clone introduces no edges of its own; the only way in is the edge this method
        /// then adds -- a parent-gated child link (childLink) or a zero-probability transition from the
        /// already-reachable animation 1.
        /// </summary>
        private static string BuildUnreachablePairXml(string source, int firstId, int secondId, bool childLink)
        {
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(source);
            var ns = new XmlNamespaceManager(document.NameTable);
            ns.AddNamespace("p", document.DocumentElement.NamespaceURI);

            XmlNode animations = document.SelectSingleNode("/p:animations/p:animations", ns);
            var template = (XmlElement)document.SelectSingleNode("/p:animations/p:animations/p:animation[@id='1']", ns);

            AppendClone(document, ns, animations, template, firstId);
            if (childLink) AppendClone(document, ns, animations, template, secondId);

            if (childLink)
            {
                XmlNode childs = document.SelectSingleNode("/p:animations/p:childs", ns);
                var child = (XmlElement)document.SelectSingleNode("/p:animations/p:childs/p:child[1]", ns).CloneNode(true);
                child.SetAttribute("animationid", firstId.ToString(CultureInfo.InvariantCulture));
                XmlNode next = child.SelectSingleNode("p:next", ns);
                if (next != null) next.InnerText = secondId.ToString(CultureInfo.InvariantCulture);
                childs.AppendChild(child);
            }
            else
            {
                // A zero-probability edge from the reachable animation 1 to the clone.
                XmlNode sequence = document.SelectSingleNode("/p:animations/p:animations/p:animation[@id='1']/p:sequence", ns);
                XmlElement zero = document.CreateElement("next", document.DocumentElement.NamespaceURI);
                zero.SetAttribute("probability", "0");
                zero.InnerText = firstId.ToString(CultureInfo.InvariantCulture);
                sequence.AppendChild(zero);
            }

            return document.OuterXml;
        }

        private static void AppendClone(
            XmlDocument document,
            XmlNamespaceManager ns,
            XmlNode animations,
            XmlElement template,
            int id)
        {
            var clone = (XmlElement)template.CloneNode(true);
            clone.SetAttribute("id", id.ToString(CultureInfo.InvariantCulture));
            // Self-loop every transition so the clone cannot reach (or be reached by) anything else.
            foreach (XmlNode next in clone.SelectNodes(".//p:next", ns))
                next.InnerText = id.ToString(CultureInfo.InvariantCulture);
            animations.AppendChild(clone);
        }

        private static string BuildTransitionLimitXml(string source, int count)
        {
            var sequence = new StringBuilder(
                "<sequence repeat=\"1\" repeatfrom=\"0\"><frame>0</frame>");
            for (int index = 0; index < count; index++)
                sequence.Append("<next probability=\"1\">1</next>");
            sequence.Append("</sequence>");
            return new Regex(
                @"<sequence\b[^>]*>.*?</sequence>",
                RegexOptions.Singleline).Replace(
                    source,
                    sequence.ToString(),
                    1);
        }

        private static void CheckAudioValidation(
            string defaultXml,
            ref int failures,
            TextWriter output)
        {
            byte[] invalidAudio = Encoding.ASCII.GetBytes("not-an-mp3");
            string audioError;
            Check(
                !Mp3Format.LooksLikeMp3(invalidAudio, out audioError) &&
                !string.IsNullOrWhiteSpace(audioError),
                "invalid MP3 data fails the base header sanity check",
                ref failures,
                output);

            int rootClose = defaultXml.LastIndexOf(
                "</animations>",
                StringComparison.OrdinalIgnoreCase);
            string invalidAudioXml =
                defaultXml.Insert(
                    rootClose,
                    "<sounds><sound animationid=\"1\">" +
                    "<probability>100</probability><loop>0</loop>" +
                    "<base64>" + Convert.ToBase64String(invalidAudio) +
                    "</base64></sound></sounds>");
            XmlData.RootNode parsed;
            string xmlError;
            Check(
                !CompanionXmlValidator.TryParse(
                    invalidAudioXml,
                    out parsed,
                    out xmlError) &&
                xmlError != null &&
                xmlError.IndexOf(
                    "usable MP3",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                "pet XML rejects audio that cannot be decoded as MP3",
                ref failures,
                output);

            using (var xml = new Xml())
            using (var animations = new Animations(xml))
            {
                animations.AddSound(
                    1,
                    100,
                    0,
                    Convert.ToBase64String(invalidAudio));
                Check(
                    !animations.SheepSound.ContainsKey(1),
                    "failed sounds are not inserted into the animation dictionary",
                    ref failures,
                    output);

                // A structurally-valid MP3 (MPEG frame sync) is accepted and its raw bytes are carried for
                // the Sound module; the base itself does not decode or open an audio device.
                byte[] validAudio = Convert.FromBase64String(
                    "/+MYxAAAAANIAAAAAExBTUUzLjEwMFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                    "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV/+MYxDsAAANIAAAAAFVVVVVVVVVVVVVV" +
                    "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                    "/+MYxHYAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                    "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV/+MYxLEAAANIAAAAAFVVVVVVVVVVVVVV" +
                    "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV");
                animations.AddSound(2, 100, 3, Convert.ToBase64String(validAudio));
                List<TSound> variants;
                Check(
                    animations.SheepSound.TryGetValue(2, out variants) &&
                    variants.Count == 1 &&
                    variants[0].Data != null &&
                    variants[0].Data.Length == validAudio.Length,
                    "valid MP3 is accepted and its raw bytes are carried (no decode in the base)",
                    ref failures,
                    output);
            }
        }

        private static void CheckSharedSpriteFrameOwnership(
            ref int failures,
            TextWriter output)
        {
            SynchronizationContext previousSynchronizationContext =
                SynchronizationContext.Current;
            Bitmap unownedFrame = null;
            Xml xml = null;
            Animations animations = null;
            FormCompanion firstRoot = null;
            FormCompanion secondRoot = null;
            FormCompanion child = null;
            FormCompanion failedRoot = null;
            try
            {
                unownedFrame = new Bitmap(3, 1);
                unownedFrame.SetPixel(0, 0, Color.Red);
                unownedFrame.SetPixel(1, 0, Color.Green);
                unownedFrame.SetPixel(2, 0, Color.Blue);

                xml = new Xml(
                    new List<Bitmap> { unownedFrame },
                    unownedFrame.Width,
                    unownedFrame.Height);
                unownedFrame = null;
                animations = new Animations(xml);
                firstRoot = new FormCompanion(animations, xml);
                secondRoot = new FormCompanion(animations, xml);

                Bitmap firstOriginal =
                    (Bitmap)firstRoot.SpriteFrameForDiagnostics(0);
                Bitmap secondOriginal =
                    (Bitmap)secondRoot.SpriteFrameForDiagnostics(0);
                Check(
                    ReferenceEquals(firstOriginal, secondOriginal),
                    "root pets share the original sprite frame",
                    ref failures,
                    output);

                firstRoot.FlipOrientationForDiagnostics();
                Bitmap firstFlipped =
                    (Bitmap)firstRoot.SpriteFrameForDiagnostics(0);
                Bitmap cachedFlipped =
                    (Bitmap)firstRoot.SpriteFrameForDiagnostics(0);
                Check(
                    firstOriginal.GetPixel(0, 0).ToArgb() ==
                        Color.Red.ToArgb() &&
                    firstOriginal.GetPixel(2, 0).ToArgb() ==
                        Color.Blue.ToArgb(),
                    "flipping a pet leaves the original frame unchanged",
                    ref failures,
                    output);
                Check(
                    firstFlipped.GetPixel(0, 0).ToArgb() ==
                        Color.Blue.ToArgb() &&
                    firstFlipped.GetPixel(2, 0).ToArgb() ==
                        Color.Red.ToArgb(),
                    "shared flipped sprite frame mirrors asymmetric pixels",
                    ref failures,
                    output);
                Check(
                    ReferenceEquals(firstFlipped, cachedFlipped),
                    "flipped sprite frame is cached by identity",
                    ref failures,
                    output);
                Check(
                    xml.MaterializedFlippedFrameCount == 1 &&
                    xml.MaterializedFlippedFrameCount <= xml.SpriteCount,
                    "lazy flipped-frame ownership remains bounded",
                    ref failures,
                    output);

                child = firstRoot.CreateUnshownChildForDiagnostics();
                Check(
                    child.IsMovingLeftForDiagnostics ==
                        firstRoot.IsMovingLeftForDiagnostics &&
                    ReferenceEquals(
                        child.SpriteFrameForDiagnostics(0),
                        firstFlipped),
                    "child inherits orientation and shared sprite identity",
                    ref failures,
                    output);

                bool initializationFailed = false;
                try
                {
                    StartUp.CreateAndInitializeOwnedPet(
                        delegate
                        {
                            failedRoot = new FormCompanion(animations, xml);
                            return failedRoot;
                        },
                        delegate(FormCompanion ignored)
                        {
                            throw new InvalidOperationException(
                                "diagnostic initialization failure");
                        });
                }
                catch (InvalidOperationException)
                {
                    initializationFailed = true;
                }
                Check(
                    initializationFailed &&
                    failedRoot != null &&
                    failedRoot.IsDisposed,
                    "failed root-form initialization disposes the unowned form",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "shared sprite-frame ownership regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                if (child != null) child.Dispose();
                if (secondRoot != null) secondRoot.Dispose();
                if (firstRoot != null) firstRoot.Dispose();
                if (failedRoot != null) failedRoot.Dispose();
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
                if (unownedFrame != null) unownedFrame.Dispose();
                SynchronizationContext.SetSynchronizationContext(
                    previousSynchronizationContext);
            }
        }

        private static void CheckCrossSessionLock(
            ref int failures,
            TextWriter output)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-cross-session-lock-" + Guid.NewGuid().ToString("N"));
            IDisposable held = null;
            try
            {
                Directory.CreateDirectory(directory);
                string dataPath = Path.Combine(directory, "settings.json");
                string name = CrossSessionLock.BuildGlobalMutexName(
                    "SecuritySelfTest",
                    dataPath);
                held = CrossSessionLock.TryAcquire(name, dataPath, 1000);
                bool blockedWhileHeld = !Task.Run(delegate
                {
                    using (IDisposable contender =
                        CrossSessionLock.TryAcquire(name, dataPath, 100))
                        return contender != null;
                }).GetAwaiter().GetResult();

                if (held != null)
                {
                    held.Dispose();
                    held = null;
                }
                bool acquiredAfterRelease = Task.Run(delegate
                {
                    using (IDisposable contender =
                        CrossSessionLock.TryAcquire(name, dataPath, 1000))
                        return contender != null;
                }).GetAwaiter().GetResult();
                Check(
                    name.StartsWith(@"Global\", StringComparison.Ordinal) &&
                    blockedWhileHeld &&
                    acquiredAfterRelease,
                    "cross-session lock is global, exclusive, and reusable",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "cross-session lock self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                if (held != null) held.Dispose();
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    Check(
                        false,
                        "cross-session lock self-test cleanup",
                        ref failures,
                        output);
                }
            }
        }

        private static void CheckIdleScheduleGeneration(
            ref int failures,
            TextWriter output)
        {
            var schedule = new GenerationAwareIdleSchedule();
            var tickEntered = new ManualResetEventSlim(false);
            var tickRelease = new ManualResetEventSlim(false);
            int staleRearmed = 0;
            bool tickCompleted = false;
            try
            {
                schedule.Reconfigure(1, true);
                bool initiallyArmed = schedule.TryArm(1);
                Task tick = Task.Run(delegate
                {
                    if (!schedule.TryBeginTick(1)) return;
                    tickEntered.Set();
                    tickRelease.Wait();
                    if (schedule.TryArm(1))
                        Interlocked.Exchange(ref staleRearmed, 1);
                });
                bool entered = tickEntered.Wait(TimeSpan.FromSeconds(2));

                // Simulate disabling the brain or invalidating its endpoint policy while the
                // asynchronous tick is between admission and its finally/rearm path.
                schedule.Reconfigure(2, false);
                tickRelease.Set();
                tickCompleted = tick.Wait(TimeSpan.FromSeconds(2));

                schedule.Reconfigure(3, true);
                bool reenabledOnce = schedule.TryArm(3);
                bool duplicateRejected = !schedule.TryArm(3);
                bool retiredRejected = !schedule.TryArm(1);
                Check(
                    initiallyArmed &&
                    entered &&
                    tickCompleted &&
                    Volatile.Read(ref staleRearmed) == 0 &&
                    reenabledOnce &&
                    duplicateRejected &&
                    retiredRejected &&
                    schedule.IsArmedForDiagnostics,
                    "idle scheduling rejects stale rearm and arms one current generation",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "idle schedule generation regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                tickRelease.Set();
                tickRelease.Dispose();
                tickEntered.Dispose();
            }
        }

        private static void CheckSecureDownloadDeadline(
            ref int failures,
            TextWriter output)
        {
            bool headersTimedOut = false;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                using (var handler = new BlockingHeadersHandler())
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/headers"))
                {
                    SecureDownload.DownloadBytesAsync(
                        handler,
                        request,
                        1024,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                headersTimedOut = true;
            }
            stopwatch.Stop();
            Check(
                headersTimedOut && stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "catalog deadline bounds response headers",
                ref failures,
                output);

            bool ignoredHeadersTimedOut = false;
            var ignoredHeadersHandler =
                new CancellationIgnoringHeadersHandler();
            stopwatch.Restart();
            try
            {
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/ignored-headers"))
                {
                    SecureDownload.DownloadBytesAsync(
                        ignoredHeadersHandler,
                        request,
                        1024,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                ignoredHeadersTimedOut = true;
            }
            stopwatch.Stop();
            ignoredHeadersHandler.CompleteResponse();
            bool lateResponseDisposed = SpinWait.SpinUntil(
                delegate
                {
                    return ignoredHeadersHandler.ResponseContentDisposed;
                },
                TimeSpan.FromSeconds(2));
            ignoredHeadersHandler.Dispose();
            Check(
                ignoredHeadersTimedOut &&
                lateResponseDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "catalog deadline races cancellation-ignoring headers and disposes the late response",
                ref failures,
                output);

            bool streamAcquisitionTimedOut = false;
            var streamHandler = new BlockingReadAsStreamHandler();
            stopwatch.Restart();
            try
            {
                using (streamHandler)
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/stream-acquisition"))
                {
                    SecureDownload.DownloadBytesAsync(
                        streamHandler,
                        request,
                        1024,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                streamAcquisitionTimedOut = true;
            }
            stopwatch.Stop();
            Check(
                streamAcquisitionTimedOut &&
                streamHandler.ContentDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "catalog deadline bounds cancellation-ignoring response stream acquisition",
                ref failures,
                output);

            bool bodyTimedOut = false;
            var bodyHandler = new BlockingBodyHandler();
            stopwatch.Restart();
            try
            {
                using (bodyHandler)
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/body"))
                {
                    SecureDownload.DownloadBytesAsync(
                        bodyHandler,
                        request,
                        1024,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                bodyTimedOut = true;
            }
            stopwatch.Stop();
            Check(
                bodyTimedOut &&
                bodyHandler.StreamDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "catalog deadline bounds cancellation-ignoring response body and disposes it",
                ref failures,
                output);
        }

        private static void CheckRestartLifecycle(
            ref int failures,
            TextWriter output)
        {
            var events = new List<string>();
            Program.CompleteInstanceLifecycle(
                new CallbackDisposable(delegate { events.Add("lease"); }),
                delegate
                {
                    events.Add("consume");
                    return true;
                },
                delegate { events.Add("launch"); });
            Check(
                events.Count == 3 &&
                events[0] == "lease" &&
                events[1] == "consume" &&
                events[2] == "launch",
                "restart launches only after the instance lease is released",
                ref failures,
                output);

            bool launchedWithoutRequest = false;
            Program.CompleteInstanceLifecycle(
                new CallbackDisposable(delegate { }),
                delegate { return false; },
                delegate { launchedWithoutRequest = true; });
            Check(
                !launchedWithoutRequest,
                "restart lifecycle does not launch without a request",
                ref failures,
                output);

            bool requestedAfterFailedSave = false;
            bool saveAccepted = Program.TryRequestRestartAfterSave(
                delegate { return false; },
                delegate { requestedAfterFailedSave = true; });
            bool launchedAfterFailedSave = false;
            Program.CompleteInstanceLifecycle(
                new CallbackDisposable(delegate { }),
                delegate { return requestedAfterFailedSave; },
                delegate { launchedAfterFailedSave = true; });
            Check(
                !saveAccepted &&
                !requestedAfterFailedSave &&
                !launchedAfterFailedSave,
                "failed persistence neither requests nor launches a restart",
                ref failures,
                output);

            // S6: a module install/uninstall restart carries the pane to reopen on relaunch.
            Program.RequestRestart("Modules");
            Check(
                Program.RestartReopenPaneForSelfTest == "Modules",
                "restart request carries the reopen-pane payload",
                ref failures,
                output);
            Program.RequestRestart();
            Check(
                Program.RestartReopenPaneForSelfTest == null,
                "a plain restart request clears any previous reopen-pane payload",
                ref failures,
                output);

            CheckPokeArbitration(ref failures, output);
        }

        /// <summary>
        /// The poke-1 responder chain: highest priority wins by default, a declining responder falls through
        /// to the next, an explicit "Trigger Speech" choice restricts the offer to exactly that module (and
        /// stays silent if it declines rather than falling back), and disposal unregisters.
        /// </summary>
        private static void CheckPokeArbitration(ref int failures, TextWriter output)
        {
            var host = new DesktopAICompanion.Plugins.CompanionHost(null);
            var calls = new List<string>();

            bool fortunesSpeaks = true, brainSpeaks = true;
            IDisposable fortunes = host.RegisterPokeResponder("fortunes", 0,
                delegate { calls.Add("fortunes"); return fortunesSpeaks; });
            IDisposable brain = host.RegisterPokeResponder("aibrain", 10,
                delegate { calls.Add("aibrain"); return brainSpeaks; });

            Check(
                host.PokeResponderModuleIds.Count == 2 &&
                host.PokeResponderModuleIds[0] == "aibrain" &&
                host.PokeResponderModuleIds[1] == "fortunes",
                "poke responders are listed highest-priority first",
                ref failures,
                output);

            // An explicit choice offers ONLY that module, whatever the priority order says.
            calls.Clear();
            bool handledFortunes = host.RaisePokeReactionFor(null, "fortunes");
            Check(
                handledFortunes && calls.Count == 1 && calls[0] == "fortunes",
                "an explicit trigger-speech choice offers only that module",
                ref failures,
                output);

            // ...and when that one declines, nothing else speaks (a choice is a restriction).
            calls.Clear();
            fortunesSpeaks = false;
            bool handledDeclined = host.RaisePokeReactionFor(null, "fortunes");
            Check(
                !handledDeclined && calls.Count == 1 && calls[0] == "fortunes",
                "a declining chosen module does not fall through to another module",
                ref failures,
                output);
            fortunesSpeaks = true;

            // An unknown module id (uninstalled since the choice was saved) simply stays silent.
            calls.Clear();
            Check(
                !host.RaisePokeReactionFor(null, "not-installed") && calls.Count == 0,
                "an unresolvable trigger-speech choice speaks nothing",
                ref failures,
                output);

            // Default (random) reaches exactly one speaker when every responder accepts.
            calls.Clear();
            Check(
                host.RaisePokeReactionFor(null, "") && calls.Count == 1,
                "the default random pick stops at the first responder that speaks",
                ref failures,
                output);

            // Default with everyone declining: all are offered, result is silence.
            calls.Clear();
            fortunesSpeaks = false;
            brainSpeaks = false;
            bool handledNone = host.RaisePokeReactionFor(null, "");
            Check(
                !handledNone && calls.Count == 2,
                "the default random pick offers every responder before giving up",
                ref failures,
                output);
            fortunesSpeaks = true;
            brainSpeaks = true;

            // Disposal unregisters, so an uninstalled module is no longer offered or listed.
            fortunes.Dispose();
            brain.Dispose();
            calls.Clear();
            Check(
                host.PokeResponderModuleIds.Count == 0 && !host.RaisePokeReactionFor(null, "") && calls.Count == 0,
                "disposing a poke responder unregisters it",
                ref failures,
                output);
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (T)
            {
                return true;
            }
        }

        private sealed class DiagnosticOwnedValue : IDisposable
        {
            private int disposeCount;

            internal int DisposeCount
            {
                get { return Volatile.Read(ref disposeCount); }
            }

            public void Dispose()
            {
                Interlocked.Increment(ref disposeCount);
            }
        }

        private sealed class BlockingHeadersHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken)
                    .ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }

        private sealed class CancellationIgnoringHeadersHandler
            : HttpMessageHandler
        {
            private readonly TaskCompletionSource<HttpResponseMessage> _pending =
                new TaskCompletionSource<HttpResponseMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private DisposalTrackingContent _content;

            public bool ResponseContentDisposed
            {
                get { return _content != null && _content.IsDisposed; }
            }

            public void CompleteResponse()
            {
                _content = new DisposalTrackingContent();
                _pending.TrySetResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = _content
                    });
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                // Deliberately ignore cancellation until the test releases the response.
                return _pending.Task;
            }
        }

        private sealed class DisposalTrackingContent : HttpContent
        {
            public bool IsDisposed { get; private set; }

            protected override Task SerializeToStreamAsync(
                Stream stream,
                TransportContext context)
            {
                return Task.CompletedTask;
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return true;
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed class BlockingBodyHandler : HttpMessageHandler
        {
            private BlockingReadStream _stream;

            public bool StreamDisposed
            {
                get { return _stream != null && _stream.IsDisposed; }
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _stream = new BlockingReadStream();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(_stream)
                });
            }
        }

        private sealed class BlockingReadAsStreamHandler : HttpMessageHandler
        {
            private BlockingReadAsStreamContent _content;

            public bool ContentDisposed
            {
                get { return _content != null && _content.IsDisposed; }
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _content = new BlockingReadAsStreamContent();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = _content
                });
            }
        }

        private sealed class BlockingReadAsStreamContent : HttpContent
        {
            private readonly TaskCompletionSource<Stream> _pending =
                new TaskCompletionSource<Stream>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public bool IsDisposed { get; private set; }

            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                // .NET Framework exposes no cancellation token for this operation.
                return _pending.Task;
            }

            protected override Task SerializeToStreamAsync(
                Stream stream,
                TransportContext context)
            {
                return Task.FromException(
                    new NotSupportedException("Serialization is not used by this test."));
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                if (disposing)
                    _pending.TrySetException(
                        new ObjectDisposedException("BlockingReadAsStreamContent"));
                base.Dispose(disposing);
            }
        }

        private sealed class BlockingReadStream : Stream
        {
            private readonly TaskCompletionSource<int> _pending =
                new TaskCompletionSource<int>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public bool IsDisposed { get; private set; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
            }

            public override int Read(
                byte[] buffer,
                int offset,
                int count)
            {
                throw new NotSupportedException();
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                // Deliberately ignore cancellation to reproduce .NET Framework transport streams
                // that leave ReadAsync pending after the supplied token has been canceled.
                return _pending.Task;
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                if (disposing)
                    _pending.TrySetException(
                        new ObjectDisposedException("BlockingReadStream"));
                base.Dispose(disposing);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private Action _callback;

            public CallbackDisposable(Action callback)
            {
                _callback = callback;
            }

            public void Dispose()
            {
                Action callback = Interlocked.Exchange(ref _callback, null);
                if (callback != null) callback();
            }
        }

        private static void CheckExpression(
            string expression,
            int expected,
            ref int failures,
            TextWriter output)
        {
            int actual;
            try
            {
                actual = SafeExpression.Evaluate(expression, delegate(string name)
                {
                    if (name == "screenW") return 100.0;
                    if (name == "imageW") return 20.0;
                    return 17.0;
                });
            }
            catch (Exception ex)
            {
                Check(false, "expression " + expression + " threw " + ex.Message,
                    ref failures, output);
                return;
            }
            Check(actual == expected, "expression: " + expression, ref failures, output);
        }

        private static void Check(
            bool condition,
            string name,
            ref int failures,
            TextWriter output)
        {
            if (condition)
            {
                output.WriteLine("[PASS] " + name);
            }
            else
            {
                failures++;
                output.WriteLine("[FAIL] " + name);
            }
        }

        private static string FormatError(string error)
        {
            return string.IsNullOrWhiteSpace(error) ? "" : " (" + error + ")";
        }
    }
}
