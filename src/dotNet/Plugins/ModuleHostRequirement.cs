using System;
using System.Globalization;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// The load-time <c>ModuleInfo.MinHostVersion</c> gate.
    ///
    /// Every module has always declared a minimum host version and the host has never read it. That was
    /// harmless while the host kept shipping; it stops being harmless the moment the host is FINAL, because
    /// this is the only mechanism by which a module built against a newer contract can be turned away
    /// cleanly. Without it, such a module loads and then fails at its first call to a member that does not
    /// exist -- a MissingMethodException from inside a module's Init, which the loader logs as a mystery.
    /// With it, the user gets "needs host 1.6.0 or newer (this host is 1.5.0)".
    ///
    /// PERMISSIVE by construction: only a requirement that BOTH sides can express is ever enforced, so this
    /// gate can refuse a module for exactly one reason -- the module said it needs a newer host than this one.
    /// Anything ambiguous loads, because silently disabling a working module over an unparseable string is a
    /// far worse failure than letting it run.
    ///
    /// Static and pure so --module-host-selftest can assert the whole rule table directly, with no
    /// too-new module DLL on disk. Same testability trick as ModuleUpdateScan.
    /// </summary>
    internal static class ModuleHostRequirement
    {
        /// <summary>
        /// True when a module declaring <paramref name="minHostVersion"/> may load on a host reporting
        /// <paramref name="hostVersion"/>. <paramref name="reason"/> carries a log-ready explanation, or ""
        /// when there is nothing worth saying.
        ///
        /// - no requirement                 -> load (every module shipped so far predates this gate)
        /// - unparseable requirement        -> load + note (an author's typo must not disable their module)
        /// - unparseable/absent host version-> load + note (refusing everything because the host could not
        ///                                     describe itself would be a self-inflicted outage)
        /// - requirement &gt; host          -> REFUSE, with the reason
        /// - requirement &lt;= host         -> load
        /// </summary>
        internal static bool IsSatisfied(string hostVersion, string minHostVersion, out string reason)
        {
            reason = "";

            // "declares nothing" and "declares something malformed" are different situations and must not
            // collapse into each other: the first is every module shipped before this gate existed and
            // deserves silence, the second is an author's typo that would otherwise be invisible forever.
            if (string.IsNullOrWhiteSpace(minHostVersion)) return true;

            Version minimum;
            if (!TryParseVersion(minHostVersion, out minimum))
            {
                reason = "declares MinHostVersion '" + (minHostVersion ?? "").Trim() +
                    "', which is not a version; the requirement is ignored";
                return true;
            }

            Version current;
            if (!TryParseVersion(hostVersion, out current))
            {
                reason = "host version '" + (hostVersion ?? "") +
                    "' is not a version; MinHostVersion " + Display(minimum) + " is not enforced";
                return true;
            }

            if (minimum <= current) return true;

            reason = "needs host " + Display(minimum) + " or newer (this host is " + Display(current) + ")";
            return false;
        }

        /// <summary>
        /// One to four dot-separated numbers, with the parts left out read as 0 (N-audit-1106-238). System.Version keeps
        /// the parts it was given and orders an absent part BELOW 0, so 1.11.0.0 compared greater than 1.11.0 and a module
        /// declaring the four-part form was refused by exactly the host it named; and Version.TryParse refuses a single
        /// part, so a declared "2" was read as a typo and its requirement ignored. Padding every value to four parts
        /// makes the comparison one of numbers, which is what a version requirement means. Anything else (an empty
        /// part, more than four, a part too large for an int) is still not a version.
        /// </summary>
        private static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            string prefix = NumericPrefix(text);
            if (prefix.Length == 0) return false;
            string[] parts = prefix.Split('.');
            if (parts.Length > 4) return false;
            var numbers = new int[4];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
            version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
            return true;
        }

        /// <summary>A padded version as people write it: three parts, and the fourth only when it is not 0.</summary>
        private static string Display(Version version)
        {
            return version.ToString(version.Revision > 0 ? 4 : 3);
        }

        /// <summary>
        /// The leading digits-and-dots of a version string, so a semver tag still compares instead of
        /// falling through the unparseable-is-permissive door: "1.6.0-beta" and "1.6.0+abc" both read as
        /// 1.6.0. Returns "" for null/blank or anything that does not start with a digit.
        /// </summary>
        private static string NumericPrefix(string version)
        {
            string text = (version ?? "").Trim();
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if (c != '.' && (c < '0' || c > '9'))
                    return text.Substring(0, index);
            }
            return text;
        }
    }
}
