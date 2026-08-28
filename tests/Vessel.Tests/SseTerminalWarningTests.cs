using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// R19 — the SSE parser's fixed terminal-blank-line handling must actually flow through
/// to the adapter's <c>stream_incomplete</c> warning: a stream cut right after its
/// sentinel data line, with no real blank line following, must not be treated as
/// terminated just because the buffer happens to end in a single newline.
/// </summary>
public class SseTerminalWarningTests
{
    [Theory]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{}}]}\n\ndata: [DONE]", true)]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{}}]}\n\ndata: [DONE]\n", true)]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{}}]}\n\ndata: [DONE]\n\n", false)]
    public void OpenAiChat_TerminalNewlineMatrix_DrivesStreamIncomplete(string streamText, bool expectIncomplete)
    {
        var adapter = new OpenAiChatAdapter();
        AdapterResult result = adapter.Parse(new AdapterInput(Request: null, streamText, Streamed: true));

        Assert.Equal(expectIncomplete, result.Warnings.Contains(Warnings.StreamIncomplete));
    }

    [Theory]
    [InlineData("event: message_stop\ndata: {}", true)]
    [InlineData("event: message_stop\ndata: {}\n", true)]
    [InlineData("event: message_stop\ndata: {}\n\n", false)]
    public void AnthropicMessages_TerminalNewlineMatrix_DrivesStreamIncomplete(string streamText, bool expectIncomplete)
    {
        var adapter = new AnthropicMessagesAdapter();
        AdapterResult result = adapter.Parse(new AdapterInput(Request: null, streamText, Streamed: true));

        Assert.Equal(expectIncomplete, result.Warnings.Contains(Warnings.StreamIncomplete));
    }
}
