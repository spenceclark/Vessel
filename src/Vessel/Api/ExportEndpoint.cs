using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Storage;

namespace Vessel.Api;

/// <summary>#24 — streamed CSV/JSONL export of exactly the current list scope.</summary>
public static class ExportEndpoint
{
    private const string Csv = "csv";
    private const string Jsonl = "jsonl";
    private static readonly byte[] _newline = [(byte)'\n'];
    private static readonly byte[] _utf8Bom = Encoding.UTF8.GetPreamble();

    public static async Task Count(HttpContext context)
    {
        RequestQuery query = ParseQuery(context);
        long count = context.RequestServices.GetRequiredService<SqliteReadStore>().CountRequests(query);
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, new ExportCountResponse(count),
            ApiJsonContext.Default.ExportCountResponse, context.RequestAborted);
    }

    public static async Task Export(HttpContext context)
    {
        string? format = NullIfEmpty(context.Request.Query["format"]);
        if (format is not (Csv or Jsonl))
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "'format' must be csv or jsonl");
            return;
        }

        string bodiesRaw = NullIfEmpty(context.Request.Query["bodies"]) ?? "none";
        ExportBodies? bodies = bodiesRaw switch
        {
            "none" => ExportBodies.None,
            "text" => ExportBodies.Text,
            "full" => ExportBodies.Full,
            _ => null,
        };
        if (bodies is null)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "'bodies' must be none, text, or full");
            return;
        }

        if (format == Csv && bodies == ExportBodies.Full)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "bodies=full is available only for jsonl exports");
            return;
        }

        RequestQuery query = ParseQuery(context);
        var store = context.RequestServices.GetRequiredService<SqliteReadStore>();
        var configStore = context.RequestServices.GetRequiredService<ConfigStore>();

        context.Response.ContentType = format == Csv
            ? "text/csv; charset=utf-8"
            : "application/x-ndjson; charset=utf-8";
        context.Response.Headers.ContentDisposition =
            $"attachment; filename=\"{BuildFilename(store, query.SessionId, format)}\"";
        context.Response.Headers.CacheControl = "no-store";

        await context.Response.StartAsync(context.RequestAborted);
        if (format == Csv)
        {
            await WriteCsv(
                context, store, query, bodies.Value,
                CaptureBudget.MaxDecodedBytes(configStore.Current));
        }
        else
        {
            await WriteJsonl(
                context, store, query, bodies.Value,
                CaptureBudget.MaxDecodedBytes(configStore.Current));
        }
    }

    private static async Task WriteCsv(
        HttpContext context, SqliteReadStore store, RequestQuery query,
        ExportBodies bodies, long maxDecodedBytes)
    {
        // #24 live-use follow-up: Excel on Windows treats BOM-less CSV as ANSI. The
        // preamble is CSV-only; JSONL must begin directly with its first JSON object.
        await context.Response.Body.WriteAsync(_utf8Bom, context.RequestAborted);
        string header =
            "id,started_at,session_id,backend,tags,method,path,format,model,status_code,error,streamed,replay_of," +
            "duration_ms,ttft_ms,vessel_overhead_ms,tok_per_sec,tokens_in,tokens_out,tokens_cached_read," +
            "tokens_cached_write,tokens_estimated,stop_reason,warnings,truncated," +
            "replay_group,replay_patch,score" +
            (bodies == ExportBodies.Text ? ",prompt_text,response_text" : "") + "\r\n";
        await context.Response.WriteAsync(header, context.RequestAborted);

        int rowsSinceFlush = 0;
        foreach (ExportRow row in store.EnumerateExport(query, bodies, maxDecodedBytes))
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            await context.Response.WriteAsync(ToCsvLine(row, bodies), context.RequestAborted);
            if (++rowsSinceFlush == 64)
            {
                await context.Response.Body.FlushAsync(context.RequestAborted);
                rowsSinceFlush = 0;
            }
        }

        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteJsonl(
        HttpContext context, SqliteReadStore store, RequestQuery query,
        ExportBodies bodies, long maxDecodedBytes)
    {
        int rowsSinceFlush = 0;
        foreach (ExportRow row in store.EnumerateExport(query, bodies, maxDecodedBytes))
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            JsonObject json = JsonSerializer.SerializeToNode(
                row.Summary, ApiJsonContext.Default.Summary)!.AsObject();
            if (bodies >= ExportBodies.Text)
            {
                json["promptText"] = row.PromptText;
                json["responseText"] = row.ResponseText;
            }

            if (bodies == ExportBodies.Full)
            {
                json["requestHeaders"] = row.RequestHeaders?.DeepClone();
                json["responseHeaders"] = row.ResponseHeaders?.DeepClone();
                json["requestBody"] = JsonSerializer.SerializeToNode(row.RequestBody, ApiJsonContext.Default.BodyPayload);
                json["responseBody"] = JsonSerializer.SerializeToNode(row.ResponseBody, ApiJsonContext.Default.BodyPayload);
                json["responseRaw"] = JsonSerializer.SerializeToNode(row.ResponseRaw, ApiJsonContext.Default.BodyPayload);
            }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(json, ExportJsonContext.Default.JsonObject);
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
            await context.Response.Body.WriteAsync(_newline, context.RequestAborted);
            if (++rowsSinceFlush == 64)
            {
                await context.Response.Body.FlushAsync(context.RequestAborted);
                rowsSinceFlush = 0;
            }
        }

        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static string ToCsvLine(ExportRow row, ExportBodies bodies)
    {
        Summary s = row.Summary;
        string[] fields =
        [
            s.Id.ToString(CultureInfo.InvariantCulture), s.StartedAt,
            Invariant(s.SessionId), s.Backend,
            JsonSerializer.Serialize(s.Tags, CaptureJsonContext.Default.StringArray), s.Method,
            s.Path, s.Format, s.Model ?? "", Invariant(s.StatusCode), s.Error ?? "",
            s.Streamed ? "true" : "false", Invariant(s.ReplayOf), Invariant(s.DurationMs),
            Invariant(s.TtftMs), Invariant(s.VesselOverheadMs), Invariant(s.TokPerSec),
            Invariant(s.TokensIn), Invariant(s.TokensOut), Invariant(s.TokensCachedRead),
            Invariant(s.TokensCachedWrite), s.TokensEstimated ? "true" : "false",
            s.StopReason ?? "",
            JsonSerializer.Serialize(s.Warnings, CaptureJsonContext.Default.StringArray),
            s.Truncated ? "true" : "false",
            s.ReplayGroup ?? "", s.ReplayPatch ?? "", Invariant(s.Score),
        ];

        IEnumerable<string> allFields = bodies == ExportBodies.Text
            ? fields.Append(row.PromptText ?? "").Append(row.ResponseText ?? "")
            : fields;
        return string.Join(',', allFields.Select(EscapeCsv)) + "\r\n";
    }

    private static string EscapeCsv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static string Invariant<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture) ?? "";

    private static string BuildFilename(SqliteReadStore store, long? sessionId, string extension)
    {
        string scope = sessionId is null
            ? "all"
            : store.GetSessionName(sessionId.Value) is { Length: > 0 } name
                ? name
                : $"session-{sessionId.Value}";
        var safe = new StringBuilder(scope.Length);
        foreach (char c in scope.ToLowerInvariant())
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        string slug = safe.ToString().Trim('-');
        if (slug.Length == 0) slug = "session";
        return $"vessel-{slug}-{DateTime.UtcNow:yyyy-MM-dd}.{extension}";
    }

    internal static RequestQuery ParseQuery(HttpContext context) => new(
        SessionId: context.Request.Query.TryGetValue("session", out var sessionRaw)
            && long.TryParse(sessionRaw, out long session) ? session : null,
        Q: NullIfEmpty(context.Request.Query["q"]),
        Backend: NullIfEmpty(context.Request.Query["backend"]),
        Model: NullIfEmpty(context.Request.Query["model"]),
        // `format` names the file format on this route, so the existing request-format
        // filter travels as `requestFormat` to avoid an impossible duplicate key.
        Format: NullIfEmpty(context.Request.Query["requestFormat"]),
        Tag: NullIfEmpty(context.Request.Query["tag"]),
        Status: NullIfEmpty(context.Request.Query["status"]),
        Warned: context.Request.Query["warned"] == "1");

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(JsonObject))]
internal sealed partial class ExportJsonContext : JsonSerializerContext;
