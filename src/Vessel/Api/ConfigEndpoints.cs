using System.Text.Json;
using Vessel.Config;

namespace Vessel.Api;

/// <summary>D7 — <c>GET/PUT /vessel/api/config</c>: the live-apply config editor's backend half.</summary>
public static class ConfigEndpoints
{
    public static async Task Get(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<ConfigStore>();
        var result = new ConfigGetResult(store.Current, store.PendingRestart);
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, result, ApiJsonContext.Default.ConfigGetResult, context.RequestAborted);
    }

    /// <summary>
    /// Validates with the same rules as startup (via <see cref="ConfigStore.Apply"/>) — a
    /// bad config gets a 400 with the human validation message and nothing is applied or
    /// persisted. A valid one is written to <c>vessel.json</c>, swapped in immediately for
    /// new requests, and reported back with which fields (if any) still need a restart.
    /// </summary>
    public static async Task Put(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<ConfigStore>();

        VesselConfig? candidate;
        try
        {
            candidate = await JsonSerializer.DeserializeAsync(
                context.Request.Body, ConfigJsonContext.Default.VesselConfig, context.RequestAborted);
        }
        catch (JsonException ex)
        {
            await VesselErrors.Write(context, StatusCodes.Status400BadRequest, VesselErrors.InvalidConfig, $"malformed JSON: {ex.Message}");
            return;
        }

        if (candidate is null)
        {
            await VesselErrors.Write(context, StatusCodes.Status400BadRequest, VesselErrors.InvalidConfig, "empty request body");
            return;
        }

        try
        {
            ConfigApplyResult result = store.Apply(candidate);
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                context.Response.Body, result, ApiJsonContext.Default.ConfigApplyResult, context.RequestAborted);
        }
        catch (ConfigException ex)
        {
            await VesselErrors.Write(context, StatusCodes.Status400BadRequest, VesselErrors.InvalidConfig, ex.Message);
        }
    }
}
