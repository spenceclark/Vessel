using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>
/// D4 — newline-delimited JSON splitter for Ollama-native streams. Splits on newlines,
/// parses each line, and skips any unparseable fragment (typically the final line of a
/// truncated capture). Whether the stream saw a terminal <c>done: true</c> object is left
/// to the adapter.
/// </summary>
public static class NdjsonParser
{
    /// <summary>Parses every well-formed JSON object line; unparseable lines are skipped.</summary>
    public static List<JsonNode> Parse(string text)
    {
        var objects = new List<JsonNode>();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? node = JsonUtil.Parse(line);
            if (node is not null)
            {
                objects.Add(node);
            }
        }

        return objects;
    }
}
