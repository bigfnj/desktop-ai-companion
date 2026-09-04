using System;
using System.Diagnostics;

namespace DesktopAICompanion
{
    /// <summary>
    /// Central, security-reviewed helpers for opening external links from the UI. Relocated from the retired
    /// WinForms <c>AboutBox</c>/<c>FormHelp</c> so the WPF About/Help windows (and the security self-test)
    /// share one validator. Two open policies: <see cref="TryOpen"/> allows any well-formed HTTPS URL (the
    /// About window's fixed links + a pet's own <c>[link:…]</c> markup), while <see cref="TryOpenProjectDoc"/>
    /// additionally enforces the Help window's github.com/bigfnj/desktop-ai-companion documentation allowlist. Both are
    /// fully defensive: a rejected URL or an unavailable browser is swallowed so nothing affects the pet runtime.
    /// </summary>
    internal static class WebLinks
    {
        /// <summary>
        /// HTTPS + non-empty host + no userinfo + at most 2048 characters. This is a security invariant —
        /// copied verbatim from the former <c>AboutBox.TryNormalizeHttpsLink</c>; the security self-test
        /// (<c>CheckAboutLinkPolicy</c>) asserts these exact rules. Do not relax it.
        /// </summary>
        internal static bool TryNormalizeHttpsLink(
            string value,
            out string normalized)
        {
            normalized = null;
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > 2048 ||
                !Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo))
                return false;
            normalized = uri.AbsoluteUri;
            return true;
        }

        /// <summary>
        /// Open an arbitrary HTTPS link in the default browser after normalizing it. A rejected URL or an
        /// unavailable browser is swallowed so nothing affects the pet runtime.
        /// </summary>
        internal static void TryOpen(string value)
        {
            try
            {
                string normalized;
                if (!TryNormalizeHttpsLink(value, out normalized))
                    return;

                using (Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = normalized,
                    UseShellExecute = true
                }))
                {
                }
            }
            catch
            {
                // An unavailable browser or rejected URL must not affect the pet runtime.
            }
        }

        /// <summary>
        /// Open one of this project's HTTPS documentation pages in the default browser. Beyond the HTTPS
        /// invariant in <see cref="TryNormalizeHttpsLink"/> this enforces the Help window's allowlist: the
        /// host must be github.com and the path must live under /bigfnj/desktop-ai-companion. Anything else (or a
        /// browser failure) is swallowed.
        /// </summary>
        internal static void TryOpenProjectDoc(string value)
        {
            try
            {
                string normalized;
                if (!TryNormalizeHttpsLink(value, out normalized))
                    return;

                Uri uri;
                if (!Uri.TryCreate(normalized, UriKind.Absolute, out uri) ||
                    !string.Equals(
                        uri.Host,
                        "github.com",
                        StringComparison.OrdinalIgnoreCase) ||
                    !uri.AbsolutePath.StartsWith(
                        "/bigfnj/desktop-ai-companion",
                        StringComparison.OrdinalIgnoreCase))
                    return;

                using (Process process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true
                }))
                {
                }
            }
            catch
            {
                // A rejected URL or an unavailable browser must not affect the pet runtime.
            }
        }
    }
}
