using System.Text;
using System.Text.Json;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>#84 — degenerate looping output is flagged; ordinary prose and structured output are not.</summary>
public class RepetitionDetectorTests
{
    [Fact]
    public void SlashOneLoop_IsRepetitive() =>
        Assert.True(RepetitionDetector.IsRepetitive(string.Concat(Enumerable.Repeat("1/", 20_000))));

    [Fact]
    public void ShortUnitLoop_IsRepetitive() =>
        Assert.True(RepetitionDetector.IsRepetitive(string.Concat(Enumerable.Repeat("abc", 5_000))));

    [Fact]
    public void ProseThatDegeneratesIntoALoop_IsRepetitive() =>
        Assert.True(RepetitionDetector.IsRepetitive(NormalMarkdown(6_000) + string.Concat(Enumerable.Repeat("1/", 2_000))));

    [Fact]
    public void NormalMarkdown_IsNotRepetitive() =>
        Assert.False(RepetitionDetector.IsRepetitive(NormalMarkdown(10_000)));

    // Structured output repeats keys and punctuation on every item; the varying values must
    // keep it from being flagged.
    [Fact]
    public void JsonArrayOfSimilarSearchResults_IsNotRepetitive()
    {
        var results = Enumerable.Range(1, 200).Select(i => new
        {
            type = "web_search_result",
            title = $"Result {i}: {Words[i % Words.Length]} {Words[(i * 7) % Words.Length]} guide",
            url = $"https://example{i % 13}.com/{Words[(i * 3) % Words.Length]}/{i}",
            page_age = $"{i % 30} days ago",
        });

        Assert.False(RepetitionDetector.IsRepetitive(JsonSerializer.Serialize(results)));
    }

    [Fact]
    public void BelowMinimumLength_IsNotRepetitive() =>
        Assert.False(RepetitionDetector.IsRepetitive(new string('1', 1_500)));

    private static readonly string[] Words =
    [
        "proxy", "capture", "request", "session", "stream", "token", "backend", "response", "search",
        "config", "warning", "model", "latency", "retention", "export", "replay", "filter", "index",
    ];

    private static string NormalMarkdown(int chars)
    {
        var sb = new StringBuilder();
        var random = new Random(84);
        for (int section = 1; sb.Length < chars; section++)
        {
            sb.Append("## Section ").Append(section).Append("\n\n");
            for (int sentence = 0; sentence < 6; sentence++)
            {
                int length = random.Next(6, 16);
                for (int w = 0; w < length; w++)
                {
                    sb.Append(Words[random.Next(Words.Length)]).Append(w == length - 1 ? ". " : " ");
                }
            }

            sb.Append("\n\n- item ").Append(random.Next(1000)).Append(" handles the ").Append(Words[random.Next(Words.Length)]).Append("\n\n");
        }

        return sb.ToString();
    }
}
