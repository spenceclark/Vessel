using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>
/// D2 — format detection: the stored path suffix first, then payload shape, then the
/// backend <c>type</c> as a tiebreak. Detection runs from the request side alone for
/// error/failed rows (a 502 to <c>/api/chat</c> is still <c>ollama-chat</c>). Nothing
/// matching is <c>raw</c>, silently — unknown traffic is normal.
/// </summary>
public static class FormatDetector
{
    public static string Detect(string path, JsonNode? request, string? responseText, string? backendType)
    {
        string p = StripQuery(path);
        if (p.EndsWith("/api/chat", StringComparison.Ordinal))
        {
            return FormatNames.OllamaChat;
        }

        if (p.EndsWith("/api/generate", StringComparison.Ordinal))
        {
            return FormatNames.OllamaGenerate;
        }

        if (p.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return FormatNames.OpenAiChat;
        }

        if (p.EndsWith("/messages", StringComparison.Ordinal))
        {
            return FormatNames.AnthropicMessages;
        }

        return SniffPayload(request, responseText, backendType);
    }

    private static string SniffPayload(JsonNode? request, string? responseText, string? backendType)
    {
        JsonObject? req = JsonUtil.Object(request);
        JsonObject? response = FirstResponseObject(responseText);

        bool hasMessages = req?["messages"] is JsonArray;
        bool hasPrompt = req?["prompt"] is not null;

        if (hasMessages)
        {
            bool anthropicShape = req?["max_tokens"] is not null
                && (req?["system"] is not null || response?["stop_reason"] is not null);
            if (anthropicShape)
            {
                return FormatNames.AnthropicMessages;
            }

            if (response?["choices"] is JsonArray)
            {
                return FormatNames.OpenAiChat;
            }

            if (response?["done"] is not null)
            {
                return FormatNames.OllamaChat;
            }
        }

        if (hasPrompt && (response?["done"] is not null || response?["response"] is not null))
        {
            return FormatNames.OllamaGenerate;
        }

        // Backend-type tiebreak, only when the request clearly looks like a chat/generate
        // call — never promote embeddings, tags, or other raw traffic.
        switch (backendType?.ToLowerInvariant())
        {
            case "anthropic" when hasMessages:
                return FormatNames.AnthropicMessages;
            case "openai" when hasMessages:
                return FormatNames.OpenAiChat;
            case "ollama" when hasMessages:
                return FormatNames.OllamaChat;
            case "ollama" when hasPrompt:
                return FormatNames.OllamaGenerate;
        }

        return FormatNames.Raw;
    }

    /// <summary>
    /// The first JSON object in a response for sniffing: the whole body when non-streamed,
    /// otherwise the first NDJSON line or SSE <c>data:</c> payload.
    /// </summary>
    private static JsonObject? FirstResponseObject(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        if (JsonUtil.Object(JsonUtil.Parse(responseText)) is JsonObject whole)
        {
            return whole;
        }

        foreach (string rawLine in responseText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                line = line["data:".Length..].Trim();
                if (line == "[DONE]")
                {
                    continue;
                }
            }

            if (line.StartsWith('{') && JsonUtil.Object(JsonUtil.Parse(line)) is JsonObject obj)
            {
                return obj;
            }
        }

        return null;
    }

    private static string StripQuery(string path)
    {
        int query = path.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? path : path[..query];
    }
}
