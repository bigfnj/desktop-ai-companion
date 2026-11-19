using System.Text;
using System.Text.Json.Nodes;

namespace AgentBroker.Core;

// AgentBroker 1.10.6, N-broker-native-confirmation-01 (lane N round 1, DECISIONS 15, 19, 21, 23): the report-only
// broker-events.jsonl in the broker store directory, beside broker-state.json and broker-tombstones.jsonl. One JSON object
// per line, never message text. The broker writes it and never reads it. Bounded at MaximumBytes with one rotation to
// broker-events.1.jsonl, replacing an older one. Best-effort by design: a failed write never affects delivery or the commit
// that produced the line; it is counted, and the next line written in this process carries "eventsLost". A loss followed by no
// successful write before the process exits is recorded nowhere, so the file can under-report, never over-report.
internal sealed class BrokerEventLog
{
    internal const long MaximumBytes = 1024 * 1024;
    // Lane N round 2: no single line is written over this (a 256-row Codex discovery_pass is about 48 KB). It bounds the whole
    // encoded line: every array, the JSON escaping, eventAt and any eventsLost added below.
    internal const int MaximumLineBytes = 64 * 1024;
    internal const string FileName = "broker-events.jsonl";
    internal const string RotatedFileName = "broker-events.1.jsonl";
    private readonly string path;
    private readonly string rotated;
    private long lost;

    internal BrokerEventLog(string directory)
    {
        path = Path.Combine(directory, FileName);
        rotated = Path.Combine(directory, RotatedFileName);
    }

    // Never throws. Called under the store gate, after the commit that produced the line has written its snapshot.
    internal void Append(JsonObject line)
    {
        try
        {
            // Lane N round 2: a line without a valid event name (ValidKind), or over MaximumLineBytes once encoded, is refused
            // and counted exactly like a failed write, so the next line written says so in eventsLost.
            if (line["event"] is not JsonValue kind || !kind.TryGetValue(out string? name) || !ValidKind(name)) { lost++; return; }
            if (lost > 0) line["eventsLost"] = lost;
            byte[] bytes = Encoding.UTF8.GetBytes(line.ToJsonString() + "\n");
            if (bytes.Length > MaximumLineBytes) { lost++; return; }
            var existing = new FileInfo(path);
            if (existing.Exists && existing.Length + bytes.Length > MaximumBytes) File.Move(path, rotated, overwrite: true);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) stream.Write(bytes);
            lost = 0;
        }
        catch (Exception) { lost++; }
    }

    // An event name: 1 to 48 characters of a-z, 0-9 and '_', starting with a letter.
    private static bool ValidKind(string? name) => name is { Length: >= 1 and <= 48 } && name[0] is >= 'a' and <= 'z' &&
        name.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
