using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
[JsonSerializable(typeof(RequestListResponse))]
[JsonSerializable(typeof(RequestDetail))]
[JsonSerializable(typeof(StatsResponse))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SessionInfo[]))]
[JsonSerializable(typeof(CreateSessionRequest))]
[JsonSerializable(typeof(JsonNode))]
public sealed partial class ApiJsonContext : JsonSerializerContext;

/// <summary>D3 — optional <c>POST /sessions</c> request body.</summary>
public sealed record CreateSessionRequest(string? Name);
