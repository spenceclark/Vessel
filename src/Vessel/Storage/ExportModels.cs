using System.Text.Json.Nodes;

namespace Vessel.Storage;

/// <summary>#24 — the list-compatible predicate shared by paging, export, and export count.</summary>
public sealed record RequestQuery(
    long? SessionId = null,
    string? Q = null,
    string? Backend = null,
    string? Model = null,
    string? Format = null,
    string? Tag = null,
    string? Status = null,
    bool Warned = false);

public enum ExportBodies
{
    None,
    Text,
    Full,
}

/// <summary>
/// One export row. Only one row is materialized at a time; optional fields are populated
/// according to the requested body tier.
/// </summary>
public sealed record ExportRow(
    Summary Summary,
    string? PromptText,
    string? ResponseText,
    JsonNode? RequestHeaders,
    JsonNode? ResponseHeaders,
    BodyPayload? RequestBody,
    BodyPayload? ResponseBody,
    BodyPayload? ResponseRaw);

public sealed record ExportCountResponse(long Count);
