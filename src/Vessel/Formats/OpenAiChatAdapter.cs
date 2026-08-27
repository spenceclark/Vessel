using System.Text;
using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>
/// OpenAI chat-completions adapter (D5/D6). SSE deltas are folded, per choice index, into
/// a synthesized <c>chat.completion</c> document so streamed and non-streamed responses
/// extract and render identically. Usage comes from the final usage-bearing chunk; model
/// and id from the first chunk.
/// </summary>
public sealed class OpenAiChatAdapter : IFormatAdapter
{
    public AdapterResult Parse(AdapterInput input)
    {
        string? requestModel = JsonUtil.Str(JsonUtil.Object(input.Request)?["model"]);
        var result = new AdapterResult { PromptText = TextFlattener.ChatMessages(input.Request) };

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
        result.TokensIn = JsonUtil.Long(usage?["prompt_tokens"]);
        result.TokensOut = JsonUtil.Long(usage?["completion_tokens"]);
        result.TokensCachedRead = JsonUtil.Long(JsonUtil.Object(usage?["prompt_tokens_details"])?["cached_tokens"]);

        JsonArray? choices = JsonUtil.Array(doc?["choices"]);
        result.StopReason = JsonUtil.Str(JsonUtil.Object(choices?.FirstOrDefault())?["finish_reason"]);
        result.ResponseText = TextFlattener.OpenAiResponse(doc);

        return result;
    }

    private static JsonObject Reassemble(string streamText, AdapterResult result)
    {
        List<SseEvent> events = SseParser.Parse(streamText);

        string? id = null;
        string? model = null;
        long? created = null;
        JsonNode? usage = null;
        bool sawDone = false;
        bool sawFinish = false;
        var choices = new SortedDictionary<int, ChoiceAccumulator>();

        foreach (SseEvent evt in events)
        {
            if (evt.Data == "[DONE]")
            {
                sawDone = true;
                continue;
            }

            JsonObject? chunk = JsonUtil.Object(JsonUtil.Parse(evt.Data));
            if (chunk is null)
            {
                continue;
            }

            id ??= JsonUtil.Str(chunk["id"]);
            model ??= JsonUtil.Str(chunk["model"]);
            created ??= JsonUtil.Long(chunk["created"]);
            if (JsonUtil.Object(chunk["usage"]) is JsonObject u)
            {
                usage = u;
            }

            foreach (JsonNode? choiceNode in JsonUtil.Array(chunk["choices"]) ?? [])
            {
                JsonObject? choice = JsonUtil.Object(choiceNode);
                if (choice is null)
                {
                    continue;
                }

                int index = (int)(JsonUtil.Long(choice["index"]) ?? 0);
                if (!choices.TryGetValue(index, out ChoiceAccumulator? acc))
                {
                    choices[index] = acc = new ChoiceAccumulator();
                }

                JsonObject? delta = JsonUtil.Object(choice["delta"]);
                acc.Content.Append(JsonUtil.Str(delta?["content"]));
                acc.Reasoning.Append(JsonUtil.Str(delta?["reasoning_content"]));
                acc.FoldToolCalls(delta?["tool_calls"]);

                if (JsonUtil.Str(choice["finish_reason"]) is string finish)
                {
                    acc.FinishReason = finish;
                    sawFinish = true;
                }
            }
        }

        if (!sawDone && !sawFinish)
        {
            result.Warnings.Add(Warnings.StreamIncomplete);
        }

        var synthChoices = new JsonArray();
        foreach ((int index, ChoiceAccumulator acc) in choices)
        {
            synthChoices.Add(acc.ToChoice(index));
        }

        var synth = new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = synthChoices,
        };
        if (usage is not null)
        {
            synth["usage"] = JsonNode.Parse(usage.ToJsonString());
        }

        return synth;
    }

    private sealed class ChoiceAccumulator
    {
        public StringBuilder Content { get; } = new();

        public StringBuilder Reasoning { get; } = new();

        public string? FinishReason { get; set; }

        private readonly SortedDictionary<int, ToolCallAccumulator> _toolCalls = [];

        public void FoldToolCalls(JsonNode? toolCalls)
        {
            foreach (JsonNode? callNode in JsonUtil.Array(toolCalls) ?? [])
            {
                JsonObject? call = JsonUtil.Object(callNode);
                if (call is null)
                {
                    continue;
                }

                int index = (int)(JsonUtil.Long(call["index"]) ?? 0);
                if (!_toolCalls.TryGetValue(index, out ToolCallAccumulator? acc))
                {
                    _toolCalls[index] = acc = new ToolCallAccumulator();
                }

                acc.Id ??= JsonUtil.Str(call["id"]);
                acc.Type ??= JsonUtil.Str(call["type"]);
                JsonObject? function = JsonUtil.Object(call["function"]);
                acc.Name ??= JsonUtil.Str(function?["name"]);
                acc.Arguments.Append(JsonUtil.Str(function?["arguments"]));
            }
        }

        // Returns JsonNode (not JsonObject) so JsonArray.Add binds to IList.Add(JsonNode?)
        // rather than the reflection-based generic Add<T> — keeps the code trim-clean.
        public JsonNode ToChoice(int index)
        {
            var message = new JsonObject { ["role"] = "assistant" };
            message["content"] = Content.Length > 0 ? Content.ToString() : null;
            if (Reasoning.Length > 0)
            {
                message["reasoning_content"] = Reasoning.ToString();
            }

            if (_toolCalls.Count > 0)
            {
                var array = new JsonArray();
                foreach ((int callIndex, ToolCallAccumulator acc) in _toolCalls)
                {
                    array.Add(acc.ToToolCall(callIndex));
                }

                message["tool_calls"] = array;
            }

            return new JsonObject
            {
                ["index"] = index,
                ["message"] = message,
                ["finish_reason"] = FinishReason,
            };
        }
    }

    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }

        public string? Type { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();

        public JsonNode ToToolCall(int index) => new JsonObject
        {
            ["index"] = index,
            ["id"] = Id,
            ["type"] = Type ?? "function",
            ["function"] = new JsonObject
            {
                ["name"] = Name,
                ["arguments"] = Arguments.ToString(),
            },
        };
    }
}
