using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.ClaudeAdapter;

/// <summary>
/// Read-only default-home numeric registry probe. Does not enumerate directories, read credentials,
/// invoke vendor listing/cleanup, inspect transcripts, or connect to native pipes.
/// </summary>
public static class NativeMetadataReader
{
    public static NativeSessionMetadata ReadDefaultHomeProcess(int processId)
        => ReadDefaultHomeProcess(processId, NativeSessionMetadata.Parse);

    public static IReadOnlyDictionary<string, string> InspectDefaultHomeProcessShape(int processId)
        => ReadDefaultHomeProcess(processId, (_, bytes) =>
        {
            try
            {
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid registry record.");
                string[] allowed = ["messagingSocketPath", "sessionId", "procStartFt", "procStart", "kind", "status", "entrypoint", "updatedAt", "statusUpdatedAt", "peerProtocol"];
                var result = allowed.ToDictionary(name => name,
                    name => json.RootElement.TryGetProperty(name, out var value) ? value.ValueKind.ToString() : "Absent", StringComparer.Ordinal);
                if (json.RootElement.TryGetProperty("procStart", out var legacy) && legacy.ValueKind == JsonValueKind.String &&
                    ulong.TryParse(legacy.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong token))
                {
                    using var process = Process.GetProcessById(processId);
                    var local = process.StartTime;
                    ulong native = checked((ulong)local.ToUniversalTime().ToFileTimeUtc());
                    ulong utcTicks = checked((ulong)local.ToUniversalTime().Ticks), localTicks = checked((ulong)local.Ticks);
                    result["procStartMatchesNative"] = (token == native).ToString();
                    result["procStartMatchesUtcTicks"] = (token == utcTicks).ToString();
                    result["procStartMatchesLocalTicks"] = (token == localTicks).ToString();
                    result["procStartMatchesUtcMicroseconds"] = (token / 10 == utcTicks / 10).ToString();
                    result["procStartMatchesLocalMicroseconds"] = (token / 10 == localTicks / 10).ToString();
                }
                return (IReadOnlyDictionary<string, string>)result;
            }
            catch (JsonException) { throw new InvalidDataException("Malformed registry record."); }
        });

    private static TResult ReadDefaultHomeProcess<TResult>(int processId, Func<string, ReadOnlyMemory<byte>, TResult> parse)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (processId <= 1) throw new ArgumentException("A canonical native PID is required.");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathFullyQualified(home)) throw new IOException("Cannot resolve the current user home.");
        string filename = processId.ToString(CultureInfo.InvariantCulture) + ".json";
        string path = Path.GetFullPath(Path.Combine(home, ".claude", "sessions", filename).Normalize(NormalizationForm.FormC));
        CredentialPathPolicy.RejectReparseComponents(path, File.GetAttributes);
        using var handle = CreateFileW(path, 0x80000000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info) || info.Links != 1 || (info.Attributes & (0x400 | 0x10)) != 0 ||
            info.SizeHigh != 0 || info.SizeLow > NativeSessionMetadata.MaximumRecordBytes)
            throw new IOException("Registry target is not a bounded regular file.");
        var final = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new IOException("Cannot verify the registry target.");
        string resolved = final.ToString();
        if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal) || resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(resolved[4..]), path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Registry target changed or is not local.");
        byte[] buffer = new byte[NativeSessionMetadata.MaximumRecordBytes + 1];
        try
        {
            using var input = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
            int used = 0, count;
            while (used < buffer.Length && (count = input.Read(buffer, used, buffer.Length - used)) != 0) used += count;
            if (used > NativeSessionMetadata.MaximumRecordBytes) throw new IOException("Registry record grew beyond its bound.");
            return parse(filename, buffer.AsMemory(0, used));
        }
        finally { Array.Clear(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { internal uint Low; internal uint High; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes;
        internal FileTime Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
