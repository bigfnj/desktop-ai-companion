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
        private const string ValueName = "Desktop AI Companion";

        /// <summary>
        /// What this value was called before the product was renamed. Windows shows the value NAME in Task
        /// Manager's Startup tab, so it had to change with everything else -- but the old entry does not
        /// clean itself up, and after the rename it points at an executable in the old install directory
        /// that no longer exists. Left alone it is a startup item that silently fails forever, and
        /// IsEnabled would report "off" for a user who had switched it on. So both names are read, and the
        /// legacy one is removed whenever the setting is written.
        /// </summary>
        // Spelled out as a HISTORICAL literal and deliberately not renamed with everything else: this is
        // the name the value actually has on disk from before the rename, so it has to keep matching the
        // old string or the cleanup silently finds nothing. A repo-wide rename pass DID rewrite this once
        // and the mistake is invisible at runtime -- the entry just stays, pointing at an executable that
        // no longer exists.
        private const string LegacyValueName = "DesktopPet AI Edition";

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
    }
}
