using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Proxy;
using Vessel.Storage;

namespace Vessel.Api;

/// <summary>Phase 5 replay validation and dispatch. Execution lives in <see cref="ReplayExecutor"/>.</summary>
public static class ReplayEndpoint
{
    private const string AnthropicVersion = "2023-06-01";

    public static async Task Handle(HttpContext context)
    {
        if (!long.TryParse((string?)context.Request.RouteValues["id"], out long id))
        {
            await VesselErrors.Write(context, StatusCodes.Status404NotFound, VesselErrors.NotFound, "no such request");
            return;
        }

        ReplayRequest? requested;
        try
        {
            requested = await JsonSerializer.DeserializeAsync(
                context.Request.Body, ApiJsonContext.Default.ReplayRequest, context.RequestAborted);
        }
        catch (JsonException)
        {
            await VesselErrors.Write(context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest, "replay body must be valid JSON");
            return;
        }

        requested ??= new ReplayRequest(null, null);

        var configStore = context.RequestServices.GetRequiredService<ConfigStore>();
        ConfigSnapshot snapshot = configStore.Snapshot;
        var store = context.RequestServices.GetRequiredService<SqliteReadStore>();
        RequestDetail? detail = store.GetDetail(id, CaptureBudget.MaxDecodedBytes(snapshot.Config));
        if (detail is null)
        {
            await VesselErrors.Write(context, StatusCodes.Status404NotFound, VesselErrors.NotFound, $"no such request: {id}");
            return;
        }

        var registry = context.RequestServices.GetRequiredService<BackendRegistry>();
        BackendSet backends = registry.Resolve(snapshot);
        string backendName = string.IsNullOrWhiteSpace(requested.Backend) ? detail.Backend : requested.Backend;
        ResolvedBackend? backend = backends.Find(backendName);
        if (backend is null)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                $"unknown replay backend '{backendName}'", backends.Names);
            return;
        }

        if (!IsCompatible(detail, backend, requested.Model is not null))
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.FormatMismatch,
                $"{detail.Format} cannot be replayed to backend '{backend.Name}' ({backend.Type})");
            return;
        }

        if (detail.Truncated)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "the captured request body was truncated and cannot be replayed safely");
            return;
        }

        if (!TryGetBody(detail.RequestBody, out byte[] body, out string? bodyError))
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                bodyError!);
            return;
        }

        if (requested.Model is not null && !TryOverrideModel(body, requested.Model, out body))
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "the captured request body is not a JSON object, so its model cannot be overridden");
            return;
        }

        if (!TryBuildAuth(detail, backend, out KeyValuePair<string, string>[] authHeaders, out string? missingEnv))
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.MissingReplayAuth,
                $"replay requires environment variable '{missingEnv}' on the Vessel process");
            return;
        }

        string? contentType = Header(detail.RequestHeaders, "Content-Type");
        string? accept = Header(detail.RequestHeaders, "Accept");
        var plan = new ReplayPlan(
            id, backend.Name, detail.Method, detail.Path, body, contentType, accept, detail.Tags,
            authHeaders, TimeSpan.FromSeconds(snapshot.Config.Timeouts.ActivitySeconds));
        context.RequestServices.GetRequiredService<ReplayExecutor>().Start(plan);
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync("{}", context.RequestAborted);
    }

    private static bool IsCompatible(RequestDetail detail, ResolvedBackend target, bool modelOverride)
    {
        string type = target.Type.ToLowerInvariant();
        bool sameBackend = string.Equals(detail.Backend, target.Name, StringComparison.OrdinalIgnoreCase);
        return detail.Format switch
        {
            "openai-chat" => type is "openai" or "ollama" || type == "auto" && sameBackend,
            "openai-responses" => type == "openai" || type == "auto" && sameBackend,
            "anthropic-messages" => type is "anthropic" or "ollama" || type == "auto" && sameBackend,
            "ollama-chat" or "ollama-generate" => type == "ollama" || type == "auto" && sameBackend,
            "raw" => sameBackend && !modelOverride,
            _ => false,
        };
    }

    private static bool TryGetBody(BodyPayload? payload, out byte[] body, out string? error)
    {
        body = [];
        error = null;
        if (payload is null)
        {
            return true;
        }

        if (payload.DecodeTruncated)
        {
            error = "the captured request body exceeded the decode limit and cannot be replayed safely";
            return false;
        }

        if (payload.DecodeFailed)
        {
            error = "the captured request body could not be content-decoded and cannot be replayed safely";
            return false;
        }

        if (payload.Text is not null)
        {
            body = Encoding.UTF8.GetBytes(payload.Text);
            return true;
        }

        if (payload.Base64 is not null)
        {
            try
            {
                body = Convert.FromBase64String(payload.Base64);
                return true;
            }
            catch (FormatException)
            {
                error = "the captured request body is not valid base64 and cannot be replayed safely";
                return false;
            }
        }

        return true;
    }

    private static bool TryOverrideModel(byte[] body, string model, out byte[] rewritten)
    {
        rewritten = body;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj)
            {
                return false;
            }

            obj["model"] = model;
            rewritten = Encoding.UTF8.GetBytes(obj.ToJsonString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryBuildAuth(
        RequestDetail detail, ResolvedBackend backend,
        out KeyValuePair<string, string>[] headers, out string? missingEnv)
    {
        headers = [];
        missingEnv = null;
        string type = backend.Type.ToLowerInvariant();
        bool isLoopback = Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;
        bool needsAuth = !string.IsNullOrWhiteSpace(backend.AuthEnv)
            || (type is "anthropic" or "openai" or "auto") && !isLoopback;
        if (!needsAuth)
        {
            return true;
        }

        string env = backend.AuthEnv ?? (type == "anthropic" ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY");
        string? value = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(value))
        {
            missingEnv = env;
            return false;
        }

        if (type == "anthropic")
        {
            headers =
            [
                new KeyValuePair<string, string>("x-api-key", value),
                new KeyValuePair<string, string>("anthropic-version", Header(detail.RequestHeaders, "anthropic-version") ?? AnthropicVersion),
            ];
        }
        else
        {
            headers = [new KeyValuePair<string, string>("Authorization", $"Bearer {value}")];
        }

        return true;
    }

    private static string? Header(System.Text.Json.Nodes.JsonNode? headers, string name)
    {
        if (headers is not JsonObject obj)
        {
            return null;
        }

        foreach ((string key, JsonNode? value) in obj)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)
                && value is JsonArray values
                && values[0]?.GetValue<string>() is string first)
            {
                return first;
            }
        }

        return null;
    }
}

public sealed record ReplayRequest(string? Backend, string? Model);
