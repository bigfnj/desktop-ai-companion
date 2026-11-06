using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopAICompanion.Modules;
using DesktopAICompanion.Tools.ShimejiConvert;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;
using DesktopAICompanion.Tools.ShimejiConvert.Shimeji;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>One row of the pack dialog: a source image set by its EXACT key (the folder name under img\, which the
    /// converter and the host match case-sensitively), and the role and visibility the author chose for it.</summary>
    internal sealed class PackMemberChoice
    {
        public string Key;
        public string Role;
        public bool Hidden;
    }

    /// <summary>What "Install pack" did: every member, or the members it installed before it stopped and why.</summary>
    internal sealed class PackInstallOutcome
    {
        public bool Complete;
        /// <summary>The install replaced an earlier install of the same pack (same ids), rather than adding one.</summary>
        public bool Replaced;
        public readonly List<string> InstalledKeys = new List<string>();
        public readonly List<string> InstalledIds = new List<string>();
        public string FailedKey;
        public string Message;
    }

    /// <summary>
    /// Companion Studio's half of a whole-pack import (1.3.0): the options the author's choices become, the guarded
    /// call into ShimejiEngine.ConvertPack, the installed ids and the install itself. Everything here is pure or takes
    /// its converter and its ICompanionManager as arguments, so PetStudioModule.SelfTest drives every rule with a
    /// synthetic pack and a recording manager; the window (PetStudioWindow.ImportPackAsync) only wires it to dialogs
    /// and threads.
    ///
    /// Nothing is guessed from folder names (the converter lane's rule, recorded with its decision): the main member is
    /// the skin the author highlighted, every other member is a form unless the SOURCE chooser hides it (a leading dot,
    /// or "unused"), and a startup is written only when the author chose one.
    /// </summary>
    internal static class PackImport
    {
        public const string RoleMain = "main";
        public const string RoleEgg = "egg";
        public const string RoleForm = "form";
        public const string RoleHelper = "helper";

        /// <summary>The roles a non-main member can take, in the order the dialog offers them.</summary>
        internal static readonly string[] OtherRoles = { RoleForm, RoleEgg, RoleHelper };

        /// <summary>The host's id rule (SecureDownload.SafeIdPattern, src/dotNet/SecureDownload.cs): 1 to 64 characters,
        /// starting and ending with a letter or digit. A module cannot call it, so the length is mirrored here and the
        /// self-test checks every generated id against the same pattern.</summary>
        internal const int HostIdMaxLength = 64;

        internal const string NotAPackLayout =
            "This skin is not laid out as a pack (an img folder holding one folder per character), so it can only be converted on its own.";

        /// <summary>The source chooser's own visibility rule, read from the converter rather than copied: a leading dot,
        /// or "unused" ignoring case.</summary>
        internal static bool SourceHidden(string key)
        {
            return TickPackEmitter.HiddenInSourceChooser(key ?? "");
        }

        /// <summary>The dialog's starting rows, in the converter's member order (ordinal): the highlighted skin is the
        /// main member and visible; a member the source chooser hides is a hidden helper, as the converter itself would
        /// make it; every other member is a visible form.</summary>
        internal static List<PackMemberChoice> DefaultChoices(IEnumerable<string> keys, string mainKey)
        {
            var rows = new List<PackMemberChoice>();
            foreach (string key in (keys ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrEmpty(k))
                .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            {
                if (string.Equals(key, mainKey, StringComparison.Ordinal))
                    rows.Add(new PackMemberChoice { Key = key, Role = RoleMain, Hidden = false });
                else if (SourceHidden(key))
                    rows.Add(new PackMemberChoice { Key = key, Role = RoleHelper, Hidden = true });
                else
                    rows.Add(new PackMemberChoice { Key = key, Role = RoleForm, Hidden = false });
            }
            return rows;
        }

        /// <summary>What the Hidden box resets to when the author changes a member's role: the converter's default rule
        /// (PackConverter.Prepare), an egg or a helper hidden and a source-hidden member kept hidden. The main member is
        /// never hidden.</summary>
        internal static bool DefaultHidden(string key, string role)
        {
            if (role == RoleMain) return false;
            return SourceHidden(key) || role == RoleEgg || role == RoleHelper;
        }

        /// <summary>
        /// The ConvertPack options for the author's choices, or null with the reason in <paramref name="refusal"/>.
        /// Every member gets an EXPLICIT role and visibility, so the converter guesses nothing: in particular the main
        /// member is sent as visible even when its folder starts with a dot, which the converter would otherwise hide
        /// (review M2: a hidden main was accepted, and a main that cannot be added is a pack nobody can use). A main the
        /// author marked hidden is refused here, before the API is called. The startup is written only when the author
        /// chose a starting member; a behaviour typed with no member is not a startup.
        /// </summary>
        internal static PackConversionOptions SelectPackOptions(IReadOnlyList<PackMemberChoice> members, string mainKey,
            string startupMember, string startupBehaviour, out string refusal)
        {
            refusal = null;
            if (members == null || members.Count == 0) { refusal = "This pack has no image sets to convert."; return null; }
            if (string.IsNullOrEmpty(mainKey)) { refusal = "Choose the main member first."; return null; }
            PackMemberChoice main = members.FirstOrDefault(m => m != null && string.Equals(m.Key, mainKey, StringComparison.Ordinal));
            if (main == null) { refusal = "The main member '" + mainKey + "' is not one of this pack's image sets."; return null; }
            if (main.Hidden)
            {
                refusal = "The main member cannot be hidden: it is the one you add from Add a companion, and a hidden member never appears there.";
                return null;
            }

            var options = new Dictionary<string, PackMemberOptions>(StringComparer.Ordinal);
            foreach (PackMemberChoice m in members)
            {
                if (m == null || string.IsNullOrEmpty(m.Key)) continue;
                bool isMain = string.Equals(m.Key, mainKey, StringComparison.Ordinal);
                string role = isMain ? RoleMain : m.Role;
                if (!isMain && role == RoleMain)
                {
                    refusal = "Only the main member can have the main role, and '" + m.Key + "' is not the main member.";
                    return null;
                }
                if (!isMain && Array.IndexOf(OtherRoles, role) < 0)
                {
                    refusal = "'" + m.Key + "' has no role Companion Studio knows (" + (role ?? "none") + ").";
                    return null;
                }
                if (options.ContainsKey(m.Key)) continue;
                options.Add(m.Key, new PackMemberOptions { Role = role, Hidden = isMain ? false : m.Hidden });
            }

            PackStartupOptions startup = null;
            if (!string.IsNullOrEmpty(startupMember))
            {
                if (!options.ContainsKey(startupMember))
                {
                    refusal = "The starting member '" + startupMember + "' is not one of this pack's image sets.";
                    return null;
                }
                string behaviour = (startupBehaviour ?? "").Trim();
                if (behaviour.Length == 0)
                {
                    refusal = "Name the behaviour '" + startupMember + "' starts with, or choose no starting member.";
                    return null;
                }
                startup = new PackStartupOptions { Member = startupMember, Behaviour = behaviour };
            }
            return new PackConversionOptions { MainMember = mainKey, Members = options, Startup = startup, IncludeParityMaps = false };
        }

        /// <summary>
        /// Call the converter, and turn every way it can fail to answer into a refusal. Two guards that are the HOST's,
        /// not the converter's, and stay after the converter lane's fixes because they cost nothing:
        /// a main member not sent as explicitly visible is refused before the call (review M2), and an exception is
        /// caught and reported as a refusal rather than reaching the import's generic "Import failed" (review M1:
        /// ConvertPack threw InvalidDataException for a root with no img folder, an empty one, or a junction).
        /// Runs on the pool thread the window hands it to; it touches no UI.
        /// </summary>
        internal static PackConversionResult ConvertGuarded(string packRoot, PackConversionOptions options,
            Func<string, PackConversionOptions, PackConversionResult> convert, out string refusal)
        {
            refusal = null;
            if (options == null || string.IsNullOrEmpty(options.MainMember))
            {
                refusal = "Choose the main member first.";
                return null;
            }
            PackMemberOptions main = null;
            if (options.Members != null) options.Members.TryGetValue(options.MainMember, out main);
            if (main == null || main.Hidden != false)
            {
                refusal = "The main member cannot be hidden: it is the one you add from Add a companion, and a hidden member never appears there.";
                return null;
            }
            PackConversionResult result;
            try { result = convert(packRoot, options); }
            catch (Exception ex)
            {
                refusal = "The converter could not read this pack: " + ex.Message;
                return null;
            }
            if (result == null) refusal = "The converter returned no result for this pack.";
            return result;
        }

        /// <summary>The production converter: the engine's in-memory pack API, which writes no files.</summary>
        internal static PackConversionResult ConvertWithEngine(string packRoot, PackConversionOptions options)
        {
            string ignored;
            return ShimejiEngine.ConvertPack(packRoot, options, out ignored);
        }

        /// <summary>The pack root a detected skin belongs to: the folder holding the img folder its sprites sit in
        /// (root\img\&lt;member&gt;), which is what ConvertPack reads. Null when the skin is not laid out that way (sprites at
        /// the root, or a deeper nesting), in which case only the single-skin conversion applies.</summary>
        internal static string PackRootOf(DetectedSkin skin)
        {
            if (skin == null || string.IsNullOrEmpty(skin.ImgDir)) return null;
            try
            {
                string member = Path.GetFullPath(skin.ImgDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string img = Path.GetDirectoryName(member);
                if (string.IsNullOrEmpty(img) || !string.Equals(Path.GetFileName(img), "img", StringComparison.OrdinalIgnoreCase)) return null;
                return Path.GetDirectoryName(img);
            }
            catch (Exception) { return null; }
        }

        /// <summary>A detected skin's member key: its sprite folder's own name, exactly as the converter reads it.</summary>
        internal static string MemberKeyOf(DetectedSkin skin)
        {
            if (skin == null || string.IsNullOrEmpty(skin.ImgDir)) return null;
            return Path.GetFileName(skin.ImgDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        /// <summary>Every member the converter will read: the folders directly under root\img, by exact name, in its
        /// order. Empty when the folder cannot be listed; the converter's own refusal then says why.</summary>
        internal static List<string> ListMemberKeys(string packRoot)
        {
            var keys = new List<string>();
            try
            {
                foreach (string dir in Directory.GetDirectories(Path.Combine(packRoot, "img")))
                    keys.Add(Path.GetFileName(dir));
            }
            catch (Exception) { keys.Clear(); }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        /// <summary>The name the pack's installed ids start with: the pack folder's own name, unless the pack IS the
        /// folder the import was handed, which for a zip is this module's extraction tree, and then the name the author
        /// chose (the zip's or the folder's).</summary>
        internal static string PackTitle(string packRoot, string importRoot, string importTitle)
        {
            string folder = null;
            try { folder = Path.GetFileName((packRoot ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch (Exception) { folder = null; }
            bool isImportRoot = false;
            try
            {
                isImportRoot = !string.IsNullOrEmpty(packRoot) && !string.IsNullOrEmpty(importRoot) && string.Equals(
                    Path.GetFullPath(packRoot).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(importRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { isImportRoot = false; }
            if ((isImportRoot || string.IsNullOrWhiteSpace(folder)) && !string.IsNullOrWhiteSpace(importTitle)) return importTitle.Trim();
            return string.IsNullOrWhiteSpace(folder) ? "Shimeji pack" : folder;
        }

        /// <summary>The studio's install-id rule, the one the single-skin import has always used: lower case, letters and
        /// digits kept, a space, '-' or '_' becomes '-', everything else dropped, no leading or trailing '-'.</summary>
        internal static string SafeId(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }
            string id = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(id) ? "shimeji-pet" : id;
        }

        /// <summary>
        /// One installed id per member: <see cref="SafeId"/> of "&lt;pack title&gt; &lt;member key&gt;", cut to the host's
        /// 64 characters, and made unique against the installed companions and the pack's own earlier members (ignoring
        /// case, as the library's folders do) by a "-2", "-3"... suffix. Distinct keys can share a slug ("A+B" and "AB",
        /// or two 255-character keys that differ after the cut), and a Windows device name is never an id. The exact
        /// member key is NOT carried by the id: the document keeps it in its header's pack member, which is what the
        /// host matches.
        /// </summary>
        internal static Dictionary<string, string> PackInstallIds(string packTitle, IEnumerable<string> keys, IEnumerable<string> takenIds)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (takenIds != null) foreach (string t in takenIds) if (!string.IsNullOrEmpty(t)) taken.Add(t);
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in keys ?? Enumerable.Empty<string>())
            {
                if (key == null || ids.ContainsKey(key)) continue;
                string slug = SafeId((packTitle ?? "") + " " + key);
                string id = FitId(slug, "");
                for (int n = 2; taken.Contains(id) || IsDeviceName(id); n++)
                    id = FitId(slug, "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                taken.Add(id);
                ids.Add(key, id);
            }
            return ids;
        }

        private static string FitId(string slug, string suffix)
        {
            int room = HostIdMaxLength - suffix.Length;
            string head = slug.Length > room ? slug.Substring(0, room) : slug;
            head = head.TrimEnd('-');
            if (head.Length == 0) head = "shimeji-pet";
            return head + suffix;
        }

        /// <summary>The names SecureDownload.IsSafeId refuses whatever their pattern: CON, PRN, AUX, NUL, COM1-9, LPT1-9
        /// (the part before the first '.', which a studio id never has).</summary>
        internal static bool IsDeviceName(string id)
        {
            string name = (id ?? "").Split('.')[0].ToUpperInvariant();
            if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL") return true;
            return name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                && name[3] >= '1' && name[3] <= '9';
        }

        /// <summary>Whether "Install pack" may be offered: the converter accepted the WHOLE pack, and there is a host to
        /// install into. A pack with a refused member has no complete identity (its PackId is null), and a subset of it
        /// installed would be a pack missing members nobody asked to drop.</summary>
        internal static bool CanInstall(PackConversionResult result, bool hostPresent)
        {
            return hostPresent && result != null && result.Accepted;
        }

        /// <summary>
        /// Install every member of an accepted pack through ICompanionManager.InstallType, which validates each one
        /// again and owns the path. Nothing is installed unless <see cref="PackConversionResult.Accepted"/>: a refused
        /// pack can still carry Prepared members, as diagnostic previews, and those must never reach the library.
        ///
        /// ORDER: the other members first, the main member LAST. A failure part way then leaves no main member behind, so
        /// the pack's own arrival (its startup egg, its births) is never offered half-installed. Add a companion drops only
        /// HIDDEN members (CompanionCatalog.EnumerateAddableFrom), so a visible member installed before the failure is
        /// addable on its own, and the outcome says which ones are. The first failure STOPS the install and the outcome
        /// names every member already installed, by key and id; Install pack again then replaces them under those ids
        /// (<see cref="PriorInstall"/>). Rejected: uninstalling them again, which deletes from the user's library on a
        /// path the author did not ask for, and whose own failure would leave less said than this does.
        ///
        /// On the UI thread, like the single install: InstallType swaps the copies on screen onto the new definition.
        /// </summary>
        internal static PackInstallOutcome InstallPack(PackConversionResult result, string packTitle, ICompanionManager pets)
        {
            return InstallPack(result, packTitle, pets, null);
        }

        /// <summary>
        /// The installed types that already carry this pack's id, by member key: what "Install pack" offers to replace.
        /// Read from the host's per-type pack facts (CompanionTypeInfo.PackId and PackMember, host 1.10.0), never guessed
        /// from ids. When two installed types claim one key (a second copy installed before 1.3.0 could replace), the first
        /// by id ignoring case is the one replaced, the same order the host's index takes within a folder. Empty when the
        /// pack has no id, nothing carries it, or the host cannot list its types.
        /// </summary>
        internal static Dictionary<string, string> PriorInstall(ICompanionManager pets, string packId)
        {
            var prior = new Dictionary<string, string>(StringComparer.Ordinal);
            if (pets == null || string.IsNullOrEmpty(packId)) return prior;
            IReadOnlyList<CompanionTypeInfo> installed;
            try { installed = pets.InstalledTypes(); }
            catch (Exception) { return prior; }
            if (installed == null) return prior;
            foreach (CompanionTypeInfo t in installed
                .Where(t => t != null && !t.IsBuiltIn && !string.IsNullOrEmpty(t.TypeId) && !string.IsNullOrEmpty(t.PackMember)
                            && string.Equals(t.PackId, packId, StringComparison.Ordinal))
                .OrderBy(t => t.TypeId, StringComparer.OrdinalIgnoreCase))
            {
                if (!prior.ContainsKey(t.PackMember)) prior.Add(t.PackMember, t.TypeId);
            }
            return prior;
        }

        /// <summary>The question "Install pack" asks before replacing an earlier install of the same pack. Pure.</summary>
        internal static string ReplaceQuestion(string packTitle, IReadOnlyDictionary<string, string> prior)
        {
            var parts = prior.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => "'" + p.Key + "' as '" + p.Value + "'");
            return "The pack '" + packTitle + "' is installed already, in whole or in part: " + string.Join(", ", parts) + ".\n\n" +
                "Replace that install? Every member is written again under the id it already has, with the roles and " +
                "visibility chosen now, so the companions you added keep working; a member not installed yet gets a new id. " +
                "A second copy is not offered: the app uses one installed copy of each member of a pack, so a copy would " +
                "never run.";
        }

        /// <summary>
        /// Install every member of an accepted pack, as <see cref="InstallPack(PackConversionResult, string, ICompanionManager)"/>
        /// does, or REPLACE an earlier install of it: with <paramref name="replace"/> (member key to installed id, from
        /// <see cref="PriorInstall"/>) a member already installed is written again under its own id, so nothing gets a
        /// "-2" copy the host's index would ignore, and only a member new to the pack gets a fresh id. A member the earlier
        /// install had and this pack lacks is left installed, and the outcome names it: removing it is the user's call.
        /// </summary>
        internal static PackInstallOutcome InstallPack(PackConversionResult result, string packTitle, ICompanionManager pets,
            IReadOnlyDictionary<string, string> replace)
        {
            var outcome = new PackInstallOutcome();
            if (result == null || !result.Accepted)
            {
                outcome.Message = "Nothing was installed: the converter did not accept the whole pack.";
                return outcome;
            }
            if (pets == null)
            {
                outcome.Message = "Nothing was installed: no companion service is available (Companion Studio needs the Companions permission).";
                return outcome;
            }

            var taken = new List<string>();
            try
            {
                IReadOnlyList<CompanionTypeInfo> installed = pets.InstalledTypes();
                if (installed != null)
                    foreach (CompanionTypeInfo t in installed)
                        if (t != null && !string.IsNullOrEmpty(t.TypeId)) taken.Add(t.TypeId);
            }
            catch (Exception) { }

            List<PackMemberConversionResult> order = result.Members
                .Where(m => !string.Equals(m.SourceKey, result.MainMember, StringComparison.Ordinal))
                .Concat(result.Members.Where(m => string.Equals(m.SourceKey, result.MainMember, StringComparison.Ordinal)))
                .ToList();
            // A replace keeps every installed member's own id; only a member new to the pack is given one, unique against
            // everything installed (the replaced ids included, which are in the installed list already).
            bool replacing = replace != null && replace.Count > 0;
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            if (replacing)
                foreach (PackMemberConversionResult m in order)
                {
                    string keptId;
                    if (replace.TryGetValue(m.SourceKey, out keptId) && !string.IsNullOrEmpty(keptId)) ids[m.SourceKey] = keptId;
                }
            foreach (KeyValuePair<string, string> fresh in PackInstallIds(packTitle,
                         order.Select(m => m.SourceKey).Where(k => !ids.ContainsKey(k)), taken))
                ids[fresh.Key] = fresh.Value;

            foreach (PackMemberConversionResult member in order)
            {
                string id = ids[member.SourceKey];
                string error;
                bool ok;
                if (member.Disposition != PackMemberDisposition.Prepared || string.IsNullOrEmpty(member.EmittedXml))
                {
                    // Unreachable while Accepted means what it says; stopping here is the honest answer if it ever does not.
                    ok = false;
                    error = "the converter prepared no document for it";
                }
                else
                {
                    try { ok = pets.InstallType(id, member.EmittedXml, out error); }
                    catch (Exception ex) { ok = false; error = ex.Message; }
                }
                if (!ok)
                {
                    outcome.FailedKey = member.SourceKey;
                    outcome.Message = PartialInstallMessage(member.SourceKey, id, error, outcome, result.MainMember, HiddenKeys(result));
                    return outcome;
                }
                outcome.InstalledKeys.Add(member.SourceKey);
                outcome.InstalledIds.Add(id);
            }
            outcome.Complete = true;
            string mainId = ids[result.MainMember];
            if (!replacing)
            {
                outcome.Message = "Installed the pack: " + outcome.InstalledKeys.Count + " member(s). Add '" + result.MainMember +
                    "' (installed as '" + mainId + "') from Add a companion; its other members arrive through it.";
                return outcome;
            }
            outcome.Replaced = true;
            int kept = outcome.InstalledKeys.Count(k => replace.ContainsKey(k));
            string text = "Replaced the installed pack: " + kept + " member(s) written again under the ids they had";
            int added = outcome.InstalledKeys.Count - kept;
            if (added > 0) text += ", " + added + " new member(s) added";
            text += ". '" + result.MainMember + "' is still '" + mainId + "' in Add a companion.";
            var left = replace.Where(p => !ids.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => "'" + p.Key + "' as '" + p.Value + "'").ToList();
            if (left.Count > 0)
                text += " Still installed from before, and no longer in this pack: " + string.Join(", ", left) + ".";
            outcome.Message = text;
            return outcome;
        }

        /// <summary>Every prepared member's document by its exact key: what "Preview pack" hands the host. Pure.</summary>
        internal static Dictionary<string, string> PreviewMembers(PackConversionResult result)
        {
            var members = new Dictionary<string, string>(StringComparer.Ordinal);
            if (result == null) return members;
            foreach (PackMemberConversionResult m in result.Members)
                if (m != null && m.Disposition == PackMemberDisposition.Prepared && !string.IsNullOrEmpty(m.EmittedXml) && !members.ContainsKey(m.SourceKey))
                    members.Add(m.SourceKey, m.EmittedXml);
            return members;
        }

        /// <summary>
        /// "Preview pack": the WHOLE pack through the host's pack preview (ICompanionPackPreview, host 1.10.0), every
        /// prepared member with its exact key and the main member's key, so the main member arrives as its startup says
        /// (an egg) and hatches, transforms and breeds into the other members inside the preview. Only an accepted pack:
        /// a refused one is missing members its references name. Null with the reason in <paramref name="error"/> when the
        /// host refuses, cannot preview a pack, or throws; the caller owns and removes the returned handle, and removing it
        /// removes every companion the preview produced.
        /// </summary>
        internal static ICompanionPreview PreviewPack(PackConversionResult result, ICompanionManager pets, out string error)
        {
            error = null;
            if (result == null || !result.Accepted) { error = "Only a pack the converter accepted whole can be previewed."; return null; }
            ICompanionPackPreview packs = pets as ICompanionPackPreview;
            if (packs == null) { error = "This app cannot preview a whole pack (it needs version 1.10.0 or later)."; return null; }
            try { return packs.SpawnPackPreview(PreviewMembers(result), result.MainMember, out error); }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        /// <summary>The members whose emitted header marks them hidden: the ones Add a companion leaves out. Pure.</summary>
        internal static HashSet<string> HiddenKeys(PackConversionResult result)
        {
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            if (result == null) return hidden;
            foreach (PackMemberConversionResult m in result.Members)
                if (m != null && m.Root != null && m.Root.Header != null && m.Root.Header.Pack != null && m.Root.Header.Pack.Hidden)
                    hidden.Add(m.SourceKey);
            return hidden;
        }

        /// <summary>"Stopped installing the pack at 'B' (as 'p-b'): reason. Already installed: 'A' as 'p-a'. ..." and what
        /// Add a companion lists now: only the visible members already installed, each on its own, since it drops only
        /// hidden members (review item 3: this said the pack "cannot be added yet", which a visible member contradicts).
        /// Pure.</summary>
        internal static string PartialInstallMessage(string failedKey, string failedId, string error, PackInstallOutcome outcome, string mainKey,
            ICollection<string> hiddenKeys)
        {
            string text = "Stopped installing the pack at '" + failedKey + "' (as '" + failedId + "'): " +
                (string.IsNullOrWhiteSpace(error) ? "the host refused it." : error.Trim());
            if (outcome.InstalledKeys.Count == 0) return text + " Nothing was installed.";
            var parts = new List<string>();
            var addable = new List<string>();
            for (int i = 0; i < outcome.InstalledKeys.Count; i++)
            {
                string part = "'" + outcome.InstalledKeys[i] + "' as '" + outcome.InstalledIds[i] + "'";
                parts.Add(part);
                if (hiddenKeys == null || !hiddenKeys.Contains(outcome.InstalledKeys[i])) addable.Add(part);
            }
            text += " Already installed: " + string.Join(", ", parts) + ".";
            if (!outcome.InstalledKeys.Contains(mainKey))
            {
                text += addable.Count == 0
                    ? " The main member was not installed, and every member above is hidden, so Add a companion lists none of them yet."
                    : " The main member was not installed. Add a companion already lists " + string.Join(", ", addable) +
                      (addable.Count == 1 ? " on its own, without the rest of the pack, because it is not hidden."
                                          : ", each on its own, without the rest of the pack, because they are not hidden.");
                text += " Install pack again to finish: it replaces these under the same ids.";
            }
            return text;
        }

        /// <summary>One line per member for the pack report: key, role, verdict, residue count and the first error. Pure.</summary>
        internal static string MemberRow(PackMemberConversionResult member, PackConversionOptions options)
        {
            if (member == null) return "";
            PackMemberOptions chosen = null;
            if (options != null && options.Members != null) options.Members.TryGetValue(member.SourceKey, out chosen);
            string role = chosen != null && chosen.Role != null ? chosen.Role : "form";
            if (chosen != null && chosen.Hidden == true) role += ", hidden";
            int residue = member.Residue == null ? 0 : member.Residue.Total;
            string row = member.SourceKey + "   (" + role + ")   " +
                (member.Disposition == PackMemberDisposition.Prepared ? "✓ prepared" : "✗ refused") +
                "   " + residue + " not converted";
            if (!string.IsNullOrWhiteSpace(member.Error)) row += "   " + member.Error.Trim();
            return row;
        }

        /// <summary>The action the pack report offers after a strict refusal that recovery can lift (decision 64).</summary>
        internal const string RecoveryAction = "Import with recovery";

        /// <summary>
        /// Decision 64: whether the report offers <see cref="RecoveryAction"/>. Studio converts every pack strictly first
        /// (<see cref="SelectPackOptions"/> never selects recovery); only a refusal that names a next entry with no root
        /// behaviour (TICK_NEXT_MISSING) can be lifted, and only the author's click on the action asks for it. A refusal
        /// for a scripted one (TICK_NEXT_MISSING_SCRIPTED) is not lifted by recovery, so on its own it offers nothing. Pure.
        /// </summary>
        internal static bool OffersRecovery(PackConversionResult result, PackConversionOptions options)
        {
            if (result == null || result.Accepted || options == null || options.RecoverMissingNext) return false;
            return result.Errors.Any(e => e.IndexOf("TICK_NEXT_MISSING:", StringComparison.Ordinal) >= 0);
        }

        /// <summary>The options of <paramref name="options"/> with recovery selected, as a copy: the strict options the
        /// author chose stay as they were. Called only from the author's click on <see cref="RecoveryAction"/>.</summary>
        internal static PackConversionOptions WithRecovery(PackConversionOptions options)
        {
            return new PackConversionOptions
            {
                MainMember = options.MainMember,
                Members = options.Members == null ? null : new Dictionary<string, PackMemberOptions>(options.Members, StringComparer.Ordinal),
                Startup = options.Startup,
                IncludeParityMaps = options.IncludeParityMaps,
                RecoverMissingNext = true,
            };
        }

        /// <summary>The line the pack report adds about recovery: what the action does, when it is offered; that the
        /// dropped entries are listed above, when the author chose it; otherwise nothing. Pure.</summary>
        internal static string RecoveryNote(PackConversionResult result, PackConversionOptions options)
        {
            if (OffersRecovery(result, options))
                return "This pack's next lists name behaviours it does not define; the original program stops every companion "
                    + "when one of those entries ends. \u201C" + RecoveryAction + "\u201D converts it again without those entries, "
                    + "and lists each one here before you install.";
            if (result != null && options != null && options.RecoverMissingNext)
                return "Converted with recovery: every dropped next entry is listed above (policy:next-missing-dropped, a "
                    + "difference from the original program). Install pack installs the pack without them.";
            return null;
        }

        /// <summary>The policy tag the converter puts on each next entry recovery dropped (PD-1: one Dropped residue item
        /// per entry, in its member's residue).</summary>
        internal const string RecoveryPolicy = "policy:next-missing-dropped";

        /// <summary>How many next entries recovery dropped across the pack: the members' Dropped residue items that carry
        /// <see cref="RecoveryPolicy"/>. Pure.</summary>
        internal static int DroppedByRecovery(PackConversionResult result)
        {
            if (result == null) return 0;
            return result.Members.Sum(m => m.Residue == null ? 0
                : m.Residue.Dropped.Count(i => i.Detail != null && i.Detail.IndexOf(RecoveryPolicy, StringComparison.Ordinal) >= 0));
        }

        /// <summary>
        /// The start of every status line while a pack is shown: "Pack 'X' accepted. " or "Pack 'X' not accepted: first
        /// error. ", then, when the author chose recovery, that it was converted with recovery and how many next entries
        /// that dropped. The main member's analysis writes its verdict after this prefix, so the recovery stays said
        /// (Round 8: a status written once by the recovery click was overwritten by that verdict two seconds later). Pure.
        /// </summary>
        internal static string StatusPrefix(PackConversionResult result, PackConversionOptions options, string title)
        {
            if (result == null) return "";
            string verdict = result.Accepted
                ? "Pack '" + title + "' accepted. "
                : "Pack '" + title + "' not accepted" + (string.IsNullOrWhiteSpace(result.Error) ? "" : ": " + result.Error.Trim().TrimEnd('.')) + ". ";
            if (options == null || !options.RecoverMissingNext) return verdict;
            return verdict + "Converted with recovery: " + DroppedByRecovery(result) + " dropped next entry(s), each listed in the pack report. ";
        }

        /// <summary>The pack report's summary: the verdict, the pack id when there is one, then the converter's errors and
        /// warnings, each on its own line. Only a pack with a prepared member says one can be opened. Pure.</summary>
        internal static string Summary(PackConversionResult result, string title)
        {
            if (result == null) return "";
            var lines = new List<string>();
            int prepared = result.Members.Count(m => m.Disposition == PackMemberDisposition.Prepared);
            lines.Add(result.Accepted
                ? "✓ Pack '" + title + "': accepted, " + result.Members.Count + " member(s). Install pack installs every one."
                : "✗ Pack '" + title + "': not accepted (" + prepared + " of " + result.Members.Count +
                  " member(s) prepared), so it cannot be installed." + (prepared > 0 ? " A prepared member can still be opened below." : ""));
            if (!string.IsNullOrEmpty(result.PackId)) lines.Add("Pack id: " + result.PackId);
            foreach (string e in result.Errors) lines.Add("✗ " + e);
            foreach (string w in result.Warnings) lines.Add("⚠ " + w);
            return string.Join("\n", lines);
        }
    }
}
