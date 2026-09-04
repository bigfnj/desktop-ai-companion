using DesktopAICompanion.Tools;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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
        /// FindWindowEx is used to open another application
        /// </summary>
        /// <param name="hwndParent">hwnd of the parent (this application)</param>
        /// <param name="hwndChildAfter">hwnd of the next application (0)</param>
        /// <param name="lpszClass">null</param>
        /// <param name="lpszWindow">null</param>
        /// <returns>A pointer to the opened application</returns>
		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr FindWindowEx(
			IntPtr hwndParent,
			IntPtr hwndChildAfter,
			string lpszClass,
			string lpszWindow);

        /// <summary>
        /// Send a message to the opened application (<see cref="FindWindowEx(IntPtr, IntPtr, string, string)"/>
        /// </summary>
        /// <param name="hWnd">hWnd of the created application pointer.</param>
        /// <param name="uMsg">Message type</param>
        /// <param name="wParam">wParam is 0</param>
        /// <param name="lParam">lParam is the text to show in the application</param>
        /// <returns></returns>
		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr SendMessageTimeout(
			IntPtr hWnd,
			uint message,
			IntPtr wParam,
			string lParam,
			uint flags,
			uint timeoutMilliseconds,
			out IntPtr result);

		private const uint WmSetText = 0x000C;
		private const uint SmtoBlock = 0x0001;
		private const uint SmtoAbortIfHung = 0x0002;
		private const uint TextMessageTimeoutMilliseconds = 2000;

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

		private void convertoToDOTToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (Animations.Xml != null)
				OpenTextInNotepad(XmlToDot.ProcessXml(Animations.Xml.AnimationXML));
		}

		private void openXMLToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (Animations.Xml != null)
				OpenTextInNotepad(Animations.Xml.AnimationXMLString);
		}

		private static void OpenTextInNotepad(string text)
		{
			try
			{
				var startInfo = new ProcessStartInfo
				{
					FileName = System.IO.Path.Combine(
						Environment.SystemDirectory,
						"notepad.exe"),
					UseShellExecute = false
				};
				using (Process notepad = Process.Start(startInfo))
				{
					if (notepad == null || !notepad.WaitForInputIdle(5000)) return;
					notepad.Refresh();
					IntPtr child = FindWindowEx(
						notepad.MainWindowHandle,
						IntPtr.Zero,
						null,
						null);
					if (child != IntPtr.Zero)
					{
						IntPtr ignored;
						SendMessageTimeout(
							child,
							WmSetText,
							IntPtr.Zero,
							text ?? "",
							SmtoBlock | SmtoAbortIfHung,
							TextMessageTimeoutMilliseconds,
							out ignored);
					}
				}
			}
			catch
			{
				// The diagnostic window must not crash the pet if Notepad is unavailable.
			}
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
