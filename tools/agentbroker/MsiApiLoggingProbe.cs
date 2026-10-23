using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace InstallerLoggingProbe
{
    // https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msienablelogw
    // Settings belong to this calling process. No LOGONLYONERROR bit is permitted.
    // https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msiconfigureproductexw
    // Its final argument contains properties, never msiexec switches.
    public interface IInstallerApi
    {
        uint SetInternalUi(uint level);
        uint SetRecordHandler(Action<uint, string> callback);
        uint RestoreRecordHandler();
        uint EnableLog(uint mode, string path, uint attributes);
        uint Maintain(string operation, string product);
    }

    public sealed class ProbeResult
    {
        public string Status { get; set; } = "not_started";
        public string Operation { get; set; }
        public bool OperationAttempted { get; set; }
        public bool OperationCompleted { get; set; }
        public uint? OperationCode { get; set; }
        public uint? EnableLogCode { get; set; }
        public uint? InstallHandlerCode { get; set; }
        public uint? DisableLogCode { get; set; }
        public uint? RestoreHandlerCode { get; set; }
        public bool RestoreInternalUiSucceeded { get; set; }
        public bool ApiException { get; set; }
        public bool RestorationException { get; set; }
        public bool CallbackFailed { get; set; }
        public bool CallbackLimitReached { get; set; }
        public bool KnownShutdownMarkerSeen { get; set; }
        public bool RawMessagesRecorded { get; set; } = false;
        public uint LogMode { get; } = Probe.LogMode;
        public uint LogAttributes { get; } = Probe.FlushEachLine;
        public bool LogOnlyOnErrorIncluded { get; } = false;
        public Dictionary<string, int> MessageCounts { get; } = new Dictionary<string, int>
        {
            ["Info"] = 0, ["ActionStart"] = 0, ["Error"] = 0, ["Warning"] = 0,
            ["InstallStart"] = 0, ["InstallEnd"] = 0, ["Other"] = 0
        };
    }

    public static class Probe
    {
        // Named SDK flags: fatal/error/warning/user/info/source/disk/action/common/property/verbose.
        public const uint LogMode = 0x1FDF;
        public const uint LogOnlyOnError = 0x4000;
        public const uint FlushEachLine = 2;
        public const uint CallbackFailure = UInt32.MaxValue;
        public const string Marker = "Installation-scoped companion shutdown: targets=1, graceful=1, terminated=0.";

        public static ProbeResult Run(IInstallerApi api, string operation, string product, string log)
        {
            if (operation != "Uninstall") throw new ArgumentException("operation");
            if (api == null || String.IsNullOrWhiteSpace(product) || String.IsNullOrWhiteSpace(log)) throw new ArgumentException("inputs");
            var result = new ProbeResult { Operation = operation };
            uint previousUi = 0;
            bool uiChanged = false, handlerChanged = false, loggingEnabled = false;
            int received = 0;
            object gate = new object();
            Action<uint, string> observer = (kind, text) =>
            {
                // No callback is allowed to unwind into native MSI, even after a malformed record.
                try
                {
                    lock (gate)
                    {
                        if (kind == CallbackFailure) { result.CallbackFailed = true; return; }
                        if (received >= 100000) { result.CallbackLimitReached = true; return; }
                        received++;
                        uint category = kind >> 24;
                        string name = category == 4 ? "Info" : category == 8 ? "ActionStart" :
                            category == 1 ? "Error" : category == 2 ? "Warning" :
                            category == 26 ? "InstallStart" : category == 27 ? "InstallEnd" : "Other";
                        result.MessageCounts[name]++;
                        // Match only the exact known fixture receipt. Never retain arbitrary text.
                        if (category == 4 && String.Equals(text, Marker, StringComparison.Ordinal)) result.KnownShutdownMarkerSeen = true;
                    }
                }
                catch { result.CallbackFailed = true; }
            };
            try
            {
                previousUi = api.SetInternalUi(2); // INSTALLUILEVEL_NONE, no dialogs.
                if (previousUi == 0) { result.Status = "ui_setup_failed"; return result; }
                uiChanged = true;
                result.InstallHandlerCode = api.SetRecordHandler(observer);
                if (result.InstallHandlerCode != 0) { result.Status = "handler_setup_failed"; return result; }
                handlerChanged = true;
                result.EnableLogCode = api.EnableLog(LogMode, log, FlushEachLine);
                if (result.EnableLogCode != 0) { result.Status = "logging_setup_failed"; return result; }
                loggingEnabled = true;
                result.OperationAttempted = true; // A throwing API can still have started maintenance.
                result.OperationCode = api.Maintain(operation, product);
                result.OperationCompleted = true;
                result.Status = result.OperationCode == 0 ? "operation_returned_success" : "operation_returned_error";
            }
            catch { result.ApiException = true; result.Status = "api_exception"; }
            finally
            {
                // The disposable worker has no pre-existing logging configuration. Disable ours.
                if (loggingEnabled)
                {
                    try { result.DisableLogCode = api.EnableLog(0, null, 0); }
                    catch { result.RestorationException = true; }
                }
                if (handlerChanged)
                {
                    try { result.RestoreHandlerCode = api.RestoreRecordHandler(); }
                    catch { result.RestorationException = true; }
                }
                if (uiChanged)
                {
                    try { result.RestoreInternalUiSucceeded = api.SetInternalUi(previousUi) != 0; }
                    catch { result.RestorationException = true; }
                }
                if (result.RestorationException || loggingEnabled && result.DisableLogCode != 0 ||
                    handlerChanged && result.RestoreHandlerCode != 0 || uiChanged && !result.RestoreInternalUiSucceeded)
                    result.Status = "restoration_failed";
                else if (result.CallbackFailed) result.Status = "callback_failed";
                GC.KeepAlive(observer);
            }
            return result;
        }
    }

    public sealed class NativeInstallerApi : IInstallerApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int RecordHandler(IntPtr context, uint kind, uint record);
        [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiSetInternalUI(uint level, IntPtr window);
        [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiSetExternalUIRecord(IntPtr handler, uint filter, IntPtr context, out IntPtr previous);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiEnableLogW(uint mode, string file, uint attributes);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiConfigureProductExW(string product, int level, int state, string properties);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiFormatRecordW(uint install, uint record, StringBuilder text, ref uint length);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiGetProductInfoW(string product, string property, StringBuilder value, ref uint length);
        private RecordHandler handler;
        private IntPtr previousHandler;
        public uint SetInternalUi(uint level) => MsiSetInternalUI(level, IntPtr.Zero);
        public static string RegisteredProperty(string product, string property)
        {
            if (property != "InstallLocation" && property != "ProductName" && property != "VersionString") throw new ArgumentException("property");
            uint length = 0;
            uint code = MsiGetProductInfoW(product, property, null, ref length);
            if (code != 0 && code != 234 || length > 32768) throw new InvalidOperationException("registration_unavailable");
            var value = new StringBuilder((int)length + 1);
            uint capacity = (uint)value.Capacity;
            if (MsiGetProductInfoW(product, property, value, ref capacity) != 0) throw new InvalidOperationException("registration_unavailable");
            return value.ToString();
        }
        public uint SetRecordHandler(Action<uint, string> callback)
        {
            handler = (context, kind, record) =>
            {
                try
                {
                    string value = null;
                    // Metadata-only counts for every record; decode only the INFO marker candidate.
                    if ((kind >> 24) == 4)
                    {
                        uint length = 0;
                        uint code = MsiFormatRecordW(0, record, null, ref length);
                        if (code != 0 && code != 234) callback(Probe.CallbackFailure, null);
                        if ((code == 0 || code == 234) && length <= 2048)
                        {
                            var text = new StringBuilder((int)length + 1);
                            uint capacity = (uint)text.Capacity;
                            if (MsiFormatRecordW(0, record, text, ref capacity) == 0) value = text.ToString();
                            else callback(Probe.CallbackFailure, null);
                        }
                    }
                    callback(kind, value);
                }
                catch
                {
                    // Fixed failure signal, even after an earlier valid marker. Never raw errors.
                    try { callback(Probe.CallbackFailure, null); } catch { }
                }
                return 0; // Preserve normal message handling; internal UI is separately NONE.
            };
            // https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msisetexternaluirecord
            return MsiSetExternalUIRecord(Marshal.GetFunctionPointerForDelegate(handler), 0x0C000F1F, IntPtr.Zero, out previousHandler);
        }
        public uint RestoreRecordHandler()
        {
            IntPtr ignored;
            // The documented filter=0 restores the previous record handler's stored configuration.
            uint code = MsiSetExternalUIRecord(previousHandler, 0, IntPtr.Zero, out ignored);
            GC.KeepAlive(handler);
            return code;
        }
        public uint EnableLog(uint mode, string path, uint attributes) => MsiEnableLogW(mode, path, attributes);
        public uint Maintain(string operation, string product)
        {
            if (operation == "Uninstall") return MsiConfigureProductExW(product, 0, 2, "REBOOT=ReallySuppress");
            throw new ArgumentException("operation");
        }
    }
}
