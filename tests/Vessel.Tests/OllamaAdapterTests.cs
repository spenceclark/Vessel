using System.Text.Json.Nodes;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// R09 — the golden fixture suite covers the documented multi-tool/thinking case end to
/// end; this file targets the specific defect shape the review called out: an *empty*
/// first <c>tool_calls</c> array. Under the old <c>??=</c> accumulation, `[]` still counts
/// as "seen a non-null value" and permanently masks every later batch — a plain "was it
/// ever null" check wouldn't catch that, so it needs its own case.
/// </summary>
public class OllamaAdapterTests
{
    private static AdapterResult ParseChat(string ndjson) =>
        new OllamaAdapter(generate: false).Parse(new AdapterInput(Request: null, ndjson, Streamed: true));

    [Fact]
    public void EmptyFirstToolCallsArray_DoesNotMaskLaterBatch()
    {
        string stream = string.Join('\n',
            """{"model":"m","message":{"role":"assistant","content":"","tool_calls":[]},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_weather","arguments":{"city":"Paris"}}}]},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":""},"done_reason":"stop","done":true,"eval_count":1,"eval_duration":1000000}""") + "\n";

        AdapterResult result = ParseChat(stream);

        JsonObject message = (JsonObject)JsonNode.Parse(System.Text.Encoding.UTF8.GetString(result.ReassembledResponse!))!["message"]!;
        JsonArray toolCalls = (JsonArray)message["tool_calls"]!;
        Assert.Single(toolCalls);
        Assert.Equal("get_weather", toolCalls[0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void MultipleToolCallBatches_AllAccumulated()
    {
        string stream = string.Join('\n',
            """{"model":"m","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"first","arguments":{}}}]},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"second","arguments":{}}}]},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":""},"done_reason":"stop","done":true,"eval_count":1,"eval_duration":1000000}""") + "\n";

        AdapterResult result = ParseChat(stream);

        JsonObject message = (JsonObject)JsonNode.Parse(System.Text.Encoding.UTF8.GetString(result.ReassembledResponse!))!["message"]!;
        JsonArray toolCalls = (JsonArray)message["tool_calls"]!;
        Assert.Equal(["first", "second"], toolCalls.Select(c => c!["function"]!["name"]!.GetValue<string>()));
    }

    [Fact]
    public void ThinkingAccumulatesAcrossChunks_AndSurvivesInResponseText()
    {
        string stream = string.Join('\n',
            """{"model":"m","message":{"role":"assistant","content":"","thinking":"step one. "},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":"","thinking":"step two."},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":"answer"},"done_reason":"stop","done":true,"eval_count":1,"eval_duration":1000000}""") + "\n";

        AdapterResult result = ParseChat(stream);

        JsonObject message = (JsonObject)JsonNode.Parse(System.Text.Encoding.UTF8.GetString(result.ReassembledResponse!))!["message"]!;
        Assert.Equal("step one. step two.", message["thinking"]!.GetValue<string>());
        Assert.Equal("answer\nstep one. step two.", result.ResponseText);
    }
}
