using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Vessel.Tests;

/// <summary>Helpers shared by the golden and detector tests: order-independent JSON comparison.</summary>
public static class GoldenJson
{
    /// <summary>
    /// Structural JSON equality: objects match regardless of property order, arrays match
    /// in order, numbers compare by value. Used to assert a synthesized <c>response_body</c>
    /// against its golden expectation without pinning property ordering.
    /// </summary>
    public static bool DeepEquals(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (null, _) or (_, null):
                return false;
        }

        switch (a)
        {
            case JsonObject oa when b is JsonObject ob:
                if (oa.Count != ob.Count)
                {
                    return false;
                }

                foreach (KeyValuePair<string, JsonNode?> kvp in oa)
                {
                    if (!ob.TryGetPropertyValue(kvp.Key, out JsonNode? other) || !DeepEquals(kvp.Value, other))
                    {
                        return false;
                    }
                }

                return true;

            case JsonArray aa when b is JsonArray ab:
                if (aa.Count != ab.Count)
                {
                    return false;
                }

                for (int i = 0; i < aa.Count; i++)
                {
                    if (!DeepEquals(aa[i], ab[i]))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValue va when b is JsonValue vb:
                return ValueEquals(va, vb);

            default:
                return false;
        }
    }

    private static bool ValueEquals(JsonValue a, JsonValue b)
    {
        if (a.TryGetValue(out bool ba) && b.TryGetValue(out bool bb))
        {
            return ba == bb;
        }

        if (a.TryGetValue(out double da) && b.TryGetValue(out double db))
        {
            return da == db;
        }

        if (a.TryGetValue(out string? sa) && b.TryGetValue(out string? sb))
        {
            return sa == sb;
        }

        // Fall back to canonical JSON text for anything else.
        return a.ToJsonString() == b.ToJsonString();
    }

    public static void AssertDeepEquals(JsonNode? expected, JsonNode? actual)
    {
        if (!DeepEquals(expected, actual))
        {
            Assert.Fail(
                $"JSON documents differ.\nexpected: {expected?.ToJsonString()}\nactual:   {actual?.ToJsonString()}");
        }
    }

    public static JsonNode? Parse(byte[]? bytes) =>
        bytes is null ? null : JsonNode.Parse(bytes);

    public static JsonDocument ReadDocument(string path) =>
        JsonDocument.Parse(File.ReadAllBytes(path));
}
