using Microsoft.Win32;

namespace DesktopAICompanion
{
    /// <summary>
    /// Whether Windows is in dark mode. One caller: <c>WpfTheme</c>, resolving the "system" theme setting.
    ///
    /// This was a whole WinForms dark theme -- immersive dark title bars via dwmapi, a recursive control
    /// walk, uxtheme DarkMode_CFD / DarkMode_Explorer calls to darken the parts WinForms does not paint,
    /// and a five-colour palette. Every bit of it served the tray dialogs retired in S5b-3, and after that
    /// only IsDark had a caller: ~150 lines, two P/Invokes and seven colours reachable from nothing.
    /// Deleted 2026-09-27 rather than kept "in case", because WPF does its own theming and none of this
    /// would come back in this shape. It is in git history if the WinForms path is ever revived.
    /// </summary>
    internal static class WindowTheme
    {
        /// <summary>True when Windows apps are set to the dark theme.</summary>
        public static bool IsDark()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return value is int i && i == 0;
            }
            catch { return false; }
        }
    }
}
