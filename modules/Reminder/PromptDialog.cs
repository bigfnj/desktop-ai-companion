using System;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// A minimal one-line input dialog (WinForms ships no InputBox for C#). Shown from a pane action, which the
    /// host runs on the UI thread, so ShowDialog is safe here. Returns false on Cancel.
    /// </summary>
    internal static class PromptDialog
    {
        public static bool Show(string title, string prompt, string initial, out string result)
        {
            result = "";
            using (var form = new Form())
            using (var label = new Label())
            using (var box = new TextBox())
            using (var ok = new Button())
            using (var cancel = new Button())
            {
                form.Text = title ?? "";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(480, 150);

                label.SetBounds(12, 12, 456, 64);
                label.AutoSize = false;
                label.Text = prompt ?? "";

                box.SetBounds(12, 82, 456, 24);
                box.Text = initial ?? "";

                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(300, 114, 78, 26);
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(388, 114, 80, 26);

                form.Controls.Add(label);
                form.Controls.Add(box);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                if (form.ShowDialog() == DialogResult.OK)
                {
                    result = box.Text ?? "";
                    return true;
                }
                return false;
            }
        }
    }
}
