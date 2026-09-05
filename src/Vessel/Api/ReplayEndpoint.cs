using System.Security.Cryptography;
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

    /// <summary>#48 — one fan may vary at most this many ways; the one-axis guard itself lives in the UI.</summary>
    public const int MaxVariations = 8;

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

        requested ??= new ReplayRequest(null, null, null);
        // D1 — a plain single replay is a fan of one: today's shape becomes one variation, and
        // from here there is exactly one code path.
        ReplayVariation[] variations = requested.Variations
            ?? [new ReplayVariation(requested.Backend, requested.Model, null)];

        if (variations.Length == 0)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "a replay needs at least one variation");
            return;
        }

        if (variations.Length > MaxVariations)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                $"a replay fan is limited to {MaxVariations} variations");
            return;
        }

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
        string group = NewGroupId();

        // D3 — validation is atomic across the fan: every variation is composed before any is
        // dispatched, so a bad one never leaves a half-fired fan behind.
        var plans = new List<ReplayPlan>(variations.Length);
        for (int index = 0; index < variations.Length; index++)
        {
            if (!TryCompose(detail, backends, snapshot, variations[index], group, out ReplayPlan? plan, out ReplayError? error))
            {
                await VesselErrors.Write(
                    context, error!.Status, error.Code, error.Message, error.Backends, index);
                return;
            }

            plans.Add(plan!);
        }

        context.RequestServices.GetRequiredService<ReplayExecutor>().Start(plans);

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, new ReplayAccepted(group, plans.Count),
            ApiJsonContext.Default.ReplayAccepted, context.RequestAborted);
    }

    private static string NewGroupId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>
    /// Composes one variation into a dispatchable plan, or the error that rejects the whole
    /// fan. Order matters: model, then the merge patch, then the dialect fix-up — so a patched
    /// <c>max_tokens</c> still gets renamed for the target dialect and recorded as such (D3).
    /// </summary>
    private static bool TryCompose(
        RequestDetail detail, BackendSet backends, ConfigSnapshot snapshot,
        ReplayVariation variation, string group,
        out ReplayPlan? plan, out ReplayError? error)
    {
        plan = null;
        error = null;
        string backendName = string.IsNullOrWhiteSpace(variation.Backend) ? detail.Backend : variation.Backend;
        ResolvedBackend? backend = backends.Find(backendName);
        if (backend is null)
        {
            error = new ReplayError(
                StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                $"unknown replay backend '{backendName}'", backends.Names);
            return false;
        }

        if (!IsCompatible(detail, backend, variation.Model is not null || variation.Params is not null))
        {
            error = new ReplayError(
                StatusCodes.Status400BadRequest, VesselErrors.FormatMismatch,
                $"{detail.Format} cannot be replayed to backend '{backend.Name}' ({backend.Type})");
            return false;
        }

        if (detail.Truncated)
        {
            error = new ReplayError(
                StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "the captured request body was truncated and cannot be replayed safely");
            return false;
        }

        if (!TryGetBody(detail.RequestBody, out byte[] body, out string? bodyError))
        {
            error = new ReplayError(StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest, bodyError!);
            return false;
        }

        if (variation.Model is not null && !TryOverrideModel(body, variation.Model, out body))
        {
            error = new ReplayError(
                StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "the captured request body is not a JSON object, so its model cannot be overridden");
            return false;
        }

        string? patchJson = null;
        if (variation.Params is not null)
        {
            if (variation.Params is not JsonObject patch)
            {
                error = new ReplayError(
                    StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                    "replay params must be a JSON object");
                return false;
            }

            // One way to do each thing: the model is the variation's own field, never a
            // patch member, so there is no precedence rule to remember.
            if (patch.ContainsKey("model"))
            {
                error = new ReplayError(
                    StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                    "replay params must not set 'model'; use the variation's model field");
                return false;
            }

            if (!TryApplyMergePatch(body, patch, out body))
            {
                error = new ReplayError(
                    StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                    "the captured request body is not a JSON object, so params cannot be applied");
                return false;
            }

            patchJson = patch.ToJsonString();
        }

        if (TryApplyDialectFixup(detail.Format, backend.Type, backend.BaseUrl, body, out byte[] fixedUpBody, out string? fixupId))
        {
            body = fixedUpBody;
        }

        if (!TryBuildAuth(detail, backend, out KeyValuePair<string, string>[] authHeaders, out string? missingEnv))
        {
            error = new ReplayError(
                StatusCodes.Status400BadRequest, VesselErrors.MissingReplayAuth,
                $"replay requires environment variable '{missingEnv}' on the Vessel process");
            return false;
        }

        plan = new ReplayPlan(
            detail.Id, backend.Name, detail.Method, detail.Path, body,
            Header(detail.RequestHeaders, "Content-Type"), Header(detail.RequestHeaders, "Accept"), detail.Tags,
            authHeaders, TimeSpan.FromSeconds(snapshot.Config.Timeouts.ActivitySeconds), fixupId, group, patchJson);
        return true;
    }

    /// <summary>
    /// RFC 7396 JSON Merge Patch: objects merge recursively, <c>null</c> deletes, arrays and
    /// scalars replace. Recursive because a sampler under Ollama's <c>options</c> must not
    /// clobber the sibling keys the original set there — which is also why the endpoint can
    /// stay format-agnostic about where a parameter lives (D3).
    /// </summary>
    public static bool TryApplyMergePatch(byte[] body, JsonObject patch, out byte[] rewritten)
    {
        rewritten = body;
        JsonObject? target;
        try
        {
            target = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (target is null)
        {
            return false;
        }

        Merge(target, patch);
        rewritten = Encoding.UTF8.GetBytes(target.ToJsonString());
        return true;
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach ((string key, JsonNode? value) in patch)
        {
            if (value is null)
            {
                target.Remove(key);
            }
            else if (value is JsonObject nested)
            {
                // An object patch always merges, even where the target has no object to merge
                // into: cloning it wholesale would carry its nested nulls into the outgoing
                // body as literal values, when a null is a deletion and deleting an absent
                // key is a no-op. Applying {"options":{"seed":null,"temperature":0.2}} to {}
                // must produce {"options":{"temperature":0.2}}, not a null seed on the wire.
                if (target.TryGetPropertyValue(key, out JsonNode? existing) && existing is JsonObject existingObject)
                {
                    Merge(existingObject, nested);
                }
                else
                {
                    var fresh = new JsonObject();
                    Merge(fresh, nested);
                    target[key] = fresh;
                }
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
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

    /// <summary>
    /// #28 — the "current" spelling is OpenAI's own Chat Completions API; every other
    /// <c>openai-chat</c>-compatible target (Ollama, local/self-hosted OpenAI-compatible
    /// servers, Gemini's compat endpoint, a same-backend <c>auto</c> target) is "legacy" and
    /// keeps the old spelling, since most of them don't yet speak the new one. Type "openai"
    /// alone isn't proof of that — only an exact, case-insensitive host match is.
    /// </summary>
    public static bool IsCurrentOpenAiDialect(string backendType, string baseUrl) =>
        string.Equals(backendType, "openai", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
        && string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);

    public const string CurrentFixupId = "openai-chat:max_tokens->max_completion_tokens";
    public const string LegacyFixupId = "openai-chat:max_completion_tokens->max_tokens";

    /// <summary>
    /// #28 — applies the one mechanical <c>openai-chat</c> parameter rename this replay's
    /// target dialect calls for, if any. Renames, never copies: the composed replay carries
    /// only the target spelling. A no-op (returns false) whenever the source member is
    /// absent, the target member is already present, or the body isn't a JSON object —
    /// callers keep the original bytes. <paramref name="fixupId"/> is the applied rule's id,
    /// stamped onto <see cref="ProxyHandler.ReplayFixupsHeader"/> so Compare can render it
    /// "(auto)" from a recorded fact rather than by guessing. Public, like
    /// <see cref="ReplayExecutor.BuildTarget"/>, so this pure transform has a direct unit
    /// test instead of one that has to dispatch a real replay to prove it.
    /// </summary>
    public static bool TryApplyDialectFixup(
        string format, string backendType, string baseUrl, byte[] body,
        out byte[] rewritten, out string? fixupId)
    {
        rewritten = body;
        fixupId = null;
        if (format != "openai-chat")
        {
            return false;
        }

        bool current = IsCurrentOpenAiDialect(backendType, baseUrl);
        string source = current ? "max_tokens" : "max_completion_tokens";
        string target = current ? "max_completion_tokens" : "max_tokens";

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (obj is null || !obj.TryGetPropertyValue(source, out JsonNode? value) || obj.ContainsKey(target))
        {
            return false;
        }

        obj.Remove(source);
        obj[target] = value;
        rewritten = Encoding.UTF8.GetBytes(obj.ToJsonString());
        fixupId = current ? CurrentFixupId : LegacyFixupId;
        return true;
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

    /// <summary>
    /// #48 — whether replaying to this backend reattaches a key, i.e. whether a fan aimed at it
    /// spends money. The single definition: <see cref="TryBuildAuth"/> gates on it and
    /// <c>/status</c> publishes it, so the dialog's paid-call count cannot drift from what
    /// replay actually does.
    /// </summary>
    public static bool RequiresAuth(ResolvedBackend backend)
    {
        string type = backend.Type.ToLowerInvariant();
        bool isLoopback = Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;
        return !string.IsNullOrWhiteSpace(backend.AuthEnv)
            || (type is "anthropic" or "openai" or "auto") && !isLoopback;
    }

    private static bool TryBuildAuth(
        RequestDetail detail, ResolvedBackend backend,
        out KeyValuePair<string, string>[] headers, out string? missingEnv)
    {
        headers = [];
        missingEnv = null;
        string type = backend.Type.ToLowerInvariant();
        if (!RequiresAuth(backend))
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

/// <summary>
/// #48 — one variation of a fan. <c>Params</c> is an RFC 7396 merge patch applied to the
/// decoded original body, which is what keeps the endpoint format-agnostic: it never needs to
/// know whether a sampler lives at the top level or under Ollama's <c>options</c>.
/// </summary>
public sealed record ReplayVariation(string? Backend, string? Model, JsonNode? Params);

/// <summary>The single-replay shape (<c>backend</c>/<c>model</c>) stays accepted as a fan of one.</summary>
public sealed record ReplayRequest(string? Backend, string? Model, ReplayVariation[]? Variations);

public sealed record ReplayAccepted(string ReplayGroup, int Count);

internal sealed record ReplayError(int Status, string Code, string Message, string[]? Backends = null);
