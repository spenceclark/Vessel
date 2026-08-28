using System.Text;
using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>
/// Ollama-native adapter for <c>/api/chat</c> and <c>/api/generate</c> (D5/D6). NDJSON
/// streams are folded into the provider's own final-object shape; the <c>done: true</c>
/// object supplies exact token counts and durations, and its non-content fields are
/// merged into the synthesized document so they're visible in the stored body too.
/// </summary>
public sealed class OllamaAdapter(bool generate) : IFormatAdapter
{
    private const long ColdLoadThresholdNs = 1_000_000_000; // 1 s

    public AdapterResult Parse(AdapterInput input)
    {
        var result = new AdapterResult
        {
            PromptText = generate
                ? TextFlattener.OllamaGeneratePrompt(input.Request)
                : TextFlattener.ChatMessages(input.Request),
        };

        string? requestModel = JsonUtil.Str(JsonUtil.Object(input.Request)?["model"]);

        JsonObject? doc = input.ResponseText is null
            ? null
            : input.Streamed
                ? Reassemble(input.ResponseText, requestModel, result)
                : JsonUtil.Object(JsonUtil.Parse(input.ResponseText));

        if (input.Streamed && doc is not null)
        {
            result.ReassembledResponse = Encoding.UTF8.GetBytes(doc.ToJsonString());
        }

        result.Model = JsonUtil.Str(doc?["model"]) ?? requestModel;

        if (doc is not null)
        {
            result.TokensIn = JsonUtil.Long(doc["prompt_eval_count"]);
            result.TokensOut = JsonUtil.Long(doc["eval_count"]);
            result.StopReason = JsonUtil.Str(doc["done_reason"]);
            result.ResponseText = generate
                ? TextFlattener.OllamaGenerateResponse(doc)
                : TextFlattener.OllamaChatResponse(doc);

            long? evalDuration = JsonUtil.Long(doc["eval_duration"]);
            if (result.TokensOut is long tokens && evalDuration is long ns && ns > 0)
            {
                result.TokPerSec = tokens / (ns / 1_000_000_000.0);
            }

            if (JsonUtil.Long(doc["load_duration"]) is long load && load > ColdLoadThresholdNs)
            {
                result.Warnings.Add(Warnings.ColdLoad);
            }
        }

        return result;
    }

    private JsonObject Reassemble(string streamText, string? requestModel, AdapterResult result)
    {
        List<JsonNode> lines = NdjsonParser.Parse(streamText);

        var content = new StringBuilder();
        var thinking = new StringBuilder();
        JsonObject? done = null;
        // R09 — Ollama's wire shape sends each turn's tool_calls as a *complete* array per
        // chunk (not OpenAI-style indexed fragments), but a multi-tool-call turn can still
        // arrive across more than one chunk. `??=` on the first sighting silently dropped
        // every later batch — including the case where the first sighting was an empty
        // array (still "non-null", so it masked everything after). Every non-empty array
        // seen is appended in order instead.
        var toolCalls = new JsonArray();

        foreach (JsonNode line in lines)
        {
            JsonObject? obj = JsonUtil.Object(line);
            if (obj is null)
            {
                continue;
            }

            if (generate)
            {
                content.Append(JsonUtil.Str(obj["response"]));
                thinking.Append(JsonUtil.Str(obj["thinking"]));
            }
            else
            {
                JsonObject? message = JsonUtil.Object(obj["message"]);
                content.Append(JsonUtil.Str(message?["content"]));
                thinking.Append(JsonUtil.Str(message?["thinking"]));
                foreach (JsonNode? call in JsonUtil.Array(message?["tool_calls"]) ?? [])
                {
                    toolCalls.Add(call is null ? null : JsonNode.Parse(call.ToJsonString()));
                }
            }

            if (JsonUtil.Bool(obj["done"]))
            {
                done = obj;
            }
        }

        if (done is null)
        {
            result.Warnings.Add(Warnings.StreamIncomplete);
        }

        // Start from the final object (with its exact counts/durations) and substitute
        // the aggregated content; fall back to a minimal object on a truncated stream.
        JsonObject synth = done is not null
            ? (JsonObject)JsonNode.Parse(done.ToJsonString())!
            : new JsonObject { ["model"] = requestModel, ["done"] = false };

        if (generate)
        {
            synth["response"] = content.ToString();
            if (thinking.Length > 0)
            {
                synth["thinking"] = thinking.ToString();
            }
        }
        else
        {
            JsonObject message = JsonUtil.Object(synth["message"]) is JsonObject existing
                ? existing
                : NewMessage(synth);
            message["content"] = content.ToString();
            if (thinking.Length > 0)
            {
                message["thinking"] = thinking.ToString();
            }

            if (toolCalls.Count > 0)
            {
                message["tool_calls"] = toolCalls;
            }
        }

        return synth;
    }

    private static JsonObject NewMessage(JsonObject synth)
    {
        var message = new JsonObject { ["role"] = "assistant" };
        synth["message"] = message;
        return message;
    }
}
