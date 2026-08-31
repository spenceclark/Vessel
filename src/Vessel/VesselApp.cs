using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.Protocol;
using Vessel.Mcp;
using Vessel.Api;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Formats;
using Vessel.Proxy;
using Vessel.Storage;

namespace Vessel;

/// <summary>Builds the Vessel host from a validated config. Shared by Program and the integration tests.</summary>
public static class VesselApp
{
    /// <param name="firstRun">
    /// #11 — true when this process created <paramref name="configPath"/>. Gates the
    /// one-shot default-backend probe, and is reported on <c>/vessel/api/status</c> so the
    /// UI can lead with the backend picker instead of leaving a cloud-only user to discover
    /// the dead Ollama default through a 502.
    /// </param>
    public static WebApplication Build(VesselConfig config, string dbPath, string configPath, bool firstRun = false)
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
        builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation
        {
            Name = "vessel",
            Version = StatusEndpoint.Version,
        })
        .WithHttpTransport(options => options.Stateless = true)
        .WithTools<McpTools>();
        builder.Services.AddSingleton(sp => new ConfigStore(config, configPath));
        builder.Services.AddSingleton<BackendRegistry>();
        builder.Services.AddSingleton<ProxyHandler>();
        builder.Services.AddSingleton<ReplayExecutor>();
        builder.Services.AddSingleton<CaptureChannel>();
        builder.Services.AddSingleton<CaptureEvents>();
        builder.Services.AddSingleton<RequestModelSnifferService>();
        builder.Services.AddSingleton<CurrentSession>();
        builder.Services.AddSingleton(sp => new FormatEnricher(
            sp.GetRequiredService<ConfigStore>(), sp.GetService<ILogger<FormatEnricher>>()));
        builder.Services.AddSingleton(sp => new SqliteCaptureStore(dbPath, sp.GetRequiredService<ConfigStore>()));
        builder.Services.AddSingleton<ICaptureStore>(sp => sp.GetRequiredService<SqliteCaptureStore>());
        builder.Services.AddSingleton(sp => new SqliteReadStore(dbPath));
        builder.Services.AddSingleton<BackendHealthTracker>();
        builder.Services.AddSingleton(new FirstRunState(firstRun));
        // Registered before Kestrel's own hosted service starts the listener, so the
        // database initializes (fail-fast) before any traffic is accepted.
        builder.Services.AddHostedService<CaptureWriterService>();
        // Same singleton ProxyHandler enqueues into (registered above) — the factory just
        // resolves it, so StartAsync/StopAsync run against that exact instance.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RequestModelSnifferService>());
        // No-ops entirely on any run but the first, so this is never a background health
        // check — BackendHealthTracker stays the only (passive) source of the health dots.
        builder.Services.AddHostedService<FirstRunProbeService>();

        var app = builder.Build();

        // D03 — Host allowlist + mutating-request same-origin check, scoped to /vessel/*
        // only. Every proxied route (the catch-all below) is untouched: this must never
        // be able to break SDK traffic, only Vessel's own control plane.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/vessel"))
            {
                await next(context);
                return;
            }

            // R03 — defense in depth alongside the frontend's own resource policy
            // (MessageView never emits a live src/href for captured content): even if a
            // future rendering path forgot that rule, the browser itself refuses the
            // request. `data:`/`blob:` stay allowed for img-src — that's the *embedded*
            // preview path (R18), which is same-document and makes no request at all.
            // UI routes only; proxied backend responses (the catch-all below) never get
            // this header rewritten onto them.
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "img-src 'self' data: blob:; " +
                "style-src 'self' 'unsafe-inline'; " +
                "font-src 'self' data:; " +
                "connect-src 'self'; " +
                "script-src 'self'; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'";

            var configStore = context.RequestServices.GetRequiredService<ConfigStore>();
            if (!HostOriginGuard.IsAllowedHost(context, configStore))
            {
                await VesselErrors.Write(
                    context, StatusCodes.Status403Forbidden, VesselErrors.ForbiddenHost,
                    $"Host '{context.Request.Host}' is not an allowed Vessel host");
                return;
            }

            if (!HostOriginGuard.IsAllowedMutationOrigin(context))
            {
                await VesselErrors.Write(
                    context, StatusCodes.Status403Forbidden, VesselErrors.ForbiddenOrigin,
                    "cross-origin request rejected");
                return;
            }

            if (context.Request.Path.StartsWithSegments("/vessel/mcp")
                && !McpEndpoint.IsEnabled(configStore))
            {
                await McpEndpoint.WriteDisabled(context);
                return;
            }

            await next(context);
        });

        // Everything Vessel-owned lives under /vessel/ — mapped before the catch-all,
        // never proxied (D7).
        McpEndpoint.Map(app);
        app.MapGet("/vessel/api/status", (RequestDelegate)StatusEndpoint.Handle);
        app.MapGet("/vessel/api/active", (RequestDelegate)ActiveRequestsEndpoint.Handle);
        app.MapGet("/vessel/api/requests", (RequestDelegate)RequestsEndpoints.List);
        app.MapDelete("/vessel/api/requests", (RequestDelegate)RequestsEndpoints.Delete);
        app.MapGet("/vessel/api/requests/facets", (RequestDelegate)FacetsEndpoint.Handle);
        app.MapGet("/vessel/api/requests/{id:long}", (RequestDelegate)RequestsEndpoints.Detail);
        app.MapGet("/vessel/api/requests/{id:long}/replays", (RequestDelegate)RequestsEndpoints.Replays);
        app.MapPost("/vessel/api/requests/{id:long}/replay", (RequestDelegate)ReplayEndpoint.Handle);
        app.MapGet("/vessel/api/stats", (RequestDelegate)StatsEndpoint.Handle);
        app.MapGet("/vessel/api/sessions", (RequestDelegate)SessionsEndpoints.List);
        app.MapPost("/vessel/api/sessions", (RequestDelegate)SessionsEndpoints.Create);
        app.MapDelete("/vessel/api/sessions/{id:long}", (RequestDelegate)SessionsEndpoints.Delete);
        app.MapGet("/vessel/api/config", (RequestDelegate)ConfigEndpoints.Get);
        app.MapPut("/vessel/api/config", (RequestDelegate)ConfigEndpoints.Put);
        app.MapGet("/vessel/api/events", (RequestDelegate)EventsEndpoint.Handle);
        app.Map("/vessel/api/{**rest}", (RequestDelegate)(context =>
            VesselErrors.Write(
                context, StatusCodes.Status404NotFound, VesselErrors.NotFound,
                $"no such Vessel API endpoint: {context.Request.Path}")));
        app.Map("/vessel", (RequestDelegate)StaticUi.Handle);
        app.Map("/vessel/{**rest}", (RequestDelegate)StaticUi.Handle);

        // D5 — OAuth discovery and appspecific well-known paths, reserved as control
        // plane. Never proxied, never captured. Mapped before the proxy catch-all.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/.well-known/oauth-authorization-server", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/.well-known/oauth-protected-resource", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/.well-known/appspecific/", StringComparison.OrdinalIgnoreCase))
            {
                await WellKnownEndpoints.HandleWellKnown(context);
                return;
            }

            if (path == "/favicon.ico")
            {
                await WellKnownEndpoints.HandleFavicon(context);
                return;
            }

            await next(context);
        });

        app.Map("/{**catchall}", (RequestDelegate)(context =>
            context.RequestServices.GetRequiredService<ProxyHandler>().Handle(context)));

        return app;
    }

    /// <summary>The actual bound address, e.g. "http://127.0.0.1:4550" (resolves port 0 after start).</summary>
    public static string ListenAddress(this WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    /// <summary>
    /// R16 — call once, after <c>StartAsync</c>, so <see cref="ConfigStore"/> knows the
    /// address Kestrel is actually bound to (which may differ from the configured
    /// <c>listen</c> when the port was 0). Must run after the listener is up.
    /// </summary>
    public static void RecordBoundListen(this WebApplication app)
    {
        var uri = new Uri(app.ListenAddress());
        app.Services.GetRequiredService<ConfigStore>().RecordBoundListen(System.Net.IPAddress.Parse(uri.Host), uri.Port);
    }
}
