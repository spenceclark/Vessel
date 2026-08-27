using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Vessel.Proxy;

namespace Vessel.Api;

public static class StatusEndpoint
{
    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static Task Handle(HttpContext context)
    {
        var registry = context.RequestServices.GetRequiredService<BackendRegistry>();
        var server = context.RequestServices.GetRequiredService<IServer>();
        string listen = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? "";

        var payload = new StatusPayload(
            "vessel",
            Version,
            listen,
            registry.Default.Name,
            registry.All
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .Select(b => new StatusBackend(b.Name, b.BaseUrl, b.Type, b.IsDefault))
                .ToArray());

        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(
            context.Response.Body, payload, ApiJsonContext.Default.StatusPayload, context.RequestAborted);
    }
}
