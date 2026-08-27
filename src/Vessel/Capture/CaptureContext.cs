using System.Diagnostics;
using System.Text.Json;
using Vessel.Proxy;

namespace Vessel.Capture;

/// <summary>
/// Per-request capture state: one monotonic clock, the timing marks, and the two body
/// buffers. Created at handler entry, stashed in <c>HttpContext.Items</c> so the
/// transformer can stamp the overhead mark, and turned into a <see cref="CaptureRecord"/>
/// once the response is complete.
/// </summary>
public sealed class CaptureContext(long maxBodyBytes)
{
    /// <summary>Key under which the context is stashed in <c>HttpContext.Items</c>.</summary>
    public const string ItemsKey = "Vessel.CaptureContext";

    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    public CaptureBuffer RequestBuffer { get; } = new(maxBodyBytes);

    public CaptureBuffer ResponseBuffer { get; } = new(maxBodyBytes);

    /// <summary>End of request transformation — the outbound request is fully prepared (§4.2 vessel_overhead_ms).</summary>
    public double? OverheadMs { get; private set; }

    /// <summary>Last read (data or EOF) from the request-body tee — request fully forwarded upstream.</summary>
    public double? RequestForwardedMs { get; private set; }

    /// <summary>Entry of the first write on the response tee — first response body byte from upstream.</summary>
    public double? FirstResponseByteMs { get; private set; }

    /// <summary>Proxy-level failure code, e.g. unknown_backend / upstream_unreachable / client_disconnect.</summary>
    public string? Error { get; set; }

    private double ElapsedMs => Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

    public void MarkOverhead() => OverheadMs ??= ElapsedMs;

    public void MarkRequestForwarded() => RequestForwardedMs = ElapsedMs;

    public void MarkFirstResponseByte() => FirstResponseByteMs ??= ElapsedMs;

    /// <summary>
    /// Builds the record on the request path — headers are redacted here, before the
    /// record ever reaches the channel. Bodies stay raw; the writer compresses.
    /// </summary>
    public CaptureRecord BuildRecord(HttpContext context, RouteDecision decision)
    {
        double durationMs = ElapsedMs;
        bool streamed = IsStreamedContentType(context.Response.ContentType);

        double? ttftMs = null;
        if (streamed && FirstResponseByteMs is double firstByte)
        {
            double baseline = RequestForwardedMs ?? OverheadMs ?? 0;
            ttftMs = Math.Max(0, firstByte - baseline);
        }

        byte[]? responseBytes = ResponseBuffer.ToArrayOrNull();

        return new CaptureRecord(
            StartedAt: _startedAtUtc.ToString("o"),
            Backend: decision.Backend?.Name ?? decision.RequestedName ?? "",
            TagsJson: decision.Tags.Length == 0
                ? null
                : JsonSerializer.Serialize(decision.Tags, CaptureJsonContext.Default.StringArray),
            Method: context.Request.Method,
            Path: decision.ForwardPath.Value + context.Request.QueryString.Value,
            Format: "raw",
            StatusCode: context.Response.HasStarted || Error is null ? context.Response.StatusCode : null,
            Error: Error,
            Streamed: streamed,
            DurationMs: durationMs,
            TtftMs: ttftMs,
            VesselOverheadMs: OverheadMs,
            RequestHeadersJson: HeaderRedactor.ToRedactedJson(context.Request.Headers),
            ResponseHeadersJson: context.Response.HasStarted || Error is null
                ? HeaderRedactor.ToRedactedJson(context.Response.Headers)
                : null,
            RequestBody: RequestBuffer.ToArrayOrNull(),
            ResponseBody: streamed ? null : responseBytes,
            ResponseRaw: streamed ? responseBytes : null,
            Truncated: RequestBuffer.Truncated || ResponseBuffer.Truncated);
    }

    /// <summary>
    /// Wire-level streamed heuristic (no parsing exists until Phase 2): SSE or NDJSON
    /// content type, parameters ignored.
    /// </summary>
    private static bool IsStreamedContentType(string? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        ReadOnlySpan<char> mediaType = contentType.AsSpan();
        int semicolon = mediaType.IndexOf(';');
        if (semicolon >= 0)
        {
            mediaType = mediaType[..semicolon];
        }

        mediaType = mediaType.Trim();
        return mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-ndjson", StringComparison.OrdinalIgnoreCase);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string[]>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
public sealed partial class CaptureJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
