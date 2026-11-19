using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// The report-only broker-events.jsonl of a store directory, read the way a concurrent appender allows (AgentBroker 1.10.6, lane N
// round 2; shared by the round 2 check files). The open shares ReadWrite and Delete, so the store's own append (FileShare.Read)
// and its rotation (a move) never fail because of this read. File.ReadAllLines asks for FileShare.Read instead: a concurrent
// append would then fail, be counted, and push an eventsLost into the next line, breaking exact grading. An IOException (a
// sharing violation in the instant of a rotation) is retried up to 10 times, 20 ms apart, and the tenth is thrown. Only
// newline-terminated lines are returned, because a line still being written is not yet a line. A missing file reads as empty.
// The rotated broker-events.1.jsonl is not read.
internal static class EventsFile
{
    internal const string FileName = "broker-events.jsonl";

    // The raw bytes of the current file, or none when it does not exist yet.
    internal static byte[] Bytes(string directory)
    {
        string path = Path.Combine(directory, FileName);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
            catch (FileNotFoundException) { return []; }
            catch (DirectoryNotFoundException) { return []; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(20); }
        }
    }

    // One JsonObject per complete line, in file order. A complete line that is not one JSON object is returned as an empty object,
    // so no line is hidden from a count of all lines; it has no event name, so no kind filter selects it.
    internal static IReadOnlyList<JsonObject> Read(string directory)
    {
        byte[] bytes = Bytes(directory);
        var lines = new List<JsonObject>();
        int start = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            lines.Add(Parse(Encoding.UTF8.GetString(bytes, start, index - start)));
            start = index + 1;
        }
        return lines;
    }

    private static JsonObject Parse(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}
