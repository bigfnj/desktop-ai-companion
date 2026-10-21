# Read only native Windows error dialogs. It does not dismiss a dialog, read process
# command lines/environment, or inspect any browser/application profile.
# Run: pwsh -NoProfile -File tools/agentbroker/inspect-test-dialog.ps1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class AgentBrokerDialogProbe {
    private delegate bool EnumProc(IntPtr handle, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr value);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder value, int length);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder value, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    private static string Text(IntPtr handle) { var b = new StringBuilder(4096); GetWindowText(handle,b,b.Capacity); return b.ToString(); }
    private static string Class(IntPtr handle) { var b = new StringBuilder(128); GetClassName(handle,b,b.Capacity); return b.ToString(); }
    public sealed class Record {
        public uint ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string Title { get; set; }
        public string[] StaticText { get; set; }
    }
    public static Record[] Read() {
        var found = new List<Record>();
        EnumWindows((h,p) => {
            if (!IsWindowVisible(h) || Class(h) != "#32770") return true;
            string title = Text(h);
            if (title.IndexOf("error",StringComparison.OrdinalIgnoreCase)<0 && title.IndexOf(".NET",StringComparison.OrdinalIgnoreCase)<0) return true;
            GetWindowThreadProcessId(h,out uint pid);
            string name;
            try { using (var process=Process.GetProcessById((int)pid)) name=process.ProcessName; } catch { name="unavailable"; }
            var text = new List<string>();
            EnumChildWindows(h,(c,v) => { if (Class(c)=="Static") { string s=Text(c); if(s.Length>0) text.Add(s); } return true; },IntPtr.Zero);
            found.Add(new Record {ProcessId=pid, ProcessName=name, Title=title, StaticText=text.ToArray()});
            return true;
        },IntPtr.Zero);
        return found.ToArray();
    }
}
'@
$dialogs = @([AgentBrokerDialogProbe]::Read())
ConvertTo-Json -InputObject @{ Count = $dialogs.Count; Dialogs = $dialogs } -Depth 4 -Compress
