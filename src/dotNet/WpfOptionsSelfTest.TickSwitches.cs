using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    internal static partial class WpfOptionsSelfTest
    {
        /// <summary>
        /// A tick companion's switches in the Pets pane (lane feature/shimeji-host-settings): a classic companion's card
        /// has none; a tick companion's card has one checkbox per row its type uses, each showing the stored choice (else
        /// the row's default) and writing the user's change to the settings, keyed by the type's id. The settings are a
        /// scratch file; the rows are handed to the card through the pane's per-companion cache, because the gallery reads
        /// installed companions and this self-test installs nothing.
        /// </summary>
        private static bool TickSwitchRows(StringBuilder sb)
        {
            bool ok = true;
            LocalData previous = Program.MyData;
            string scratch = Path.Combine(Path.GetTempPath(), "dp-tick-switch-rows-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            const string id = "tick-switch-selftest";
            try
            {
                Directory.CreateDirectory(scratch);
                Program.MyData = new LocalData(new AppSettingsStore(Path.Combine(scratch, "settings.json"), null));
                Program.MyData.SetCompanionSwitch(id, TickTypeSwitches.Breeding, false);   // a choice made before the pane opened

                var pane = new DesktopAICompanion.Wpf.CompanionsPaneControl();
                var grid = (Panel)typeof(DesktopAICompanion.Wpf.CompanionsPaneControl)
                    .GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pane);
                int cards = grid.Children.Count;
                ok &= Check(sb, "tick switches: the gallery of classic companions shows no switch (" + cards + " cards)",
                    cards > 0 && CheckBoxesIn(grid).Count == 0);

                var rows = new List<TickSettingRow>
                {
                    new TickSettingRow { Key = TickTypeSwitches.Breeding, Label = "Breeding", DefaultOn = true },
                    new TickSettingRow { Key = TickTypeSwitches.WindowActions, Label = "Let it move windows", DefaultOn = false },
                    new TickSettingRow { Key = "toggle:Standing", Label = "Stands about", DefaultOn = false },
                };
                ok &= Check(sb, "tick switches: no rows build no switch block, so a classic card is unchanged",
                    pane.BuildTickSwitchRows(id, "Tick Pet", null) == null
                    && pane.BuildTickSwitchRows(id, "Tick Pet", new List<TickSettingRow>()) == null);

                // The card of a tick companion whose type uses these three switches.
                Type paneType = typeof(DesktopAICompanion.Wpf.CompanionsPaneControl);
                Type statsType = paneType.GetNestedType("CompanionStats", BindingFlags.NonPublic);
                object stats = Activator.CreateInstance(statsType, true);
                statsType.GetField("TickRows").SetValue(stats, rows);
                var cache = (IDictionary)paneType.GetField("_statsCache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                lock (cache) cache[id] = stats;
                FrameworkElement card;
                try
                {
                    card = (FrameworkElement)paneType.GetMethod("BuildCard", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(pane,
                        new object[] { new DesktopAICompanion.Options.CompanionRow { Id = id, DisplayName = "Tick Pet" }, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) });
                }
                finally { DesktopAICompanion.Wpf.CompanionsPaneControl.ForgetStats(id); }
                List<CheckBox> boxes = CheckBoxesIn(card);
                var labels = new List<string>();
                foreach (CheckBox b in boxes) labels.Add(((TextBlock)b.Content).Text + "=" + b.IsChecked);
                string seen = string.Join(", ", labels);
                ok &= Check(sb, "tick switches: a tick companion's card has one switch per row, showing the stored choice, else the default (" + seen + ")",
                    seen == "Breeding=False, Let it move windows=False, Stands about=False");

                // The user's change goes to the settings under the type's id, and the box follows the store.
                if (boxes.Count == 3)
                {
                    boxes[1].IsChecked = true;
                    bool windowsOn = Program.MyData.GetCompanionSwitch(id, TickTypeSwitches.WindowActions) == true;
                    boxes[0].IsChecked = true;
                    bool breedingOn = Program.MyData.GetCompanionSwitch(id, TickTypeSwitches.Breeding) == true;
                    boxes[1].IsChecked = false;
                    bool windowsOff = Program.MyData.GetCompanionSwitch(id, TickTypeSwitches.WindowActions) == false;
                    ok &= Check(sb, "tick switches: ticking and clearing a switch stores the choice for the type",
                        windowsOn && breedingOn && windowsOff && boxes[1].IsChecked == false && boxes[0].IsChecked == true
                        && Program.MyData.GetCompanionSwitch(id, "toggle:Standing") == null);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("FAIL: tick switches threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Program.MyData = previous;
                try { Directory.Delete(scratch, true); } catch { }
            }
            return ok;
        }

        /// <summary>Every CheckBox under an element, through the logical tree (a card that was never laid out has no
        /// visual tree yet).</summary>
        private static List<CheckBox> CheckBoxesIn(DependencyObject root)
        {
            var found = new List<CheckBox>();
            if (root == null) return found;
            var box = root as CheckBox;
            if (box != null) found.Add(box);
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var d = child as DependencyObject;
                if (d != null) found.AddRange(CheckBoxesIn(d));
            }
            return found;
        }
    }
}
