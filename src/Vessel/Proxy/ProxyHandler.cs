using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Vessel.Api;
using Vessel.Capture;
using Vessel.Config;
using Yarp.ReverseProxy.Forwarder;

namespace Vessel.Proxy;

/// <summary>
/// The catch-all endpoint: resolve the backend, forward via YARP direct forwarding,
/// map forwarder errors to marked Vessel error responses. Every request that reaches
/// this handler is captured — tees observe the bodies, and a record is enqueued for
/// the background writer no matter how the request ends.
/// </summary>
public sealed class ProxyHandler
{
    private readonly IHttpForwarder _forwarder;
    private readonly BackendRegistry _registry;
    private readonly CaptureChannel _captureChannel;
    private readonly HttpMessageInvoker _invoker;
    private readonly ForwarderRequestConfig _requestConfig;
    private readonly long _maxBodyBytes;
    private readonly ILogger<ProxyHandler> _logger;

    public ProxyHandler(
        IHttpForwarder forwarder, BackendRegistry registry, CaptureChannel captureChannel,
        VesselConfig config, ILogger<ProxyHandler> logger)
    {
        _forwarder = forwarder;
        _registry = registry;
        _captureChannel = captureChannel;
        _maxBodyBytes = (long)config.Capture.MaxBodyMb * 1024 * 1024;
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
        var capture = new CaptureContext(_maxBodyBytes);
        context.Items[CaptureContext.ItemsKey] = capture;

        // The tees: request bytes observed as YARP reads them upstream; response bytes
        // written to the client first, then buffered. The feature wrap covers both the
        // Stream and PipeWriter write paths.
        context.Request.Body = new RequestTeeStream(context.Request.Body, capture);
        IHttpResponseBodyFeature priorBody = context.Features.Get<IHttpResponseBodyFeature>()!;
        context.Features.Set<IHttpResponseBodyFeature>(
            new StreamResponseBodyFeature(new ResponseTeeStream(priorBody.Stream, capture), priorBody));

        RouteDecision decision = RouteResolver.Resolve(context.Request.Path, context.Request.Headers, _registry);

        try
        {
            if (decision.Backend is null)
            {
                capture.Error = VesselErrors.UnknownBackend;
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
                await HandleForwarderError(context, capture, decision.Backend, error);
            }
        }
        finally
        {
            // Fire-and-forget from the request's point of view; redaction happens
            // inside BuildRecord, so plaintext secrets never reach the channel.
            _captureChannel.Enqueue(capture.BuildRecord(context, decision));
        }
    }

    private async Task HandleForwarderError(
        HttpContext context, CaptureContext capture, ResolvedBackend backend, ForwarderError error)
    {
        if (context.Response.HasStarted)
        {
            // Mid-stream failure: YARP has already aborted the client connection;
            // nothing else is possible.
            capture.Error = IsClientSide(error) ? VesselErrors.ClientDisconnect : error.ToString();
            _logger.LogDebug("forwarding to '{Backend}' failed mid-response: {Error}", backend.Name, error);
            return;
        }

        if (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away — nobody's listening for a response.
            capture.Error = VesselErrors.ClientDisconnect;
            _logger.LogDebug("client disconnected during request to '{Backend}': {Error}", backend.Name, error);
            return;
        }

        switch (error)
        {
            case ForwarderError.RequestTimedOut:
                capture.Error = VesselErrors.UpstreamTimeout;
                await VesselErrors.Write(
                    context, StatusCodes.Status504GatewayTimeout, VesselErrors.UpstreamTimeout,
                    $"backend '{backend.Name}' ({backend.BaseUrl}) timed out");
                break;

            case ForwarderError.RequestCanceled:
            case ForwarderError.RequestBodyCanceled:
            case ForwarderError.RequestBodyClient:
                capture.Error = VesselErrors.ClientDisconnect;
                _logger.LogDebug("client-side failure during request to '{Backend}': {Error}", backend.Name, error);
                break;

            default:
                capture.Error = VesselErrors.UpstreamUnreachable;
                await VesselErrors.Write(
                    context, StatusCodes.Status502BadGateway, VesselErrors.UpstreamUnreachable,
                    $"backend '{backend.Name}' ({backend.BaseUrl}) is unreachable: {error}");
                break;
        }
    }

    private static bool IsClientSide(ForwarderError error) => error is
        ForwarderError.RequestCanceled or
        ForwarderError.RequestBodyCanceled or
        ForwarderError.RequestBodyClient or
        ForwarderError.ResponseBodyCanceled or
        ForwarderError.ResponseBodyClient;
}
