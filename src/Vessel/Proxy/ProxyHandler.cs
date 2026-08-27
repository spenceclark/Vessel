using System.Net;
using Vessel.Api;
using Vessel.Config;
using Yarp.ReverseProxy.Forwarder;

namespace Vessel.Proxy;

/// <summary>
/// The catch-all endpoint: resolve the backend, forward via YARP direct forwarding,
/// map forwarder errors to marked Vessel error responses.
/// </summary>
public sealed class ProxyHandler
{
    private readonly IHttpForwarder _forwarder;
    private readonly BackendRegistry _registry;
    private readonly HttpMessageInvoker _invoker;
    private readonly ForwarderRequestConfig _requestConfig;
    private readonly ILogger<ProxyHandler> _logger;

    public ProxyHandler(IHttpForwarder forwarder, BackendRegistry registry, VesselConfig config, ILogger<ProxyHandler> logger)
    {
        _forwarder = forwarder;
        _registry = registry;
        _logger = logger;

        // One shared invoker for all backends, per YARP direct-forwarding guidance.
        // AutomaticDecompression stays off: if the client asked for gzip, the client
        // gets gzip — Vessel never decodes or re-encodes.
        _invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator = null,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });

        _requestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromSeconds(config.Timeouts.ActivitySeconds),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public async Task Handle(HttpContext context)
    {
        RouteDecision decision = RouteResolver.Resolve(context.Request.Path, context.Request.Headers, _registry);

        if (decision.Backend is null)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status404NotFound, VesselErrors.UnknownBackend,
                $"unknown backend '{decision.RequestedName}'", _registry.Names);
            return;
        }

        context.Items[RouteDecision.ItemsKey] = decision;

        ForwarderError error = await _forwarder.SendAsync(
            context, decision.Backend.BaseUrl, _invoker, _requestConfig, VesselTransformer.Instance);

        if (error != ForwarderError.None)
        {
            await HandleForwarderError(context, decision.Backend, error);
        }
    }

    private async Task HandleForwarderError(HttpContext context, ResolvedBackend backend, ForwarderError error)
    {
        if (context.Response.HasStarted)
        {
            // Mid-stream failure: YARP has already aborted the client connection;
            // nothing else is possible.
            _logger.LogDebug("forwarding to '{Backend}' failed mid-response: {Error}", backend.Name, error);
            return;
        }

        if (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away — nobody's listening for a response.
            _logger.LogDebug("client disconnected during request to '{Backend}': {Error}", backend.Name, error);
            return;
        }

        switch (error)
        {
            case ForwarderError.RequestTimedOut:
                await VesselErrors.Write(
                    context, StatusCodes.Status504GatewayTimeout, VesselErrors.UpstreamTimeout,
                    $"backend '{backend.Name}' ({backend.BaseUrl}) timed out");
                break;

            case ForwarderError.RequestCanceled:
            case ForwarderError.RequestBodyCanceled:
            case ForwarderError.RequestBodyClient:
                _logger.LogDebug("client-side failure during request to '{Backend}': {Error}", backend.Name, error);
                break;

            default:
                await VesselErrors.Write(
                    context, StatusCodes.Status502BadGateway, VesselErrors.UpstreamUnreachable,
                    $"backend '{backend.Name}' ({backend.BaseUrl}) is unreachable: {error}");
                break;
        }
    }
}
