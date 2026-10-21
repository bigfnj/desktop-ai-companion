using System.Globalization;
using System.Text.Json;
using AgentBroker.ClaudeAdapter;

// No default scan: each caller-supplied PID selects one exact numeric .json target only.
if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine("ProbeNativeMetadata <native-pid> [native-pid ...], maximum 32 explicit PIDs.");
    Console.WriteLine("Read-only default-home .claude/sessions/<PID>.json only. Aggregate output; no keys, pipes, transcripts, listing or cleanup.");
    return 0;
}
if (args.Length == 2 && args[0] == "--shape")
{
    if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 1 || pid.ToString(CultureInfo.InvariantCulture) != args[1])
    { Console.Error.WriteLine("A canonical numeric PID is required."); return 2; }
    try { Console.WriteLine(JsonSerializer.Serialize(NativeMetadataReader.InspectDefaultHomeProcessShape(pid))); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
        System.ComponentModel.Win32Exception or NotSupportedException)
    { Console.WriteLine("{\"metadataUnavailable\":true}"); }
    return 0;
}
if (args.Length is 0 or > 32 || args.Any(value => !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ||
    pid <= 1 || pid.ToString(CultureInfo.InvariantCulture) != value))
{
    Console.Error.WriteLine("Explicit canonical PIDs are required; no discovery scan is performed.");
    return 2;
}
int valid = 0, unavailable = 0, idle = 0, busy = 0, other = 0;
foreach (int pid in args.Select(value => int.Parse(value, CultureInfo.InvariantCulture)).Distinct())
{
    try
    {
        var metadata = NativeMetadataReader.ReadDefaultHomeProcess(pid);
        valid++;
        if (metadata.Status == "idle") idle++;
        else if (metadata.Status == "busy") busy++;
        else other++;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
        System.ComponentModel.Win32Exception or NotSupportedException)
    { unavailable++; } // Never log record contents, paths, IDs or exception text.
}
Console.WriteLine(JsonSerializer.Serialize(new { requested = args.Distinct().Count(), valid, unavailable, idle, busy, other,
    credentialsRead = false, pipeConnected = false, readinessProven = false }));
return 0;
