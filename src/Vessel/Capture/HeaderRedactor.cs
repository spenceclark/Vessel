using System.Text.Json;

namespace Vessel.Capture;

/// <summary>
/// Redacts secret-bearing headers for the stored copy only (forwarding is untouched).
/// Runs on the request path, before the record enters the channel — plaintext secrets
/// never reach the writer or the database.
/// </summary>
public static class HeaderRedactor
{
    /// <summary>Request headers whose values are secrets (architecture §8), plus Set-Cookie on responses.</summary>
    private static readonly HashSet<string> _secretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "X-Api-Key",
        "Api-Key",
        "Cookie",
        "Set-Cookie",
    };

    /// <summary>Redacted headers as a JSON object of name → value array.</summary>
    public static string ToRedactedJson(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string[]>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in headers)
        {
            string[] values = header.Value.Where(v => v is not null).ToArray()!;
            if (_secretHeaders.Contains(header.Key))
            {
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = Redact(values[i]);
                }
            }

            result[header.Key] = values;
        }

        return JsonSerializer.Serialize(result, CaptureJsonContext.Default.DictionaryStringStringArray);
    }

    /// <summary>
    /// Scheme + last 4 chars: <c>Bearer …-Ab4x</c>, or <c>…Ab4x</c> for schemeless
    /// values. Secrets of 8 chars or fewer lose the tail entirely — too short to
    /// safely echo any of it. A leading token only counts as a scheme when it looks
    /// like one (plain RFC auth-scheme token) — cookie values such as
    /// <c>sid=…; Path=/</c> must never have their first pair preserved.
    /// </summary>
    public static string Redact(string value)
    {
        string scheme = "";
        string secret = value;

        int space = value.IndexOf(' ');
        if (space > 0 && !value.AsSpan(0, space).ContainsAny('=', ';', ','))
        {
            scheme = value[..space] + " ";
            secret = value[(space + 1)..].TrimStart();
        }

        return secret.Length > 8 ? $"{scheme}…{secret[^4..]}" : $"{scheme}…";
    }
}
