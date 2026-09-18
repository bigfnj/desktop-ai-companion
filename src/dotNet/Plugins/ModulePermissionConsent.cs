using System;
using System.Collections.Generic;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// What a module update is asking for that the installed copy did not have.
    ///
    /// WHY THIS EXISTS. `ModulePermissions`' own doc block says: "The Modules pane shows declared
    /// permissions BEFORE a download, and a module that later widens its set re-prompts rather than
    /// widening silently, so the flag is the only place a user can see this coming." That sentence is
    /// the load-bearing justification for the entire permission model -- the model is disclosure, not
    /// containment, so the disclosure has to keep being true after install or it buys nothing.
    ///
    /// It had no implementation. Found by an audit on 2026-09-17: the "wants: ..." line was rendered
    /// only on the pre-install row, the installed row showed a version and three buttons, and neither
    /// the update path nor the background scan compared permission sets. A repo-wide grep for
    /// "re-prompt" hit that comment and nothing else. So an update could have taken a module from
    /// "wants: Speech, Storage" to "wants: Speech, Storage, AgentTranscripts" -- the most sensitive
    /// read in the application -- with no more ceremony than a version bump.
    ///
    /// The comparison is pure and lives here rather than inline in the pane for the reason
    /// ModuleUpdateScan gives for the version rule: a second opinion about what counts as "wider" is
    /// how a prompt and a badge drift apart. Nothing about it needs a window, so it is asserted as a
    /// table in --hardening-selftest.
    /// </summary>
    internal static class ModulePermissionConsent
    {
        /// <summary>
        /// The flags <paramref name="offered"/> declares that <paramref name="installed"/> did not.
        /// `None` when the update asks for nothing new, which is the overwhelmingly common case and
        /// must stay silent -- a prompt on every update would train the user to click through it.
        ///
        /// Deliberately one-directional. A module that DROPS a permission is strictly less alarming
        /// than one that adds one, and prompting about a narrowing would be the same click-through
        /// tax for no information.
        /// </summary>
        internal static ModulePermissions NewlyRequested(ModulePermissions installed, ModulePermissions offered)
        {
            return offered & ~installed;
        }

        /// <summary>
        /// The flags as a comma-separated list of their declared names, for a consent prompt.
        ///
        /// Enumerates the enum rather than calling ToString() on the value: a flags enum renders an
        /// unknown bit as a NUMBER, and a prompt that asks the user to approve "4096" is worse than
        /// no prompt. An unknown bit is possible here -- a catalog written by a newer host -- though
        /// RemoteCatalog.TryParsePermissions drops those names before they reach this far.
        /// </summary>
        internal static string Describe(ModulePermissions flags)
        {
            var parts = new List<string>();
            foreach (ModulePermissions flag in Enum.GetValues(typeof(ModulePermissions)))
                if (flag != ModulePermissions.None && (flags & flag) == flag) parts.Add(flag.ToString());
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "";
        }

        /// <summary>
        /// The prompt body for an update that widens its set. Built here so the sentence is asserted
        /// with the comparison: a correct diff behind a message that does not say what changed is
        /// still a silent widening as far as the reader is concerned.
        /// </summary>
        internal static string PromptText(string displayName, string version, ModulePermissions added)
        {
            string name = string.IsNullOrEmpty(displayName) ? "This module" : displayName;
            string body = name + " v" + (version ?? "?") + " is asking for access it did not have before:"
                          + Environment.NewLine + Environment.NewLine
                          + "    " + Describe(added)
                          + Environment.NewLine + Environment.NewLine
                          + "Nothing in the app enforces these, so they are a statement of what the module"
                          + " does rather than a restriction on what it can do. Update anyway?";
            return body;
        }
    }
}
