using System;

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
            if (!Version.TryParse(NumericPrefix(minHostVersion), out minimum))
            {
                reason = "declares MinHostVersion '" + (minHostVersion ?? "").Trim() +
                    "', which is not a version; the requirement is ignored";
                return true;
            }

            Version current;
            if (!Version.TryParse(NumericPrefix(hostVersion), out current))
            {
                reason = "host version '" + (hostVersion ?? "") +
                    "' is not a version; MinHostVersion " + minimum + " is not enforced";
                return true;
            }

            if (minimum <= current) return true;

            reason = "needs host " + minimum + " or newer (this host is " + current + ")";
            return false;
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
