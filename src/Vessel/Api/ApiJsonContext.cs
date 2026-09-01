using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vessel.Capture;
using Vessel.Storage;

namespace Vessel.Api;

/// <summary>
/// STJ source-gen for every API wire shape (Phase 0 status/error payloads plus Phase 3's
/// requests/stats/sessions). Kept as a single partial-class declaration — splitting the
/// <c>[JsonSerializable]</c> attributes across files trips a source-generator hintName
/// collision (each file re-triggers per-type file emission for shared primitives).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ErrorPayload))]
[JsonSerializable(typeof(StatusPayload))]
[JsonSerializable(typeof(McpStatus))]
[JsonSerializable(typeof(ListenSecurity))]
[JsonSerializable(typeof(SetupStatus))]
[JsonSerializable(typeof(BackendHealth))]
[JsonSerializable(typeof(ActiveRequestsPayload))]
[JsonSerializable(typeof(RequestListResponse))]
[JsonSerializable(typeof(Summary))]
[JsonSerializable(typeof(RequestDetail))]
[JsonSerializable(typeof(BodyPayload))]
[JsonSerializable(typeof(ExportCountResponse))]
[JsonSerializable(typeof(Summary[]))]
[JsonSerializable(typeof(StatsResponse))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SessionInfo[]))]
[JsonSerializable(typeof(CreateSessionRequest))]
[JsonSerializable(typeof(FacetsResponse))]
[JsonSerializable(typeof(SeriesResponse))]
[JsonSerializable(typeof(SeriesGroup))]
[JsonSerializable(typeof(SeriesPoint))]
[JsonSerializable(typeof(AggregateResponse))]
[JsonSerializable(typeof(AggregateRow))]
[JsonSerializable(typeof(ClearResponse))]
[JsonSerializable(typeof(ReplayRequest))]
[JsonSerializable(typeof(Vessel.Config.ConfigApplyResult))]
[JsonSerializable(typeof(Vessel.Config.ConfigGetResult))]
[JsonSerializable(typeof(JsonNode))]
public sealed partial class ApiJsonContext : JsonSerializerContext;

/// <summary>D3 — optional <c>POST /sessions</c> request body.</summary>
public sealed record CreateSessionRequest(string? Name);

/// <summary>
/// D6 — <c>DELETE /requests</c> response: the count deleted, for the UX toast. R23/H0a: no
/// deletion boundary here — the client purges cleared rows on the in-band <c>cleared</c> SSE
/// event, which orders correctly against completions, so the ack is UX only.
/// </summary>
public sealed record ClearResponse(int Deleted);
