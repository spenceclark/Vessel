using System.Text;
using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>
/// OpenAI Responses API adapter (<c>/v1/responses</c>) — a different endpoint from Chat
/// Completions, with a different wire shape: requests use <c>input</c> instead of
/// <c>messages</c>; responses are one <c>response</c> object with an <c>output</c> array
/// of typed items (<c>message</c>, <c>reasoning</c>, <c>function_call</c>, …) instead of
/// <c>choices</c>. Streaming is simpler than Chat Completions here: rather than deltas that
/// need folding, the terminal SSE event (<c>response.completed</c>/<c>.incomplete</c>/
/// <c>.failed</c>) carries the complete final response object in its own <c>response</c>
/// field, so reassembly is just picking that event out.
/// </summary>
public sealed class OpenAiResponsesAdapter : IFormatAdapter
{
    public AdapterResult Parse(AdapterInput input)
    {
        string? requestModel = JsonUtil.Str(JsonUtil.Object(input.Request)?["model"]);
        var result = new AdapterResult { PromptText = TextFlattener.ResponsesInput(input.Request) };

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
        result.TokensIn = JsonUtil.Long(usage?["input_tokens"]);
        result.TokensOut = JsonUtil.Long(usage?["output_tokens"]);
        result.TokensCachedRead = JsonUtil.Long(JsonUtil.Object(usage?["input_tokens_details"])?["cached_tokens"]);

        result.StopReason = StopReason(doc);
        result.ResponseText = TextFlattener.ResponsesOutput(doc);

        return result;
    }

    /// <summary>
    /// Normalizes <c>status</c>/<c>incomplete_details.reason</c> onto the same stop-reason
    /// vocabulary Chat Completions/Anthropic use, so downstream truncation/error handling
    /// (§4: "length"/"max_tokens" → truncated warning; "content_filter"/"refusal"/"error" →
    /// danger) doesn't need a second code path just for this format. Anything unrecognized
    /// passes through verbatim rather than being swallowed.
    /// </summary>
    private static string? StopReason(JsonObject? doc)
    {
        string? status = JsonUtil.Str(doc?["status"]);
        if (status != "incomplete")
        {
            return status switch
            {
                "completed" => "stop",
                "failed" => "error",
                _ => status,
            };
        }

        string? reason = JsonUtil.Str(JsonUtil.Object(doc?["incomplete_details"])?["reason"]);
        return reason switch
        {
            "max_output_tokens" => "length",
            null => "incomplete",
            _ => reason,
        };
    }

    private static JsonObject? Reassemble(string streamText, AdapterResult result)
    {
        JsonObject? finalResponse = null;
        bool sawTerminal = false;

        foreach (SseEvent evt in SseParser.Parse(streamText))
        {
            JsonObject? data = JsonUtil.Object(JsonUtil.Parse(evt.Data));
            string? type = evt.EventType ?? JsonUtil.Str(data?["type"]);
            if (type is not ("response.completed" or "response.incomplete" or "response.failed"))
            {
                continue;
            }

            sawTerminal = true;
            if (JsonUtil.Object(data?["response"]) is JsonObject resp)
            {
                finalResponse = resp;
            }
        }

        if (!sawTerminal)
        {
            result.Warnings.Add(Warnings.StreamIncomplete);
        }

        return finalResponse;
    }
}
