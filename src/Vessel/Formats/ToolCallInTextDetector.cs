using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vessel.Formats;

/// <summary>
/// Detects a model fumble where a declared tool call is represented as JSON in assistant
/// text rather than in the provider's structured tool-call field. This is deliberately a
/// narrow signal: only complete JSON text or a fenced JSON block is considered, and its
/// name must match a tool declared by the request.
/// </summary>
internal static partial class ToolCallInTextDetector
{
    public static bool IsDetected(JsonNode? request, JsonNode? response, string? responseText)
    {
        HashSet<string> declaredTools = DeclaredToolNames(request);
        return declaredTools.Count > 0
            && !HasStructuredToolCall(response)
            && Candidates(responseText).Any(candidate => ContainsDeclaredToolCall(candidate, declaredTools));
    }

    private static HashSet<string> DeclaredToolNames(JsonNode? request)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? toolNode in JsonUtil.Array(JsonUtil.Object(request)?["tools"]) ?? [])
        {
            JsonObject? tool = JsonUtil.Object(toolNode);
            string? name = JsonUtil.Str(tool?["name"])
                ?? JsonUtil.Str(JsonUtil.Object(tool?["function"])?["name"]);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static bool HasStructuredToolCall(JsonNode? response)
    {
        JsonObject? document = JsonUtil.Object(response);
        foreach (JsonNode? choice in JsonUtil.Array(document?["choices"]) ?? [])
        {
            if (JsonUtil.Array(JsonUtil.Object(choice)?["message"]?["tool_calls"]) is { Count: > 0 })
            {
                return true;
            }
        }

        foreach (JsonNode? output in JsonUtil.Array(document?["output"]) ?? [])
        {
            if (JsonUtil.Str(JsonUtil.Object(output)?["type"]) == "function_call")
            {
                return true;
            }
        }

        JsonObject? message = JsonUtil.Object(document?["message"]);
        if (JsonUtil.Array(message?["tool_calls"]) is { Count: > 0 })
        {
            return true;
        }

        foreach (JsonNode? content in JsonUtil.Array(document?["content"]) ?? [])
        {
            if (JsonUtil.Str(JsonUtil.Object(content)?["type"]) == "tool_use")
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<JsonNode> Candidates(string? responseText)
    {
        if (JsonUtil.Parse(responseText) is JsonNode wholeResponse)
        {
            yield return wholeResponse;
        }

        if (responseText is null)
        {
            yield break;
        }

        foreach (Match match in JsonCodeBlock().Matches(responseText))
        {
            if (JsonUtil.Parse(match.Groups["json"].Value) is JsonNode codeBlock)
            {
                yield return codeBlock;
            }
        }
    }

    private static bool ContainsDeclaredToolCall(JsonNode? node, HashSet<string> declaredTools)
    {
        if (node is JsonArray array)
        {
            return array.Any(item => ContainsDeclaredToolCall(item, declaredTools));
        }

        JsonObject? obj = JsonUtil.Object(node);
        if (obj is null)
        {
            return false;
        }

        JsonObject? function = JsonUtil.Object(obj["function"]);
        string? name = JsonUtil.Str(obj["name"]) ?? JsonUtil.Str(function?["name"]);
        bool hasArguments = obj.ContainsKey("arguments") || obj.ContainsKey("input")
            || function?.ContainsKey("arguments") == true || function?.ContainsKey("input") == true;
        if (name is not null && hasArguments && declaredTools.Contains(name))
        {
            return true;
        }

        return obj.Any(entry => ContainsDeclaredToolCall(entry.Value, declaredTools));
    }

    [GeneratedRegex("```(?:json)?[ \\t]*\\r?\\n(?<json>.*?)```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex JsonCodeBlock();
}
