using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>The app's stored per-type switches (<see cref="LocalData.GetCompanionSwitch"/>), read afresh on every
    /// question, so a change in the Pets pane reaches live companions at their next decision.</summary>
    internal sealed class LocalDataSwitchStore : ITickSwitchStore
    {
        public static readonly LocalDataSwitchStore Instance = new LocalDataSwitchStore();

        public bool? Get(string typeId, string key)
        {
            LocalData data = Program.MyData;
            return data == null ? null : data.GetCompanionSwitch(typeId, key);
        }
    }

    /// <summary>
    /// The session's record of windows tick companions moved or minimised: what "Put windows back" reads and acts on.
    /// The real one is the desktop's interactive window (<see cref="Win32InteractiveWindow"/>); a self-test gives the
    /// host a fake, so the tray item is exercised without a real window ever being touched.
    /// </summary>
    internal interface ITickWindowRecord
    {
        /// <summary>How many windows were moved or minimised this session and not yet put back.</summary>
        int RecordedCount { get; }

        /// <summary>Every recorded window goes back where it was; returns how many did. The record is emptied.</summary>
        int PutWindowsBack();
    }

    /// <summary>
    /// The tray's "Put windows back" item: shown only while some window a companion moved or minimised this session is
    /// still recorded, and saying, once used, how many it put back.
    /// </summary>
    internal static class TickWindowsTray
    {
        /// <summary>Show the item only when there is something to put back, and say how much.</summary>
        public static void Refresh(ToolStripItem item, TickHost host)
        {
            if (item == null) return;
            int recorded = host == null ? 0 : host.WindowsToPutBack;
            item.Visible = recorded > 0;
            item.Text = recorded == 1 ? "Put the moved &window back" : "Put &windows back (" + recorded + ")";
        }

        /// <summary>The item's action: every recorded window goes back. Returns the sentence the user is shown.</summary>
        public static string PutBack(TickHost host)
        {
            int recorded = host == null ? 0 : host.WindowsToPutBack;
            if (recorded == 0) return "No window needed putting back.";
            return Message(host.PutWindowsBack(), recorded);
        }

        public static string Message(int restored, int recorded)
        {
            if (restored == recorded)
                return restored == 1 ? "Put 1 window back." : "Put " + restored + " windows back.";
            int left = recorded - restored;
            return "Put " + restored + " of " + recorded + " windows back; " + left + (left == 1 ? " was" : " were")
                + " left where " + (left == 1 ? "it is" : "they are")
                + " (closed, maximised, busy, being moved, or run as administrator).";
        }
    }

    /// <summary>
    /// The Pets pane's switches for an installed companion: decided from its compiled tick type
    /// (<see cref="TickSettingRows.For"/>), and none for a classic companion, whose card stays as it was.
    /// </summary>
    internal static class TickPaneRows
    {
        private static readonly Regex TickClock = new Regex(@"<clock>\s*tick\s*</clock>", RegexOptions.CultureInvariant);

        /// <summary>The rows for a companion's animations.xml text: empty for a classic companion or a document that does
        /// not load (the card then offers nothing it could not honour).</summary>
        public static List<TickSettingRow> ForPetXml(string xml)
        {
            // Only a tick-clock document is parsed and compiled; a classic one (most companions) costs one regex.
            if (string.IsNullOrEmpty(xml) || !TickClock.IsMatch(xml)) return new List<TickSettingRow>();
            try
            {
                XmlData.RootNode root;
                string error;
                if (!CompanionXmlValidator.TryParse(xml, out root, out error) || !ShimejiFormatValidator.IsTickMode(root))
                    return new List<TickSettingRow>();
                return TickSettingRows.For(TickType.Compile("", root));
            }
            catch (Exception)
            {
                return new List<TickSettingRow>();
            }
        }
    }
}
