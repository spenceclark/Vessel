using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Vessel.Api;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Proxy;
using Vessel.Storage;

namespace Vessel;

/// <summary>Builds the Vessel host from a validated config. Shared by Program and the integration tests.</summary>
public static class VesselApp
{
    public static WebApplication Build(VesselConfig config, string dbPath)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Vessel is silent in normal operation: framework categories at Warning,
        // Vessel's own single startup line at Information, per-request logging at Debug.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Vessel", LogLevel.Information);

        ConfigLoader.TryParseListen(config.Listen, out System.Net.IPAddress address, out int port);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Base64 images in prompts routinely exceed the 30 MB default; Vessel must
            // not be the thing that rejects them.
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Listen(address, port);
        });

        builder.Services.AddHttpForwarder();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<BackendRegistry>();
        builder.Services.AddSingleton<ProxyHandler>();
        builder.Services.AddSingleton<CaptureChannel>();
        builder.Services.AddSingleton(sp => new SqliteCaptureStore(dbPath, sp.GetRequiredService<VesselConfig>()));
        // Registered before Kestrel's own hosted service starts the listener, so the
        // database initializes (fail-fast) before any traffic is accepted.
        builder.Services.AddHostedService<CaptureWriterService>();

        var app = builder.Build();

        // Everything Vessel-owned lives under /vessel/ — mapped before the catch-all,
        // never proxied.
        app.MapGet("/vessel/api/status", (RequestDelegate)StatusEndpoint.Handle);
        app.Map("/vessel/{**rest}", (RequestDelegate)(context =>
            VesselErrors.Write(
                context, StatusCodes.Status404NotFound, VesselErrors.NotFound,
                $"no such Vessel endpoint: {context.Request.Path}")));

        app.Map("/{**catchall}", (RequestDelegate)(context =>
            context.RequestServices.GetRequiredService<ProxyHandler>().Handle(context)));

        return app;
    }

    /// <summary>The actual bound address, e.g. "http://127.0.0.1:4550" (resolves port 0 after start).</summary>
    public static string ListenAddress(this WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
}
