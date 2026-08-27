using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>
/// Small, exception-free helpers over <see cref="JsonNode"/> for reading untrusted LLM
/// payloads. Adapters run on arbitrary (and deliberately malformed) input, so every
/// accessor returns null rather than throwing — the enricher's backstop is for genuine
/// bugs, not for expected garbage.
/// </summary>
internal static class JsonUtil
{
    /// <summary>Parses text into a node, or null if it isn't valid JSON.</summary>
    public static JsonNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The string value of a node, or null if it isn't a JSON string.</summary>
    public static string? Str(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? s))
        {
            return s;
        }

        return null;
    }

    /// <summary>The integer value of a node (accepts JSON integers and whole doubles), or null.</summary>
    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out long l))
        {
            return l;
        }

        if (value.TryGetValue(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            return (long)d;
        }

        return null;
    }

    /// <summary>True only if the node is JSON <c>true</c>.</summary>
    public static bool Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool b) && b;

    /// <summary>The node as an array, or null if it isn't one.</summary>
    public static JsonArray? Array(JsonNode? node) => node as JsonArray;

    /// <summary>The node as an object, or null if it isn't one.</summary>
    public static JsonObject? Object(JsonNode? node) => node as JsonObject;

    /// <summary>Sum of the given values treating null as absent; null only when every input is null.</summary>
    public static long? Sum(params long?[] values)
    {
        long total = 0;
        bool any = false;
        foreach (long? v in values)
        {
            if (v.HasValue)
            {
                total += v.Value;
                any = true;
            }
        }

        return any ? total : null;
    }
}
