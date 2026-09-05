namespace Vessel.Capture;

/// <summary>
/// One captured request, assembled on the request path (headers already redacted) and
/// handed to the background writer over the channel. Bodies are the raw wire bytes —
/// zstd compression happens on the writer thread, never here.
/// </summary>
public sealed record CaptureRecord(
    string StartedAt,
    long Seq,
    long SessionId,
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
    bool UsageInjected,
    /// <summary>R08 — the captured response bytes are Vessel's own error body, not the backend's.</summary>
    bool ResponseAuthoredByVessel = false,
    long? ReplayOf = null,
    string? SessionName = null,
    /// <summary>#48 — the fan id shared by every child of one multi-replay, null otherwise.</summary>
    string? ReplayGroup = null,
    /// <summary>#48 — compact JSON of the merge patch this child was composed with, null when none.</summary>
    string? ReplayPatch = null);
