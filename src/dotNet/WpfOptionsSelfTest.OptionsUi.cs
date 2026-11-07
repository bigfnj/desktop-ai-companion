using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion
{
    internal static partial class WpfOptionsSelfTest
    {
        /// <summary>
        /// N-audit-1106-033: after a successful Apply on a pane that is not rebuilt (no Info or Header field), the view
        /// measured edits against its PRE-Apply baseline, so a field changed back to what it was before the Apply left
        /// Apply grey, and that value could never be saved. Same for a deferred list box ticked back. Driven through the
        /// window's own Apply on a laid-out window, and the module's Save and SetChecked record what reached it.
        /// </summary>
        private static bool ApplyRebasesTheBaseline(StringBuilder sb)
        {
            bool ok = true;
            IReadOnlyDictionary<string, string> lastSave = null;
            var sent = new List<string>();
            var pane = new OptionsPane
            {
                Title = "Rebase",
                Schema = new[] { new SettingField { Id = "note", Label = "Note", Kind = SettingKind.Text, Group = "Rebase" } },
                Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal) { { "note", "first" } }; },
                Save = delegate(IReadOnlyDictionary<string, string> values) { lastSave = values; return true; },
                Lists = new[]
                {
                    new ListCard
                    {
                        Title = "Deferred",
                        DeferChanges = true,
                        SetChecked = delegate(string id, bool on) { sent.Add(id + "=" + (on ? "on" : "off")); },
                        LoadItems = delegate { return new[] { new ListItem { Id = "a", Label = "Item a", Checked = false } }; },
                    },
                },
            };
            var probe = new LaidOutWindow(pane, ElsewherePane());
            System.Windows.Controls.TextBox note = probe.TextRow(0);
            System.Windows.Controls.CheckBox item = BoxTagged(probe, "a");
            if (note == null || item == null) return Check(sb, "options-ui rebase: the pane rendered its text row and its deferred box", false);

            note.Text = "second";
            probe.Apply();
            string saved;
            ok &= Check(sb, "options-ui rebase: WITNESS Apply saved the edit and greyed Apply",
                lastSave != null && lastSave.TryGetValue("note", out saved) && saved == "second" && !probe.Window.IsDirty);
            note.Text = "first";
            ok &= Check(sb, "options-ui rebase: a field changed back after Apply to its pre-Apply value lights Apply", probe.Window.IsDirty);
            probe.Apply();
            ok &= Check(sb, "options-ui rebase: ...and Apply saves that value",
                lastSave != null && lastSave.TryGetValue("note", out saved) && saved == "first");

            item.IsChecked = true;
            probe.Apply();
            ok &= Check(sb, "options-ui rebase: WITNESS Apply sent the staged tick (" + string.Join(", ", sent) + ")",
                sent.Count == 1 && sent[0] == "a=on" && !probe.Window.IsDirty);
            item.IsChecked = false;
            ok &= Check(sb, "options-ui rebase: a deferred box ticked back after Apply lights Apply", probe.Window.IsDirty);
            probe.Apply();
            ok &= Check(sb, "options-ui rebase: ...and Apply sends the untick (" + string.Join(", ", sent) + ")",
                sent.Count == 2 && sent[1] == "a=off");
            return ok;
        }

        /// <summary>
        /// N-audit-1106-260: Preferences shows the volume rounded to tenths and Apply posts every field, so a stored
        /// volume off the 0.1 grid was rewritten by any unrelated Apply. Driven through the pane's own Load and Save on a
        /// scratch settings file; only the volume key is posted, so the Save touches nothing outside the scratch store
        /// (no Run key: that write happens only when runAtStartup is posted).
        /// </summary>
        private static bool PreferencesKeepsAnOffGridVolume(StringBuilder sb)
        {
            bool ok = true;
            LocalData previous = Program.MyData;
            string scratch = Path.Combine(Path.GetTempPath(), "dp-volume-grid-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(scratch);
                Program.MyData = new LocalData(new AppSettingsStore(Path.Combine(scratch, "settings.json"), null));
                Program.MyData.SetVolume(0.37);
                OptionsPane prefs = DesktopAICompanion.Wpf.OptionsShell.BuildPreferencesPane();
                IReadOnlyDictionary<string, string> shown = prefs.Load();
                string tenth;
                bool showsRounded = shown != null && shown.TryGetValue("volume", out tenth) && tenth == "4";
                ok &= Check(sb, "options-ui volume: WITNESS the pane shows a 0.37 volume as the tenth 4", showsRounded);
                if (!showsRounded) return ok;

                bool savedUntouched = prefs.Save(new Dictionary<string, string>(StringComparer.Ordinal) { { "volume", "4" } });
                float kept = Program.MyData.GetVolume();
                ok &= Check(sb, "options-ui volume: an Apply that leaves the volume field alone keeps a stored volume off the 0.1 grid (now "
                    + kept.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")",
                    savedUntouched && Math.Abs(kept - 0.37f) < 0.0001f);

                bool savedChanged = prefs.Save(new Dictionary<string, string>(StringComparer.Ordinal) { { "volume", "6" } });
                ok &= Check(sb, "options-ui volume: WITNESS a changed volume field is written",
                    savedChanged && Math.Abs(Program.MyData.GetVolume() - 0.6f) < 0.0001f);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("FAIL: options-ui volume threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Program.MyData = previous;
                try { Directory.Delete(scratch, true); } catch { }
            }
            return ok;
        }
    }
}
