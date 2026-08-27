namespace Vessel.Formats;

/// <summary>The <c>format</c> column's vocabulary — the strings stored per row and used to pick an adapter.</summary>
public static class FormatNames
{
    public const string OpenAiChat = "openai-chat";
    public const string AnthropicMessages = "anthropic-messages";
    public const string OllamaChat = "ollama-chat";
    public const string OllamaGenerate = "ollama-generate";
    public const string Raw = "raw";
}
