using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopAICompanion
{
    /// <summary>
    /// Per-user "run at startup" registration via the HKCU Run key. Best-effort: registration must
    /// never throw into the UI. Extracted from FormOptions so the renderer-agnostic Options controller
    /// can drive it too. For self-tests, the DESKTOP_AI_COMPANION_STARTUP_TEST_KEY environment variable redirects
    /// to a throwaway subkey, so a test never rewrites or deletes the user's real startup entry.
    /// </summary>
    internal static class StartupRegistration
    {
        private const string RealKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // Internal, not private: CoreTests compiles this file and seeds the redirected key by these names,
        // so a rename here cannot leave the test seeding a value the code no longer reads.
        internal const string ValueName = "Desktop AI Companion";

        /// <summary>
        /// What this value was called before the product was renamed. Windows shows the value NAME in Task
        /// Manager's Startup tab, so it had to change with everything else -- but the old entry does not
        /// clean itself up, and after the rename it points at an executable in the old install directory
        /// that no longer exists. Left alone it is a startup item that silently fails forever, and
        /// IsEnabled would report "off" for a user who had switched it on. So both names are read, the
        /// legacy one is removed whenever the setting is written, and <see cref="MigrateLegacy"/> moves it
        /// onto the current name once, at launch.
        /// </summary>
        // Spelled out as a HISTORICAL literal and deliberately not renamed with everything else: this is
        // the name the value actually has on disk from before the rename, so it has to keep matching the
        // old string or the cleanup silently finds nothing. A repo-wide rename pass DID rewrite this once
        // and the mistake is invisible at runtime -- the entry just stays, pointing at an executable that
        // no longer exists.
        internal const string LegacyValueName = "DesktopPet AI Edition";

        private static string KeyPath
        {
            get
            {
                string redirect = Environment.GetEnvironmentVariable("DESKTOP_AI_COMPANION_STARTUP_TEST_KEY");
                return string.IsNullOrWhiteSpace(redirect) ? RealKeyPath : redirect;
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                    return key != null &&
                        (key.GetValue(ValueName) != null || key.GetValue(LegacyValueName) != null);
            }
            catch { return false; }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true)
                                         ?? Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null) return;
                    // Either way the legacy entry goes: enabling replaces it with the current name and the
                    // current executable path, disabling must not leave the old one still starting the app.
                    if (key.GetValue(LegacyValueName) != null)
                        key.DeleteValue(LegacyValueName, false);
                    if (enabled)
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else if (key.GetValue(ValueName) != null)
                        key.DeleteValue(ValueName, false);
                }
            }
            catch { /* startup registration is best-effort */ }
        }

        /// <summary>
        /// Move a pre-rename Run entry onto the current name and this executable, at launch (RA-267). The
        /// rewrite in <see cref="Set"/> ran only when the user pressed Apply on Preferences, and a user who
        /// upgraded with autostart on never had a reason to: the pane already showed the box ticked (IsEnabled
        /// reads both names), while the entry Windows ran at logon pointed at the uninstalled product's
        /// directory and started nothing. Idempotent and one registry round trip when there is nothing to
        /// do: true only when a legacy value was found and moved, so the caller can log exactly that. An
        /// existing current-name value wins over the legacy one (the user has since saved Preferences), and
        /// the legacy value goes either way. Best-effort like everything else here.
        /// </summary>
        internal static bool MigrateLegacy()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (key == null || key.GetValue(LegacyValueName) == null) return false;
                    key.DeleteValue(LegacyValueName, false);
                    if (key.GetValue(ValueName) == null)
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>What <see cref="Remove"/> found: nothing to remove, an entry removed, or a registry
        /// failure (which the factory reset counts as an error, the way a file it could not delete is).</summary>
        internal enum RemovalOutcome { Nothing, Removed, Failed }

        /// <summary>
        /// Remove the Run value this app wrote, under either name, for the factory reset (RA-232). The
        /// installer's "Clear all settings and modules" promises a first launch, and a first launch does not
        /// autostart; the Preferences reset already cleared this value, so the two reset surfaces agreed
        /// only after this. <paramref name="detail"/> is the line the reset prints for it.
        /// </summary>
        internal static RemovalOutcome Remove(out string detail)
        {
            detail = "no run-at-startup entry";
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (key == null) return RemovalOutcome.Nothing;
                    bool current = key.GetValue(ValueName) != null;
                    bool legacy = key.GetValue(LegacyValueName) != null;
                    if (!current && !legacy) return RemovalOutcome.Nothing;
                    if (current) key.DeleteValue(ValueName, false);
                    if (legacy) key.DeleteValue(LegacyValueName, false);
                    detail = "run-at-startup entry removed" + (legacy ? " (the pre-rename name too)" : "");
                    return RemovalOutcome.Removed;
                }
            }
            catch (Exception ex)
            {
                detail = "run-at-startup entry could not be removed: " + ex.Message;
                return RemovalOutcome.Failed;
            }
        }
    }
}
