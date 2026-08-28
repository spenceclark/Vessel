namespace Vessel.Formats;

/// <summary>One dispatched Server-Sent Event: its optional <c>event:</c> type and joined <c>data:</c> payload.</summary>
public readonly record struct SseEvent(string? EventType, string Data);

/// <summary>
/// D4 — a single SSE parser shared by the OpenAI and Anthropic adapters. Operates on the
/// complete captured buffer (already decoded to text). Handles LF and CRLF line endings,
/// multiple <c>data:</c> lines per event, comment/keep-alive lines (<c>:</c> prefix), and
/// <c>event:</c> types. Truncation-tolerant: an event is only dispatched when terminated
/// by a blank line, so a final event cut off mid-bytes is discarded and prior events stay
/// intact. Whether the stream reached a terminal marker is left to the adapter.
/// </summary>
public static class SseParser
{
    public static List<SseEvent> Parse(string text)
    {
        var events = new List<SseEvent>();
        string? eventType = null;
        var data = new System.Text.StringBuilder();
        bool haveData = false;

        // R19 — `string.Split('\n')` always yields a final element that is not a real
        // line: if `text` ends with '\n' (the normal case), that element is the empty
        // string sitting *after* the last newline, not a blank line the stream actually
        // contained; if `text` has no trailing newline, it's an unterminated partial line.
        // Either way it never represents a *complete* line the spec says to process — an
        // event is only dispatched on an actual blank line, and an unterminated line is
        // discarded (truncation tolerance) — so it's dropped unconditionally before the
        // line loop rather than falling through and being mistaken for a genuine blank
        // line (which previously let `"data: x\n"` alone look like a terminated event).
        string[] lines = text.Split('\n');
        foreach (string rawLine in lines.Take(lines.Length - 1))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;

            if (line.Length == 0)
            {
                // Blank line = event boundary. Per the SSE spec, an event with no data
                // field is not dispatched.
                if (haveData)
                {
                    events.Add(new SseEvent(eventType, TrimTrailingNewline(data.ToString())));
                }

                eventType = null;
                data.Clear();
                haveData = false;
                continue;
            }

            if (line[0] == ':')
            {
                continue; // comment / keep-alive
            }

            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..]; // strip a single leading space after the colon
            }

            switch (field)
            {
                case "data":
                    data.Append(value).Append('\n');
                    haveData = true;
                    break;
                case "event":
                    eventType = value;
                    break;

                // id:, retry:, and unknown fields are ignored.
            }
        }

        // Any pending, unterminated event is discarded (truncation tolerance).
        return events;
    }

    private static string TrimTrailingNewline(string s) => s.EndsWith('\n') ? s[..^1] : s;
}
