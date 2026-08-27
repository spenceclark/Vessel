using System.Text;
using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>
/// D9 — flattens prompts and responses into plain text for FTS and list preview (not for
/// display). Text blocks verbatim; tool definitions skipped; tool-use/tool-result blocks
/// contribute name + stringified args/result; images contribute nothing (base64 never
/// enters FTS). Reasoning/thinking text is included. Empty results collapse to null so
/// the FTS row is skipped.
/// </summary>
public static class TextFlattener
{
    /// <summary>Flattens an OpenAI/Ollama-style <c>messages</c> array (roles carry system prompts inline).</summary>
    public static string? ChatMessages(JsonNode? request)
    {
        JsonArray? messages = JsonUtil.Array(JsonUtil.Object(request)?["messages"]);
        if (messages is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (JsonNode? message in messages)
        {
            AppendMessage(sb, message);
        }

        return NullIfEmpty(sb);
    }

    /// <summary>Flattens an Anthropic request: top-level <c>system</c> then the <c>messages</c> array.</summary>
    public static string? AnthropicPrompt(JsonNode? request)
    {
        JsonObject? obj = JsonUtil.Object(request);
        var sb = new StringBuilder();

        string? system = FlattenContent(obj?["system"]);
        if (!string.IsNullOrEmpty(system))
        {
            AppendLine(sb, "system", system);
        }

        JsonArray? messages = JsonUtil.Array(obj?["messages"]);
        if (messages is not null)
        {
            foreach (JsonNode? message in messages)
            {
                AppendMessage(sb, message);
            }
        }

        return NullIfEmpty(sb);
    }

    /// <summary>Flattens an Ollama <c>/api/generate</c> request: optional <c>system</c> + <c>prompt</c>.</summary>
    public static string? OllamaGeneratePrompt(JsonNode? request)
    {
        JsonObject? obj = JsonUtil.Object(request);
        var sb = new StringBuilder();

        string? system = JsonUtil.Str(obj?["system"]);
        if (!string.IsNullOrEmpty(system))
        {
            AppendLine(sb, "system", system);
        }

        string? prompt = JsonUtil.Str(obj?["prompt"]);
        if (!string.IsNullOrEmpty(prompt))
        {
            AppendLine(sb, "user", prompt);
        }

        return NullIfEmpty(sb);
    }

    /// <summary>Flattens an OpenAI <c>chat.completion</c> (wire or synthesized) to assistant text.</summary>
    public static string? OpenAiResponse(JsonNode? completion)
    {
        JsonArray? choices = JsonUtil.Array(JsonUtil.Object(completion)?["choices"]);
        if (choices is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (JsonNode? choice in choices)
        {
            JsonObject? message = JsonUtil.Object(choice)?["message"] as JsonObject;
            Append(sb, JsonUtil.Str(message?["reasoning_content"]));
            Append(sb, FlattenContent(message?["content"]));
            AppendToolCalls(sb, message?["tool_calls"]);
        }

        return NullIfEmpty(sb);
    }

    /// <summary>Flattens an Anthropic <c>message</c> (wire or synthesized) content array.</summary>
    public static string? AnthropicResponse(JsonNode? message)
    {
        string? text = FlattenContent(JsonUtil.Object(message)?["content"]);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Flattens an Ollama chat response object's <c>message</c> (content + tool calls).</summary>
    public static string? OllamaChatResponse(JsonNode? messageObject)
    {
        JsonObject? message = JsonUtil.Object(JsonUtil.Object(messageObject)?["message"]);
        var sb = new StringBuilder();
        Append(sb, JsonUtil.Str(message?["content"]));
        AppendToolCalls(sb, message?["tool_calls"]);
        return NullIfEmpty(sb);
    }

    /// <summary>Flattens an Ollama generate response object's <c>response</c> text.</summary>
    public static string? OllamaGenerateResponse(JsonNode? responseObject)
    {
        string? text = JsonUtil.Str(JsonUtil.Object(responseObject)?["response"]);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static void AppendMessage(StringBuilder sb, JsonNode? message)
    {
        JsonObject? obj = JsonUtil.Object(message);
        if (obj is null)
        {
            return;
        }

        string role = JsonUtil.Str(obj["role"]) ?? "user";
        var content = new StringBuilder();
        Append(content, FlattenContent(obj["content"]));
        AppendToolCalls(content, obj["tool_calls"]);

        string text = content.ToString().Trim();
        if (text.Length > 0)
        {
            AppendLine(sb, role, text);
        }
    }

    /// <summary>Content may be a plain string or an array of typed parts (OpenAI or Anthropic shapes).</summary>
    private static string? FlattenContent(JsonNode? content)
    {
        if (content is null)
        {
            return null;
        }

        string? asString = JsonUtil.Str(content);
        if (asString is not null)
        {
            return asString;
        }

        if (JsonUtil.Array(content) is not JsonArray parts)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (JsonNode? part in parts)
        {
            JsonObject? obj = JsonUtil.Object(part);
            switch (JsonUtil.Str(obj?["type"]))
            {
                case "text":
                    Append(sb, JsonUtil.Str(obj?["text"]));
                    break;
                case "thinking":
                    Append(sb, JsonUtil.Str(obj?["thinking"]));
                    break;
                case "tool_use":
                    Append(sb, $"[tool_use {JsonUtil.Str(obj?["name"])}] {Compact(obj?["input"])}".Trim());
                    break;
                case "tool_result":
                    Append(sb, $"[tool_result] {FlattenContent(obj?["content"])}".Trim());
                    break;

                // image / image_url / input_audio and unknown parts contribute nothing.
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static void AppendToolCalls(StringBuilder sb, JsonNode? toolCalls)
    {
        if (JsonUtil.Array(toolCalls) is not JsonArray calls)
        {
            return;
        }

        foreach (JsonNode? call in calls)
        {
            JsonObject? function = JsonUtil.Object(JsonUtil.Object(call)?["function"]);
            string? name = JsonUtil.Str(function?["name"]);
            JsonNode? arguments = function?["arguments"];
            // OpenAI serializes arguments as a JSON string; keep it as-is, else compact.
            string args = JsonUtil.Str(arguments) ?? Compact(arguments);
            Append(sb, $"[tool_call {name}] {args}".Trim());
        }
    }

    private static void AppendLine(StringBuilder sb, string role, string text)
    {
        if (sb.Length > 0)
        {
            sb.Append('\n');
        }

        sb.Append(role).Append(": ").Append(text);
    }

    private static void Append(StringBuilder sb, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (sb.Length > 0)
        {
            sb.Append('\n');
        }

        sb.Append(text);
    }

    private static string Compact(JsonNode? node) => node?.ToJsonString() ?? "";

    private static string? NullIfEmpty(StringBuilder sb) => sb.Length == 0 ? null : sb.ToString();
}
