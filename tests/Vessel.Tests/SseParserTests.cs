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
}
