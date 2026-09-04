using System;
using Microsoft.Win32;

namespace DesktopAICompanion
{
        /// <summary>
        /// Make the pet's tray icon visible on Windows 11 instead of buried in the "hidden icons" flyout.
        ///
        /// Windows 11 files every tray icon under HKCU\Control Panel\NotifyIconSettings, keyed by an
        /// undocumented hash of the owning executable, and an icon whose IsPromoted value is ABSENT is
        /// hidden. A first launch therefore reads to the user as "the pet is on screen but there is no tray
        /// icon" -- the icon is registered and working, it is just in the flyout behind the chevron, one of
        /// thirty. There is no API for an app to promote its own icon; this value is the only lever, and the
        /// shell picks a change up live (measured: the icon leaves the flyout within ~3s, with no app
        /// restart, no explorer restart and no elevation -- HKCU only).
        ///
        /// Promote ONLY when the value is absent. Windows writes 0 when the user drags the icon back into
        /// the flyout, so absent means "never expressed a preference" while 0 means "hidden on purpose",
        /// which has to stand or the pet would overrule the user on every launch. That makes this
        /// self-limiting and needs no setting of its own: after promotion the value is 1, not absent, so it
        /// is never rewritten.
        /// </summary>
    internal static class TrayPromotion
    {
        internal const string SettingsSubKey = @"Control Panel\NotifyIconSettings";

        /// <summary>Set once per process, so a pet switch does not re-walk 100 registry keys.</summary>
        static bool attempted;

            /// <summary>
            /// Decide whether the shell's stored preference leaves us free to promote. Pure, so the
            /// absent/hidden/shown cases are testable without touching the real notification area.
            /// </summary>
            /// <param name="storedIsPromoted">The IsPromoted value as read, or null when absent.</param>
        internal static bool ShouldPromote(object storedIsPromoted)
        {
            // Absent -- the user has never moved this icon either way, so promoting is not overruling them.
            if (storedIsPromoted == null) return true;
            // Any explicit value is the user's choice, including a 0 meaning "keep it hidden".
            return false;
        }

            /// <summary>
            /// Match the shell's recorded executable against ours. Deliberately an EXACT comparison: this
            /// machine can hold a dozen keys whose executable is named DesktopAICompanion.exe (portable build,
            /// installed build, test builds), so a suffix or filename match would promote some other copy's
            /// icon. Packaged apps are recorded in a "{KnownFolderGuid}\..." form we cannot resolve here --
            /// those simply do not match, and not promoting is the safe outcome.
            /// </summary>
        internal static bool PathMatches(string recorded, string exePath)
        {
            if (string.IsNullOrEmpty(recorded) || string.IsNullOrEmpty(exePath)) return false;
            return string.Equals(recorded, exePath, StringComparison.OrdinalIgnoreCase);
        }

            /// <summary>
            /// Find this executable's tray entry and promote it if the user has not already chosen. Safe to
            /// call from any thread and safe to call before the shell has created the key: the entry only
            /// appears once Shell_NotifyIcon has taken our NIM_ADD, so this retries briefly rather than
            /// racing it. Never throws; a locked or absent key just means no promotion.
            /// </summary>
            /// <param name="exePath">Full path of the running executable.</param>
            /// <param name="tooltip">Correct label for the icon, written alongside a promotion.</param>
        internal static void PromoteOnce(string exePath, string tooltip)
        {
            if (attempted) return;
            attempted = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // The key is created by the shell when it accepts the icon, which has just happened on
                    // another thread. Six attempts over ~3s covers a slow shell without delaying anything:
                    // this runs on a pool thread and nothing waits on it.
                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        string detail;
                        if (TryPromote(exePath, tooltip, out detail))
                        {
                            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "tray icon: " + detail);
                            return;
                        }
                        if (attempt == 5)
                            // A control that silently does nothing is indistinguishable from one that is not
                            // wired up, so say which of the two happened.
                            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "tray icon not promoted: " + detail);
                        System.Threading.Thread.Sleep(500);
                    }
                }
                catch (Exception) { }
            });
        }

            /// <summary>
            /// One pass over the shell's tray entries. Returns true when the question is settled -- either we
            /// promoted the icon or the user's own choice is already recorded -- and false only when our entry
            /// is not there yet and another attempt is worth making.
            /// </summary>
        internal static bool TryPromote(string exePath, string tooltip, out string detail)
        {
            using (RegistryKey settings = Registry.CurrentUser.OpenSubKey(SettingsSubKey, true))
                return TryPromoteIn(settings, exePath, tooltip, out detail);
        }

            /// <summary>
            /// The walk itself, over a caller-supplied key so a self-test can stage the awkward cases -- a
            /// dozen entries whose executable is also called DesktopAICompanion.exe, an entry the user has hidden on
            /// purpose -- against a throwaway subtree instead of the live notification area.
            /// </summary>
        internal static bool TryPromoteIn(RegistryKey settings, string exePath, string tooltip, out string detail)
        {
            detail = "no notification-area entry for " + exePath;
            {
                if (settings == null)
                {
                    // Pre-Windows-11 shells have no such key and no promotion concept: nothing to do, ever.
                    detail = "this Windows build does not use NotifyIconSettings";
                    return true;
                }
                foreach (string name in settings.GetSubKeyNames())
                {
                    using (RegistryKey entry = settings.OpenSubKey(name, true))
                    {
                        if (entry == null) continue;
                        if (!PathMatches(entry.GetValue("ExecutablePath") as string, exePath)) continue;

                        // Windows caches the tooltip from the FIRST icon it ever accepted from this path, so
                        // an entry created by an older build still carries that build's label -- "eSheep
                        // Desktop Pet" for a pet named Pearl. Corrected independently of the promotion
                        // decision below: the flyout label is how the user finds the icon, and unlike
                        // visibility there is no user preference here to overrule. Converges after one write.
                        bool relabelled = false;
                        if (!string.IsNullOrEmpty(tooltip) &&
                            !string.Equals(entry.GetValue("InitialTooltip") as string, tooltip, StringComparison.Ordinal))
                        {
                            entry.SetValue("InitialTooltip", tooltip, RegistryValueKind.String);
                            relabelled = true;
                        }

                        if (!ShouldPromote(entry.GetValue("IsPromoted")))
                        {
                            detail = "visibility left as the user set it" + (relabelled ? ", label corrected" : "");
                            return true;
                        }
                        entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        detail = "promoted out of the hidden-icons flyout";
                        return true;
                    }
                }
            }
            return false;
        }

            /// <summary>Test seam: let a self-test run the walk more than once in one process.</summary>
        internal static void ResetForTests()
        {
            attempted = false;
        }
    }
}
