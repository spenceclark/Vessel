using Vessel.Capture;

namespace Vessel.Formats;

/// <summary>
/// A <see cref="CaptureRecord"/> plus the normalized fields the enricher extracted (D1).
/// The store writes these columns; <see cref="ReassembledResponse"/>, when present, is the
/// Vessel-synthesized non-streamed document that replaces <c>response_body</c> for a
/// streamed row (the raw chunk stream stays in <c>response_raw</c>).
/// </summary>
public sealed record EnrichedRecord(
    CaptureRecord Record,
    string Format,
    string? Model,
    double? TokPerSec,
    long? TokensIn,
    long? TokensOut,
    long? TokensCachedRead,
    long? TokensCachedWrite,
    bool TokensEstimated,
    string? StopReason,
    string? WarningsJson,
    byte[]? ReassembledResponse,
    string? PromptText,
    string? ResponseText);
