using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>F2 — SSE parser units: line endings, multi-line data, comments, [DONE], truncation.</summary>
public class SseParserTests
{
    [Fact]
    public void Lf_SplitsEvents()
    {
        List<SseEvent> events = SseParser.Parse("data: a\n\ndata: b\n\n");
        Assert.Equal(["a", "b"], events.Select(e => e.Data));
        Assert.All(events, e => Assert.Null(e.EventType));
    }

    [Fact]
    public void Crlf_SplitsEvents()
    {
        List<SseEvent> events = SseParser.Parse("data: a\r\n\r\ndata: b\r\n\r\n");
        Assert.Equal(["a", "b"], events.Select(e => e.Data));
    }

    [Fact]
    public void MultipleDataLines_JoinedWithNewline()
    {
        List<SseEvent> events = SseParser.Parse("data: line1\ndata: line2\n\n");
        SseEvent only = Assert.Single(events);
        Assert.Equal("line1\nline2", only.Data);
    }

    [Fact]
    public void EventType_IsCaptured()
    {
        List<SseEvent> events = SseParser.Parse("event: message_start\ndata: {\"x\":1}\n\n");
        SseEvent only = Assert.Single(events);
        Assert.Equal("message_start", only.EventType);
        Assert.Equal("{\"x\":1}", only.Data);
    }

    [Fact]
    public void CommentAndKeepAliveLines_Ignored()
    {
        List<SseEvent> events = SseParser.Parse(": keep-alive\n\ndata: real\n\n: ping\n");
        SseEvent only = Assert.Single(events);
        Assert.Equal("real", only.Data);
    }

    [Fact]
    public void DoneSentinel_IsAnEvent()
    {
        List<SseEvent> events = SseParser.Parse("data: {\"x\":1}\n\ndata: [DONE]\n\n");
        Assert.Equal(["{\"x\":1}", "[DONE]"], events.Select(e => e.Data));
    }

    [Fact]
    public void NoSpaceAfterColon_IsAccepted()
    {
        List<SseEvent> events = SseParser.Parse("data:{\"x\":1}\n\n");
        Assert.Equal("{\"x\":1}", Assert.Single(events).Data);
    }

    // Event cut mid-bytes → the prior complete events survive, the partial is discarded.
    [Fact]
    public void FinalEventCutMidBytes_DiscardedPriorIntact()
    {
        List<SseEvent> events = SseParser.Parse("data: first\n\ndata: {\"partial\":");
        SseEvent only = Assert.Single(events);
        Assert.Equal("first", only.Data);
    }

    // A stream lacking any terminal marker leaves the adapter to flag stream_incomplete;
    // the parser itself still yields the events it saw.
    [Fact]
    public void NoTerminalMarker_StillYieldsEvents()
    {
        List<SseEvent> events = SseParser.Parse("data: a\n\ndata: b\n\n");
        Assert.DoesNotContain(events, e => e.Data == "[DONE]");
        Assert.Equal(2, events.Count);
    }

    // R19 — a single trailing newline is not an event terminator: `string.Split('\n')`
    // always manufactures a final empty element that isn't a real blank line the stream
    // contained. The matrix: LF/CRLF x {no newline, one, two} after the last data line.
    // Only the two-newline (real blank line) case terminates the event.
    [Theory]
    [InlineData("data: [DONE]", 0)] // no newline at all: unterminated line, discarded
    [InlineData("data: [DONE]\n", 0)] // one LF: line complete, but no blank line follows
    [InlineData("data: [DONE]\n\n", 1)] // two LF: real blank line terminates the event
    [InlineData("data: [DONE]\r", 0)]
    [InlineData("data: [DONE]\r\n", 0)]
    [InlineData("data: [DONE]\r\n\r\n", 1)]
    public void TerminalNewlineMatrix_OnlyRealBlankLineDispatches(string text, int expectedCount)
    {
        List<SseEvent> events = SseParser.Parse(text);
        Assert.Equal(expectedCount, events.Count);
    }

    // Same matrix, one event ahead of the sentinel — proves a genuine prior blank line is
    // never mistaken for the trailing artifact (only the *last* split element is dropped).
    [Theory]
    [InlineData("data: a\n\ndata: [DONE]", 1)]
    [InlineData("data: a\n\ndata: [DONE]\n", 1)]
    [InlineData("data: a\n\ndata: [DONE]\n\n", 2)]
    public void TerminalNewlineMatrix_PriorEventUnaffected(string text, int expectedCount)
    {
        List<SseEvent> events = SseParser.Parse(text);
        Assert.Equal(expectedCount, events.Count);
        Assert.Equal("a", events[0].Data);
    }
}
