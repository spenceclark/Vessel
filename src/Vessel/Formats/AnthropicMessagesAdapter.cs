using System.Text;
using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>
/// Anthropic messages adapter (D5/D6). Streamed events are folded into a synthesized
/// <c>message</c> document: <c>message_start</c> seeds it (model, role, input/cache
/// tokens), content blocks are built from <c>content_block_*</c> events, and
/// <c>message_delta</c> supplies the stop reason and output tokens. <c>tokens_in</c> sums
/// input and both cache token counts — the UI wants total submitted context.
/// </summary>
public sealed class AnthropicMessagesAdapter : IFormatAdapter
{
    public AdapterResult Parse(AdapterInput input)
    {
        string? requestModel = JsonUtil.Str(JsonUtil.Object(input.Request)?["model"]);
        var result = new AdapterResult { PromptText = TextFlattener.AnthropicPrompt(input.Request) };

        JsonObject? doc = input.ResponseText is null
            ? null
            : input.Streamed
                ? Reassemble(input.ResponseText, result)
                : JsonUtil.Object(JsonUtil.Parse(input.ResponseText));

        if (input.Streamed && doc is not null)
        {
            result.ReassembledResponse = Encoding.UTF8.GetBytes(doc.ToJsonString());
        }

        result.Model = JsonUtil.Str(doc?["model"]) ?? requestModel;

        JsonObject? usage = JsonUtil.Object(doc?["usage"]);
        result.TokensCachedRead = JsonUtil.Long(usage?["cache_read_input_tokens"]);
        result.TokensCachedWrite = JsonUtil.Long(usage?["cache_creation_input_tokens"]);
        result.TokensIn = JsonUtil.Sum(
            JsonUtil.Long(usage?["input_tokens"]), result.TokensCachedRead, result.TokensCachedWrite);
        result.TokensOut = JsonUtil.Long(usage?["output_tokens"]);
        result.StopReason = JsonUtil.Str(doc?["stop_reason"]);
        result.ResponseText = TextFlattener.AnthropicResponse(doc);

        return result;
    }

    private static JsonObject Reassemble(string streamText, AdapterResult result)
    {
        List<SseEvent> events = SseParser.Parse(streamText);

        JsonObject? message = null;
        var blocks = new SortedDictionary<int, BlockAccumulator>();
        bool sawStop = false;

        foreach (SseEvent evt in events)
        {
            JsonObject? data = JsonUtil.Object(JsonUtil.Parse(evt.Data));
            string? type = evt.EventType ?? JsonUtil.Str(data?["type"]);

            switch (type)
            {
                case "message_start":
                    if (JsonUtil.Object(data?["message"]) is JsonObject start)
                    {
                        message = (JsonObject)JsonNode.Parse(start.ToJsonString())!;
                    }

                    break;

                case "content_block_start":
                    Block(blocks, data).Start = JsonUtil.Object(data?["content_block"]);
                    break;

                case "content_block_delta":
                    Fold(Block(blocks, data), JsonUtil.Object(data?["delta"]));
                    break;

                case "message_delta":
                    if (message is not null)
                    {
                        if (JsonUtil.Str(JsonUtil.Object(data?["delta"])?["stop_reason"]) is string stop)
                        {
                            message["stop_reason"] = stop;
                        }

                        if (JsonUtil.Long(JsonUtil.Object(data?["usage"])?["output_tokens"]) is long outputTokens)
                        {
                            MessageUsage(message)["output_tokens"] = outputTokens;
                        }
                    }

                    break;

                case "message_stop":
                    sawStop = true;
                    break;
            }
        }

        message ??= new JsonObject { ["type"] = "message", ["role"] = "assistant" };
        if (!sawStop)
        {
            result.Warnings.Add(Warnings.StreamIncomplete);
        }

        var content = new JsonArray();
        foreach ((int _, BlockAccumulator acc) in blocks)
        {
            content.Add(acc.ToBlock());
        }

        message["content"] = content;
        return message;
    }

    private static BlockAccumulator Block(SortedDictionary<int, BlockAccumulator> blocks, JsonObject? data)
    {
        int index = (int)(JsonUtil.Long(data?["index"]) ?? 0);
        if (!blocks.TryGetValue(index, out BlockAccumulator? acc))
        {
            blocks[index] = acc = new BlockAccumulator();
        }

        return acc;
    }

    private static void Fold(BlockAccumulator acc, JsonObject? delta)
    {
        switch (JsonUtil.Str(delta?["type"]))
        {
            case "text_delta":
                acc.Text.Append(JsonUtil.Str(delta?["text"]));
                break;
            case "thinking_delta":
                acc.Thinking.Append(JsonUtil.Str(delta?["thinking"]));
                break;
            case "input_json_delta":
                acc.Json.Append(JsonUtil.Str(delta?["partial_json"]));
                break;
        }
    }

    private static JsonObject MessageUsage(JsonObject message)
    {
        if (JsonUtil.Object(message["usage"]) is JsonObject usage)
        {
            return usage;
        }

        var created = new JsonObject();
        message["usage"] = created;
        return created;
    }

    private sealed class BlockAccumulator
    {
        public JsonObject? Start { get; set; }

        public StringBuilder Text { get; } = new();

        public StringBuilder Thinking { get; } = new();

        public StringBuilder Json { get; } = new();

        public JsonNode ToBlock()
        {
            switch (JsonUtil.Str(Start?["type"]))
            {
                case "text":
                    return new JsonObject { ["type"] = "text", ["text"] = Text.ToString() };
                case "thinking":
                    return new JsonObject { ["type"] = "thinking", ["thinking"] = Thinking.ToString() };
                case "tool_use":
                    return new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = JsonUtil.Str(Start?["id"]),
                        ["name"] = JsonUtil.Str(Start?["name"]),
                        ["input"] = JsonUtil.Parse(Json.ToString()) ?? new JsonObject(),
                    };
                default:
                    return Start is not null
                        ? JsonNode.Parse(Start.ToJsonString())!
                        : new JsonObject();
            }
        }
    }
}
