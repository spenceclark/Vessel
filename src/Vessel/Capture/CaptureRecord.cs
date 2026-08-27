namespace Vessel.Capture;

/// <summary>
/// One captured request, assembled on the request path (headers already redacted) and
/// handed to the background writer over the channel. Bodies are the raw wire bytes —
/// zstd compression happens on the writer thread, never here.
/// </summary>
public sealed record CaptureRecord(
    string StartedAt,
    string Backend,
    string? TagsJson,
    string Method,
    string Path,
    string Format,
    int? StatusCode,
    string? Error,
    bool Streamed,
    double? DurationMs,
    double? TtftMs,
    double? VesselOverheadMs,
    double? FirstResponseByteMs,
    double? LastResponseByteMs,
    string RequestHeadersJson,
    string? ResponseHeadersJson,
    byte[]? RequestBody,
    byte[]? ResponseBody,
    byte[]? ResponseRaw,
    bool Truncated,
    bool UsageInjected);
