using System.Text.Json.Nodes;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// #82 — a streamed <c>server_tool_use</c> block's input arrives via
/// <c>input_json_delta</c> just like <c>tool_use</c>; reassembly must keep it rather than
/// returning the empty <c>content_block_start</c> input.
/// </summary>
public class AnthropicAdapterTests
{
    [Fact]
    public void StreamedServerToolUse_ReassemblesInputJsonDelta()
    {
        string stream = string.Join("\n\n",
            """event: message_start""" + "\n" + """data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-4-5","content":[],"usage":{"input_tokens":1}}}""",
            """event: content_block_start""" + "\n" + """data: {"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{}}}""",
            """event: content_block_delta""" + "\n" + """data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\": \"vessel"}}""",
            """event: content_block_delta""" + "\n" + """data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":" proxy\"}"}}""",
            """event: content_block_stop""" + "\n" + """data: {"type":"content_block_stop","index":0}""",
            """event: message_stop""" + "\n" + """data: {"type":"message_stop"}""") + "\n\n";

        AdapterResult result = new AnthropicMessagesAdapter().Parse(new AdapterInput(Request: null, stream, Streamed: true));

        JsonNode block = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(result.ReassembledResponse!))!["content"]![0]!;
        Assert.Equal("server_tool_use", block["type"]!.GetValue<string>());
        Assert.Equal("srvtoolu_1", block["id"]!.GetValue<string>());
        Assert.Equal("web_search", block["name"]!.GetValue<string>());
        Assert.Equal("vessel proxy", block["input"]!["query"]!.GetValue<string>());
    }
}
