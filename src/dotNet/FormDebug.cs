using DesktopAICompanion.Tools;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopAICompanion
{
	/// <summary>
	/// Debug form. If you start the application pressing the SHIFT-key a debug window will be started.<br />
	/// With this window, you can see what is happening to your pet.
	/// </summary>
	public partial class FormDebug : Form
    {
		/// <summary>
		/// The most rows the list keeps (F273). A multi-pet session logs 5 to 12 lines a second for as long
		/// as it runs, and with no cap the list grew for the whole session; the oldest rows go first.
		/// </summary>
		internal const int MaxRows = 5000;

		/// <summary>How many rows the list holds; read by the self-test.</summary>
		internal int RowCount { get { return listView1.Items.Count; } }

		private bool addingAnimationsLog;
		private bool addingSpawnLog;
		private bool addingChildLog;
		private bool playingNewAnimation;

		/// <summary>
		/// Constructor of this form.
		/// </summary>
		public FormDebug()
        {
            InitializeComponent();
        }

            /// <summary>
            /// Add a debug information line to the window.
            /// </summary>
            /// <param name="type">Line type: info, warning or error.</param>
            /// <param name="text">Text to display in the window.</param>
        public void AddDebugInfo(StartUp.DEBUG_TYPE type, string text)
        {
			if (IsDisposed || Disposing || listView1 == null || listView1.IsDisposed) return;
			text = text ?? "";

			bool sameCoalescingGroup =
				(addingAnimationsLog && text.StartsWith("adding animation", StringComparison.Ordinal)) ||
				(addingSpawnLog && text.StartsWith("adding spawn", StringComparison.Ordinal)) ||
				(addingChildLog && text.StartsWith("adding child", StringComparison.Ordinal));
			if (sameCoalescingGroup && TryAppendCoalesced(text)) return;

			bool appendAnimationDetail = playingNewAnimation;
			ResetCoalescingState();
			if (appendAnimationDetail)
			{
				if (TryAppendAnimationDetail(text)) return;
			}

			bool visible =
				(type == StartUp.DEBUG_TYPE.info && checkBox1.Checked) ||
				(type == StartUp.DEBUG_TYPE.warning && checkBox2.Checked) ||
				(type == StartUp.DEBUG_TYPE.error && checkBox3.Checked);
			if (!visible) return;

			var item = new ListViewItem(DateTime.Now.ToLongTimeString());
			item.ForeColor =
				type == StartUp.DEBUG_TYPE.warning ? Color.Yellow :
				type == StartUp.DEBUG_TYPE.error ? Color.Salmon :
				Color.White;
			item.SubItems.Add(text);
			listView1.Items.Add(item);
			TrimRows();

			addingAnimationsLog =
				text.StartsWith("adding animation", StringComparison.Ordinal);
			addingSpawnLog = text.StartsWith("adding spawn", StringComparison.Ordinal);
			addingChildLog = text.StartsWith("adding child", StringComparison.Ordinal);
			playingNewAnimation =
				text.StartsWith("new animation", StringComparison.Ordinal);
			if (checkBox4.Checked) item.EnsureVisible();
        }

		private bool TryAppendCoalesced(string text)
		{
			if (listView1.Items.Count == 0) return false;
			ListViewItem item = listView1.Items[listView1.Items.Count - 1];
			if (item.SubItems.Count < 2) return false;

			if (item.SubItems[1].Text.Length > 64)
			{
				if (!checkBox1.Checked) return false;
				var continuation = new ListViewItem(DateTime.Now.ToLongTimeString())
				{
					ForeColor = Color.White
				};
				continuation.SubItems.Add(text);
				listView1.Items.Add(continuation);
				TrimRows();
				if (checkBox4.Checked) continuation.EnsureVisible();
				return true;
			}

			int separator = text.IndexOf(':');
			string suffix = separator >= 0 && separator + 1 < text.Length
				? text.Substring(separator + 1)
				: text;
			item.SubItems[1].Text += "," + suffix;
			if (checkBox4.Checked) item.EnsureVisible();
			return true;
		}

		private bool TryAppendAnimationDetail(string text)
		{
			if (listView1.Items.Count == 0) return false;
			ListViewItem item = listView1.Items[listView1.Items.Count - 1];
			if (item.SubItems.Count < 2) return false;
			item.SubItems[1].Text += " - " + text;
			if (checkBox4.Checked) item.EnsureVisible();
			return true;
		}

		private void ResetCoalescingState()
		{
			addingAnimationsLog = false;
			addingSpawnLog = false;
			addingChildLog = false;
			playingNewAnimation = false;
		}

		/// <summary>Drop the oldest rows past <see cref="MaxRows"/> (F273). The coalescing state looks only
		/// at the LAST row, so trimming from the front never disturbs it.</summary>
		private void TrimRows()
		{
			while (listView1.Items.Count > MaxRows)
				listView1.Items.RemoveAt(0);
		}

		private void convertoToDOTToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (Animations.Xml != null)
				OpenText("animations-dot", XmlToDot.ProcessXml(Animations.Xml.AnimationXML));
		}

		private void openXMLToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (Animations.Xml != null)
				OpenText("animations-xml", Animations.Xml.AnimationXMLString);
		}

		/// <summary>
		/// Show a text in the user's editor (F274). The old handoff started notepad.exe and pushed the text
		/// into its edit control with WM_SETTEXT. On Windows 11 notepad.exe is a launcher stub for the Store
		/// app, so the window it found belonged to the stub or to nobody, the text went nowhere and the
		/// failure was swallowed; and an unguarded MainWindowHandle could retitle a foreign window in a race.
		/// The text goes to a file under %TEMP% and the file is opened through the shell, the one handoff
		/// that works on every Windows; a failure is logged in this window instead of swallowed.
		/// </summary>
		private static void OpenText(string kind, string text)
		{
			string path = null;
			try
			{
				path = WriteDebugText(kind, text);
				using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
			}
			catch (Exception ex)
			{
				StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error,
					"could not open " + kind + (path == null ? "" : " (" + path + ")") + ": " + ex.Message);
			}
		}

		/// <summary>Write a debug text to one fixed file per kind under %TEMP% and return its path (F274).
		/// Overwritten on every open, so the debug window leaves nothing to accumulate.</summary>
		internal static string WriteDebugText(string kind, string text)
		{
			var safe = new System.Text.StringBuilder();
			foreach (char c in kind ?? "")
				safe.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
			string path = System.IO.Path.Combine(
				System.IO.Path.GetTempPath(),
				"dp-debug-" + (safe.Length == 0 ? "text" : safe.ToString()) + ".txt");
			System.IO.File.WriteAllText(path, text ?? "", new System.Text.UTF8Encoding(false));
			return path;
		}

		private void clearWindowToolStripMenuItem_Click(object sender, EventArgs e)
		{
			ResetCoalescingState();
			listView1.Items.Clear();
		}

		private void removeInfosToolStripMenuItem_Click(object sender, EventArgs e)
		{
			ResetCoalescingState();
			for (int index = listView1.Items.Count - 1; index >= 0; index--)
				if (listView1.Items[index].ForeColor == Color.White)
					listView1.Items.RemoveAt(index);
		}
	}
}
