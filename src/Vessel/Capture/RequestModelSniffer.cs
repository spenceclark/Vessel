using System.Text.Json;

namespace Vessel.Capture;

/// <summary>
/// D5 (<c>request_ready</c>) — best-effort, top-level <c>"model"</c> string lookup in a
/// captured request body. Every supported format (Chat Completions, Ollama, Anthropic
/// Messages, the Responses API) puts it at the JSON root, so this needs no per-format
/// knowledge. Malformed/truncated JSON, a missing or non-string field, or an empty body
/// all just mean "nothing to report" — this only ever runs off the request path, but
/// there's still no reason to surface partial or garbage traffic as a model name.
/// </summary>
internal static class RequestModelSniffer
{
    public static string? TryExtractModel(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("model", out JsonElement model)
                && model.ValueKind == JsonValueKind.String)
            {
                return model.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed/truncated body — nothing to report.
        }

        return null;
    }
}
