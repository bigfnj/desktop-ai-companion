using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;

namespace DesktopAICompanion.Tools.ShimejiConvert
{
    /// <summary>Metadata chosen by the caller of the whole-pack conversion. Member names are exact source keys.</summary>
    public sealed class PackConversionOptions
    {
        public string MainMember { get; set; }
        public IDictionary<string, PackMemberOptions> Members { get; set; }
        public PackStartupOptions Startup { get; set; }
        public bool IncludeParityMaps { get; set; }
        /// <summary>Decision 64: recover a next entry that names no root behaviour by dropping it, with a warning in
        /// Warnings and in its member's residue (policy:next-missing-dropped, a divergence, never exact). False, the
        /// default, refuses such an entry with TICK_NEXT_MISSING. Only an explicit choice sets it; this call reads it once,
        /// into its own snapshot. An entry under a scripted condition is refused either way.</summary>
        public bool RecoverMissingNext { get; set; }
    }

    public sealed class PackMemberOptions
    {
        /// <summary>main, egg, form or helper. A main role must agree with MainMember.</summary>
        public string Role { get; set; }
        /// <summary>When absent, the selected main is visible; other members preserve the source chooser's visibility rule.</summary>
        public bool? Hidden { get; set; }
    }

    public sealed class PackStartupOptions
    {
        public string Member { get; set; }
        /// <summary>An exact source root-behaviour name, or an unambiguous emitted name, in the selected member.</summary>
        public string Behaviour { get; set; }
    }

    public enum PackMemberDisposition { Prepared, Refused }

    /// <summary>One member's conversion report. A refused member has no usable document or parity map.</summary>
    public sealed class PackMemberConversionResult
    {
        public string SourceKey { get; private set; }
        public XmlData.RootNode Root { get; private set; }
        public string EmittedXml { get; private set; }
        public string ParityMapJson { get; private set; }
        public ResidueReport Residue { get; private set; }
        public GraphReport Graph { get; private set; }
        public PackMemberDisposition Disposition { get; private set; }
        /// <summary>The real per-document validator accepted this member; whole-pack acceptance is separate.</summary>
        public bool Valid { get; private set; }
        /// <summary>This member survives deep copy and canonical parse/serialization without a field change.</summary>
        public bool RoundTrips { get; private set; }
        public string Error { get; private set; }

        internal PackMemberConversionResult(string key, XmlData.RootNode root, string xml, string map,
            ResidueReport residue, GraphReport graph, bool valid, bool roundTrips, string error)
        {
            SourceKey = key;
            Residue = residue ?? new ResidueReport();
            Graph = graph;
            Valid = valid;
            RoundTrips = roundTrips;
            Error = error;
            Disposition = error == null && root != null && valid && roundTrips
                ? PackMemberDisposition.Prepared : PackMemberDisposition.Refused;
            if (Disposition == PackMemberDisposition.Prepared)
            {
                Root = root;
                EmittedXml = xml;
                ParityMapJson = map;
            }
        }
    }

    /// <summary>A diagnostic result for the complete pack. Install or save members only when Accepted is true.</summary>
    public sealed class PackConversionResult
    {
        /// <summary>The complete source pack's identity; null if source preparation refused any member.</summary>
        public string PackId { get; private set; }
        public string MainMember { get; private set; }
        public IReadOnlyList<PackMemberConversionResult> Members { get; private set; }
        public IReadOnlyList<string> Errors { get; private set; }
        public IReadOnlyList<string> Warnings { get; private set; }
        public bool Accepted { get; private set; }
        public string Error { get; private set; }

        internal PackConversionResult(string packId, string mainMember, IEnumerable<PackMemberConversionResult> members,
            IEnumerable<string> errors, IEnumerable<string> warnings)
        {
            PackId = packId;
            MainMember = mainMember;
            Members = new List<PackMemberConversionResult>(members).AsReadOnly();
            Errors = new List<string>(errors).AsReadOnly();
            Warnings = new List<string>(warnings).AsReadOnly();
            Error = Errors.Count == 0 ? null : Errors[0];
            Accepted = Errors.Count == 0 && Members.Count > 0
                && Members.All(member => member.Disposition == PackMemberDisposition.Prepared);
        }
    }

    /// <summary>Prepares literal tick-format members entirely in memory and applies the real format and pack checks.</summary>
    internal static class PackConverter
    {
        private static readonly XNamespace Ns = "https://esheep.petrucci.ch/";

        private sealed class OptionsSnapshot
        {
            public string MainMember;
            public readonly Dictionary<string, MemberSnapshot> Members = new Dictionary<string, MemberSnapshot>(StringComparer.Ordinal);
            public bool IncludeParityMaps, HasStartup, RecoverMissingNext;
            public string StartupMember, StartupBehaviour;
        }

        private sealed class MemberSnapshot
        {
            public string Role;
            public bool? Hidden;
        }

        internal static PackConversionResult Convert(string extractedPackRoot, PackConversionOptions options, out string error)
        {
            return Convert(extractedPackRoot, options, XmlData.NodeCopy.Deep<XmlData.RootNode>, ShimejiEngine.Serialize, out error);
        }

        /// <summary>Call-scoped verification dependencies let self-tests inject a lossy copy or unstable serialization
        /// without changing another conversion. The public entry point always supplies the production operations.</summary>
        internal static PackConversionResult Convert(string extractedPackRoot, PackConversionOptions options,
            Func<XmlData.RootNode, XmlData.RootNode> copy, Func<XmlData.RootNode, string> serialize, out string error)
        {
            var members = new List<PackMemberConversionResult>();
            var errors = new List<string>();
            var warnings = new List<string>();
            OptionsSnapshot snapshot = null;
            string identity = null;
            try
            {
                snapshot = Snapshot(options, errors);
                if (errors.Count == 0)
                {
                    List<TickPackEmitter.PreparedMember> prepared = TickPackEmitter.PrepareMembers(extractedPackRoot, true, snapshot.RecoverMissingNext);
                    var keys = new HashSet<string>(prepared.Select(member => member.Name), StringComparer.Ordinal);
                    if (!keys.Contains(snapshot.MainMember))
                        errors.Add("TICK_PACK_MAIN_UNKNOWN: the requested main image set is not in this pack: " + snapshot.MainMember);
                    foreach (string key in snapshot.Members.Keys.OrderBy(key => key, StringComparer.Ordinal))
                        if (!keys.Contains(key)) errors.Add("TICK_PACK_OPTIONS_UNKNOWN: member metadata names an image set that is not in this pack: " + key);

                    List<TickPackEmitter.PreparedMember> ready = prepared.Where(member => member.Error == null).ToList();
                    if (snapshot.HasStartup)
                    {
                        TickPackEmitter.PreparedMember target = ready.SingleOrDefault(member => member.Name == snapshot.StartupMember);
                        if (target != null)
                        {
                            string startupError;
                            string mapped = ResolveStartupBehaviour(target, snapshot.StartupBehaviour, out startupError);
                            if (startupError == null) snapshot.StartupBehaviour = mapped;
                            else errors.Add(startupError);
                        }
                    }
                    // A subset identity is useful only inside refused-pack diagnostic previews. It must not be
                    // exposed as the identity of the complete source pack.
                    string previewIdentity = ready.Count == 0 ? null : TickPackEmitter.SourcePackId(ready);
                    if (ready.Count == prepared.Count) identity = previewIdentity;
                    foreach (TickPackEmitter.PreparedMember member in prepared)
                    {
                        PackMemberConversionResult report;
                        if (member.Error != null)
                            report = Refused(member.Name, member.Error);
                        else
                            report = Prepare(member, previewIdentity, snapshot, copy, serialize);
                        members.Add(report);
                        foreach (string warning in member.Warnings)
                        {
                            string diagnostic = "Image set '" + member.Name + "': " + warning;
                            warnings.Add(diagnostic);
                            report.Residue.Notes.Add(diagnostic);
                        }
                        if (report.Error != null) errors.Add("Image set '" + member.Name + "': " + report.Error);
                        else
                        {
                            if (report.Graph.Unreachable.Count > 0)
                                warnings.Add("Image set '" + member.Name + "' retains " + report.Graph.Unreachable.Count
                                    + " source action or behaviour nodes that the reachability graph cannot enter.");
                            foreach (string warning in RuntimeEntryWarnings(report))
                            {
                                warnings.Add(warning);
                                report.Residue.Notes.Add(warning);
                            }
                        }
                    }

                    var packMembers = new List<PackValidator.Member>();
                    foreach (PackMemberConversionResult member in members)
                        if (member.Disposition == PackMemberDisposition.Prepared)
                            packMembers.Add(new PackValidator.Member("Image set '" + member.SourceKey + "'", member.Root));
                    if (packMembers.Count > 0)
                    {
                        PackValidator.Report pack = PackValidator.Validate(packMembers);
                        errors.AddRange(pack.Errors);
                        warnings.AddRange(pack.Warnings);
                        foreach (PackMemberConversionResult member in members)
                            foreach (string warning in pack.Warnings)
                                if (warning.StartsWith("Image set '" + member.SourceKey + "' ", StringComparison.Ordinal))
                                    member.Residue.Notes.Add(warning);
                    }
                    else if (errors.Count == 0) errors.Add("TICK_PACK_EMPTY: the source pack has no prepared image sets.");
                }
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                errors.Add("TICK_PACK_REFUSED: " + ex.Message);
            }

            var result = new PackConversionResult(identity, snapshot == null ? null : snapshot.MainMember, members, errors, warnings);
            error = result.Error;
            return result;
        }

        internal static string ResolveStartupBehaviour(TickPackEmitter.PreparedMember member, string requested, out string error)
        {
            string[] matches = member.BehaviourNames.Where(pair => pair.Key == requested || pair.Value == requested)
                .Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (matches.Length == 1)
            {
                error = null;
                return matches[0];
            }
            error = (matches.Length == 0 ? "TICK_PACK_STARTUP_UNMAPPED" : "TICK_PACK_STARTUP_AMBIGUOUS")
                + ": image set '" + member.Name + "' cannot resolve startup behaviour '" + requested + "'.";
            return null;
        }

        private static OptionsSnapshot Snapshot(PackConversionOptions options, List<string> errors)
        {
            var snapshot = new OptionsSnapshot();
            if (options == null)
            {
                errors.Add("TICK_PACK_OPTIONS_REQUIRED: choose the main image set before converting a pack.");
                return snapshot;
            }
            snapshot.MainMember = options.MainMember;
            snapshot.IncludeParityMaps = options.IncludeParityMaps;
            snapshot.RecoverMissingNext = options.RecoverMissingNext;
            if (string.IsNullOrEmpty(snapshot.MainMember))
                errors.Add("TICK_PACK_MAIN_REQUIRED: provide the exact main image-set name.");
            else if (!ShimejiFormatValidator.IsMemberKey(snapshot.MainMember))
                errors.Add("TICK_PACK_MAIN_INVALID: the main image-set name is not a valid member key.");

            IDictionary<string, PackMemberOptions> suppliedMembers = options.Members;
            if (suppliedMembers != null)
                foreach (KeyValuePair<string, PackMemberOptions> pair in suppliedMembers)
                {
                    if (!ShimejiFormatValidator.IsMemberKey(pair.Key))
                    {
                        errors.Add("TICK_PACK_MEMBER_INVALID: member metadata has an invalid image-set key.");
                        continue;
                    }
                    if (pair.Value == null)
                    {
                        errors.Add("TICK_PACK_MEMBER_OPTIONS_REQUIRED: member metadata is null for '" + pair.Key + "'.");
                        continue;
                    }
                    var member = new MemberSnapshot { Role = pair.Value.Role, Hidden = pair.Value.Hidden };
                    if (member.Role != null && member.Role != "main" && member.Role != "egg" && member.Role != "form" && member.Role != "helper")
                        errors.Add("TICK_PACK_ROLE_INVALID: image set '" + pair.Key + "' has unknown role '" + member.Role + "'.");
                    if (pair.Key == snapshot.MainMember && member.Role != null && member.Role != "main"
                        || pair.Key != snapshot.MainMember && member.Role == "main")
                        errors.Add("TICK_PACK_ROLE_CONFLICT: the main role must agree with MainMember for image set '" + pair.Key + "'.");
                    if (pair.Key == snapshot.MainMember && member.Hidden == true)
                        errors.Add("TICK_PACK_MAIN_HIDDEN: the main image set '" + pair.Key + "' must be visible.");
                    snapshot.Members.Add(pair.Key, member);
                }

            PackStartupOptions startup = options.Startup;
            if (startup != null)
            {
                snapshot.HasStartup = true;
                snapshot.StartupMember = startup.Member;
                snapshot.StartupBehaviour = startup.Behaviour;
                if (!ShimejiFormatValidator.IsMemberKey(snapshot.StartupMember))
                    errors.Add("TICK_PACK_STARTUP_MEMBER_INVALID: startup needs an exact image-set member key.");
                if (string.IsNullOrWhiteSpace(snapshot.StartupBehaviour))
                    errors.Add("TICK_PACK_STARTUP_BEHAVIOUR_REQUIRED: startup needs a behaviour name.");
            }
            return snapshot;
        }

        private static PackMemberConversionResult Prepare(TickPackEmitter.PreparedMember member, string identity, OptionsSnapshot options,
            Func<XmlData.RootNode, XmlData.RootNode> copy, Func<XmlData.RootNode, string> serialize)
        {
            bool valid = false;
            var residue = new ResidueReport();
            try
            {
                MemberSnapshot metadata;
                options.Members.TryGetValue(member.Name, out metadata);
                bool sourceHidden = TickPackEmitter.HiddenInSourceChooser(member.Name);
                string role = member.Name == options.MainMember ? "main" : sourceHidden ? "helper" : "form";
                if (metadata != null && metadata.Role != null) role = metadata.Role;
                bool hidden = metadata != null && metadata.Hidden.HasValue ? metadata.Hidden.Value
                    : role != "main" && (sourceHidden || role == "egg" || role == "helper");

                XDocument document = XDocument.Parse(member.Xml);
                XElement header = document.Root.Element(Ns + "header");
                if (header == null) throw new InvalidDataException("Prepared member has no header.");
                XElement features = header.Element(Ns + "features");
                if (features == null) throw new InvalidDataException("Prepared member has no feature declaration.");
                if (!CompanionFormatFeatures.Parse(features.Value).Contains(CompanionFormatFeatures.Packs))
                    features.Value += " " + CompanionFormatFeatures.Packs;
                header.Elements(Ns + "pack").Remove();
                header.Elements(Ns + "startup").Remove();
                header.Add(new XElement(Ns + "pack", new XAttribute("id", identity), new XAttribute("member", member.Name),
                    new XAttribute("role", role), new XAttribute("hidden", hidden)));
                if (member.Name == options.MainMember && options.HasStartup)
                    header.Add(new XElement(Ns + "startup", new XAttribute("member", options.StartupMember),
                        new XAttribute("behaviour", options.StartupBehaviour)));

                XmlData.RootNode parsed;
                string validationError;
                if (!CompanionXmlValidator.TryParseWithFeatures(document.ToString(), CompanionFormatFeatures.All, out parsed, out validationError))
                    return Refused(member.Name, "validation failed: " + validationError, residue);
                valid = true;
                string canonical = serialize(parsed);
                XmlData.RootNode copied = copy(parsed);
                string copiedXml = serialize(copied);
                if (!string.Equals(canonical, copiedXml, StringComparison.Ordinal))
                    return Refused(member.Name, "deep copy changed the serialized document: " + FirstDifference(canonical, copiedXml), residue, true);

                XmlData.RootNode reparsed;
                if (!CompanionXmlValidator.TryParseWithFeatures(copiedXml, CompanionFormatFeatures.All, out reparsed, out validationError))
                    return Refused(member.Name, "copied document failed validation: " + validationError, residue, true);
                string roundTrip = serialize(reparsed);
                if (!string.Equals(copiedXml, roundTrip, StringComparison.Ordinal))
                    return Refused(member.Name, "the emitted XML is not a fixed point of parse -> serialize: "
                        + FirstDifference(copiedXml, roundTrip), residue, true);
                GraphReport graph = ShimejiEngine.Analyze(reparsed);
                if (sourceHidden)
                    residue.Notes.Add(hidden ? "The source chooser hides this image set; it remains hidden in the converted pack."
                        : "The caller selected this source-hidden image set as visible in the converted pack.");
                if (graph.Unreachable.Count > 0)
                    residue.Notes.Add(graph.Unreachable.Count + " source action or behaviour nodes have no reachable graph entry; they remain in the document.");
                if (options.IncludeParityMaps && string.IsNullOrEmpty(member.Map))
                    return Refused(member.Name, "A parity map was requested but none was prepared.", residue, true);
                return new PackMemberConversionResult(member.Name, reparsed, copiedXml,
                    options.IncludeParityMaps ? member.Map : null, residue, graph, true, true, null);
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                return Refused(member.Name, ex.Message, residue, valid);
            }
        }

        private static PackMemberConversionResult Refused(string key, string error, ResidueReport residue = null, bool valid = false)
        {
            if (residue == null) residue = new ResidueReport();
            residue.Notes.Add("This image set was refused: " + error);
            return new PackMemberConversionResult(key, null, null, null, residue, null, valid, false, error);
        }

        private static string FirstDifference(string expected, string actual)
        {
            int offset = 0, common = Math.Min(expected.Length, actual.Length);
            while (offset < common && expected[offset] == actual[offset]) offset++;
            return "offset " + offset + " (length " + expected.Length + " became " + actual.Length + ")";
        }

        private static IEnumerable<string> RuntimeEntryWarnings(PackMemberConversionResult member)
        {
            XmlData.AnimationNode[] nodes = member.Root.Animations.Animation;
            // Only the route's behaviours can be entered by name. A next-list-only namesake does not supply one.
            var rootIds = new HashSet<int>(nodes.Where(node => node.Route != null)
                .SelectMany(node => node.Sequence.Next ?? new XmlData.NextNode[0]).Select(next => next.Value));
            var names = new HashSet<string>(nodes.Where(node => rootIds.Contains(node.Id) && node.Behaviour != null)
                .Select(node => node.Behaviour.Name), StringComparer.Ordinal);
            string prefix = "Image set '" + member.SourceKey + "' has no ";
            if (!names.Contains("Fall"))
                yield return prefix + "Fall behaviour; arrival or a fall cannot enter it, so the companion is disposed.";
            if (!names.Contains("Dragged"))
                yield return prefix + "Dragged behaviour; a press that starts dragging disposes the companion.";
            if (!names.Contains("Thrown"))
                yield return prefix + "Thrown behaviour; releasing a dragged companion disposes it.";
        }

        private static bool IsRefusal(Exception exception)
        {
            return exception is InvalidDataException || exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException
                || exception is InvalidOperationException || exception is NotSupportedException || exception is OverflowException
                || exception is XmlException || exception is System.Security.SecurityException
                || exception is System.Runtime.InteropServices.ExternalException;
        }
    }
}
