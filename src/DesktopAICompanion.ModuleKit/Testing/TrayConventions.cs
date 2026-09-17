using System;
using System.Collections.Generic;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.ModuleKit.Testing
{
    /// <summary>
    /// Assertions for the project conventions a module's tray contribution has to keep.
    ///
    /// Lifted out of <c>BlinkingLedModule</c>, which was the only module asserting any of this while all six
    /// share one notification-area menu with the host. A convention one module checks is not a convention.
    /// </summary>
    public static class TrayConventions
    {
        /// <summary>
        /// Every tray entry carries its own icon, and no two entries share one.
        ///
        /// Both halves matter, and for different reasons. An icon-less row reads as a rendering bug beside
        /// its neighbours, because the tray is shared by the host and six modules and everything else in it
        /// has a glyph. Two rows with the SAME glyph are worse than one row with none: they look like
        /// duplicates of each other, so a user cannot tell which one they already clicked.
        ///
        /// An empty collection passes. Not every module contributes a tray entry, and a module that
        /// contributes none has kept this convention rather than skipped it.
        ///
        /// Returns a reason rather than a bare bool, because the caller is a self-test whose output a
        /// module author reads: "every tray entry has an icon: FAIL" does not say which row, and with six
        /// modules in one menu that is the only part worth printing. Icons are compared by their bytes,
        /// which is what a duplicate actually is -- two rows can legitimately be built from the same
        /// embedded resource NAME in different modules, and this check is per module.
        /// </summary>
        /// <param name="items">The tray entries a module contributed, e.g. <c>RecordingHost.TrayItems</c>.</param>
        /// <param name="reason">Empty when the convention holds; otherwise which entry broke it and how.</param>
        public static bool EveryTrayEntryHasAUniqueIcon(IEnumerable<TrayItem> items, out string reason)
        {
            reason = "";
            if (items == null)
            {
                reason = "the tray item collection is null";
                return false;
            }

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            int index = 0;
            foreach (TrayItem item in items)
            {
                string where = "tray entry " + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (item == null)
                {
                    reason = where + " is null";
                    return false;
                }
                where += " ('" + (item.Label ?? "<no label>") + "')";
                if (item.IconPng == null || item.IconPng.Length == 0)
                {
                    reason = where + " has no icon";
                    return false;
                }

                string key = Convert.ToBase64String(item.IconPng);
                int first;
                if (seen.TryGetValue(key, out first))
                {
                    reason = where + " reuses the icon already used by tray entry " +
                             first.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return false;
                }
                seen.Add(key, index);
                index++;
            }
            return true;
        }

        /// <summary>
        /// <see cref="EveryTrayEntryHasAUniqueIcon(IEnumerable{TrayItem}, out string)"/> without the reason,
        /// for a caller that only wants the verdict.
        /// </summary>
        public static bool EveryTrayEntryHasAUniqueIcon(IEnumerable<TrayItem> items)
        {
            string ignored;
            return EveryTrayEntryHasAUniqueIcon(items, out ignored);
        }

        /// <summary>
        /// Records the convention on a <see cref="SelfTestProbe"/>, naming the offending entry when it
        /// fails. This is the form every module's self-test should call: one line, and the failure says
        /// which row.
        /// </summary>
        public static bool CheckTrayIcons(SelfTestProbe probe, IEnumerable<TrayItem> items)
        {
            if (probe == null) throw new ArgumentNullException("probe");
            string reason;
            bool ok = EveryTrayEntryHasAUniqueIcon(items, out reason);
            return probe.Check(
                ok
                    ? "every tray entry has its own unique icon"
                    : "every tray entry has its own unique icon -- " + reason,
                ok);
        }
    }
}
