namespace Vessel.Formats;

/// <summary>
/// #84 — flags degenerate looping output (a model stuck emitting <c>1/1/1/…</c>). Only the
/// last <see cref="WindowChars"/> chars of a response of at least <see cref="MinChars"/> are
/// examined, so the work is capped regardless of response size. Repetitive when either:
/// (a) one unit of 1–64 chars repeats back-to-back over at least 80% of the window, or
/// (b) distinct 8-char shingles are under 5% of all shingles in the window.
/// </summary>
public static class RepetitionDetector
{
    private const int MinChars = 2000;
    private const int WindowChars = 4000;
    private const int MaxUnit = 64;
    private const double MinPeriodicCoverage = 0.8;
    private const int ShingleChars = 8;
    private const double MaxDistinctShingleRatio = 0.05;

    public static bool IsRepetitive(string? text)
    {
        if (text is null || text.Length < MinChars)
        {
            return false;
        }

        ReadOnlySpan<char> window = text.AsSpan(Math.Max(0, text.Length - WindowChars));
        return HasPeriodicRun(window) || HasFewDistinctShingles(window);
    }

    // For each unit length, the longest stretch where every char equals the one `unit`
    // chars earlier is a back-to-back repetition covering that stretch plus one unit.
    private static bool HasPeriodicRun(ReadOnlySpan<char> window)
    {
        int needed = (int)Math.Ceiling(window.Length * MinPeriodicCoverage);
        for (int unit = 1; unit <= MaxUnit && unit < window.Length; unit++)
        {
            int run = 0;
            for (int i = unit; i < window.Length; i++)
            {
                run = window[i] == window[i - unit] ? run + 1 : 0;
                if (run + unit >= needed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasFewDistinctShingles(ReadOnlySpan<char> window)
    {
        int total = window.Length - ShingleChars + 1;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < total; i++)
        {
            distinct.Add(window.Slice(i, ShingleChars).ToString());
        }

        return distinct.Count < total * MaxDistinctShingleRatio;
    }
}
