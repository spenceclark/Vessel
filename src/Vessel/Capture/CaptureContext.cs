using System.Diagnostics;
using System.Text.Json;
using Vessel.Proxy;

namespace Vessel.Capture;

/// <summary>
/// Per-request capture state: one monotonic clock, the timing marks, and the two body
/// buffers. Created at handler entry, stashed in <c>HttpContext.Items</c> so the
/// transformer can stamp the overhead mark, and turned into a <see cref="CaptureRecord"/>
/// once the response is complete. <see cref="Seq"/> is a process-lifetime counter — the
/// correlation key the SSE feed uses before a request has a DB id (D5); <see cref="SessionId"/>
/// is captured from <c>CurrentSession</c> once, here, so a record enqueued before a session
/// reset keeps the session it started in even if it doesn't flush until after (D4).
/// </summary>
public sealed class CaptureContext
{
    /// <summary>Key under which the context is stashed in <c>HttpContext.Items</c>.</summary>
    public const string ItemsKey = "Vessel.CaptureContext";

    private static long _seqCounter;

    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly CaptureEvents? _events;

    public CaptureContext(long maxBodyBytes, long sessionId, CaptureEvents? events = null)
    {
        Seq = Interlocked.Increment(ref _seqCounter);
        SessionId = sessionId;
        _events = events;
        RequestBuffer = new CaptureBuffer(maxBodyBytes);
        ResponseBuffer = new CaptureBuffer(maxBodyBytes);
    }

    /// <summary>Process-lifetime sequence number (D5) — assigned once per request, never reused.</summary>
    public long Seq { get; }

    public long SessionId { get; }

    public string StartedAtIso => _startedAtUtc.ToString("o");

    public CaptureBuffer RequestBuffer { get; }

    public CaptureBuffer ResponseBuffer { get; }

    /// <summary>End of request transformation — the outbound request is fully prepared (§4.2 vessel_overhead_ms).</summary>
    public double? OverheadMs { get; private set; }

    /// <summary>Last read (data or EOF) from the request-body tee — request fully forwarded upstream.</summary>
    public double? RequestForwardedMs { get; private set; }

    /// <summary>Entry of the first write on the response tee — first response body byte from upstream.</summary>
    public double? FirstResponseByteMs { get; private set; }

    /// <summary>Last write on the response tee — last response body byte (§4.2 tok/s denominator, carry-in).</summary>
    public double? LastResponseByteMs { get; private set; }

    /// <summary>Proxy-level failure code, e.g. unknown_backend / upstream_unreachable / client_disconnect.</summary>
    public string? Error { get; set; }

    /// <summary>Set by <c>ProxyHandler</c> when it injected <c>stream_options.include_usage</c> (D11).</summary>
    public bool UsageInjected { get; set; }

    private double ElapsedMs => Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

    public void MarkOverhead() => OverheadMs ??= ElapsedMs;

    public void MarkRequestForwarded() => RequestForwardedMs = ElapsedMs;

    /// <summary>
    /// Stamps the first-byte mark once. Returns true only on the call that actually set
    /// it, so the tee knows whether this write is the one to check for a live
    /// <c>first_token</c> emit (D5) rather than re-checking on every subsequent chunk.
    /// </summary>
    public bool MarkFirstResponseByte()
    {
        if (FirstResponseByteMs is not null)
        {
            return false;
        }

        FirstResponseByteMs = ElapsedMs;
        return true;
    }

    /// <summary>Overwrite semantics: every response-tee write restamps the last-byte mark.</summary>
    public void MarkLastResponseByte() => LastResponseByteMs = ElapsedMs;

    /// <summary>
    /// D5 <c>first_token</c> — call once, right after the first response byte is marked.
    /// Only streamed responses get the event; with no subscribers the hub is a no-op.
    /// </summary>
    public void EmitFirstTokenIfStreamed(string? responseContentType)
    {
        if (_events is null || FirstResponseByteMs is not double firstByte || !IsStreamedContentType(responseContentType))
        {
            return;
        }

        _events.FirstToken(Seq, ComputeTtftMs(firstByte));
    }

    private double ComputeTtftMs(double firstByteMs)
    {
        double baseline = RequestForwardedMs ?? OverheadMs ?? 0;
        return Math.Max(0, firstByteMs - baseline);
    }

    /// <summary>
    /// Builds the record on the request path — headers are redacted here, before the
    /// record ever reaches the channel. Bodies stay raw; the writer compresses.
    /// </summary>
    public CaptureRecord BuildRecord(HttpContext context, RouteDecision decision)
    {
        double durationMs = ElapsedMs;
        bool streamed = IsStreamedContentType(context.Response.ContentType);

        double? ttftMs = streamed && FirstResponseByteMs is double firstByte ? ComputeTtftMs(firstByte) : null;

        byte[]? responseBytes = ResponseBuffer.ToArrayOrNull();

        return new CaptureRecord(
            StartedAt: StartedAtIso,
            Seq: Seq,
            SessionId: SessionId,
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
            FirstResponseByteMs: FirstResponseByteMs,
            LastResponseByteMs: LastResponseByteMs,
            RequestHeadersJson: HeaderRedactor.ToRedactedJson(context.Request.Headers),
            ResponseHeadersJson: context.Response.HasStarted || Error is null
                ? HeaderRedactor.ToRedactedJson(context.Response.Headers)
                : null,
            RequestBody: RequestBuffer.ToArrayOrNull(),
            ResponseBody: streamed ? null : responseBytes,
            ResponseRaw: streamed ? responseBytes : null,
            Truncated: RequestBuffer.Truncated || ResponseBuffer.Truncated,
            UsageInjected: UsageInjected);
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
