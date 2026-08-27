namespace Vessel.Formats;

/// <summary>
/// D8 — the chars/4 fallback for when a backend doesn't report usage (the canonical case
/// being an OpenAI-format stream without <c>include_usage</c>). Estimation only ever fills
/// a <em>missing</em> count; a reported value is never overwritten, and each estimated
/// value independently flags the row.
/// </summary>
public static class TokenEstimator
{
    /// <summary>ceil(text length / 4), or null for null/empty text.</summary>
    public static long? Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? null : (text.Length + 3) / 4;
}
