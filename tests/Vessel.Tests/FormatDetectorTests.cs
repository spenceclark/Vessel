using System.Text.Json.Nodes;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>F3 — detection: path suffix, prefix-less payload sniff, backend-type tiebreak, raw.</summary>
public class FormatDetectorTests
{
    [Theory]
    [InlineData("/api/chat", FormatNames.OllamaChat)]
    [InlineData("/api/generate", FormatNames.OllamaGenerate)]
    [InlineData("/v1/chat/completions", FormatNames.OpenAiChat)]
    [InlineData("/v1/messages", FormatNames.AnthropicMessages)]
    [InlineData("/b/x/api/chat?foo=bar", FormatNames.OllamaChat)] // query stripped, suffix still matches
    public void PathSuffix_Wins(string path, string expected) =>
        Assert.Equal(expected, FormatDetector.Detect(path, request: null, responseText: null, backendType: null));

    // Prefix-less path: the format is revealed by request + response shape.
    [Fact]
    public void PayloadSniff_OpenAi()
    {
        JsonNode request = JsonNode.Parse("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""")!;
        string response = """{"choices":[{"index":0,"message":{"content":"hi"}}]}""";
        Assert.Equal(FormatNames.OpenAiChat, FormatDetector.Detect("/proxy/completions-alias", request, response, null));
    }

    [Fact]
    public void PayloadSniff_Anthropic()
    {
        JsonNode request = JsonNode.Parse(
            """{"model":"m","max_tokens":10,"system":"be brief","messages":[{"role":"user","content":"hi"}]}""")!;
        string response = """{"stop_reason":"end_turn","content":[]}""";
        Assert.Equal(FormatNames.AnthropicMessages, FormatDetector.Detect("/gateway/anthropic", request, response, null));
    }

    [Fact]
    public void PayloadSniff_OllamaChat_ViaNdjsonDone()
    {
        JsonNode request = JsonNode.Parse("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""")!;
        string response = "{\"message\":{\"content\":\"x\"},\"done\":false}\n{\"done\":true}\n";
        Assert.Equal(FormatNames.OllamaChat, FormatDetector.Detect("/weird/ollama", request, response, null));
    }

    // Error rows have no response — the backend type breaks the tie from the request alone.
    [Theory]
    [InlineData("openai", FormatNames.OpenAiChat)]
    [InlineData("anthropic", FormatNames.AnthropicMessages)]
    [InlineData("ollama", FormatNames.OllamaChat)]
    public void BackendTypeTiebreak_ChatRequestNoResponse(string type, string expected)
    {
        JsonNode request = JsonNode.Parse("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""")!;
        Assert.Equal(expected, FormatDetector.Detect("/nonstandard", request, responseText: null, backendType: type));
    }

    [Fact]
    public void BackendTypeTiebreak_OllamaGenerateFromPrompt()
    {
        JsonNode request = JsonNode.Parse("""{"model":"m","prompt":"hi"}""")!;
        Assert.Equal(FormatNames.OllamaGenerate, FormatDetector.Detect("/nonstandard", request, null, "ollama"));
    }

    // Unknown traffic — a typed backend must never promote non-chat payloads (e.g. embeddings).
    [Fact]
    public void EmbeddingsOnTypedBackend_StaysRaw()
    {
        JsonNode request = JsonNode.Parse("""{"model":"m","input":"embed this"}""")!;
        Assert.Equal(FormatNames.Raw, FormatDetector.Detect("/api/embeddings", request, null, "ollama"));
    }

    [Fact]
    public void NothingMatches_IsRaw()
    {
        Assert.Equal(FormatNames.Raw, FormatDetector.Detect("/api/tags", request: null, responseText: null, backendType: "ollama"));
        Assert.Equal(FormatNames.Raw, FormatDetector.Detect("/random", JsonNode.Parse("{}"), "not json", null));
    }
}
