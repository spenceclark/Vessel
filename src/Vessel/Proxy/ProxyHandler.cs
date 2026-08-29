using System.Net;
using System.Text.Json.Nodes;
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
    private readonly CaptureEvents _captureEvents;
    private readonly BackendHealthTracker _backendHealthTracker;
    private readonly RequestModelSnifferService _modelSniffer;
    private readonly CurrentSession _currentSession;
    private readonly ConfigStore _configStore;
    private readonly HttpMessageInvoker _invoker;
    private readonly ILogger<ProxyHandler> _logger;

    public ProxyHandler(
        IHttpForwarder forwarder, BackendRegistry registry, CaptureChannel captureChannel,
        CaptureEvents captureEvents, BackendHealthTracker backendHealthTracker,
        RequestModelSnifferService modelSniffer, CurrentSession currentSession,
        ConfigStore configStore, ILogger<ProxyHandler> logger)
    {
        _forwarder = forwarder;
        _registry = registry;
        _captureChannel = captureChannel;
        _captureEvents = captureEvents;
        _backendHealthTracker = backendHealthTracker;
        _modelSniffer = modelSniffer;
        _currentSession = currentSession;
        _configStore = configStore;
        _logger = logger;

        // One shared invoker for all backends, per YARP direct-forwarding guidance.
        // AutomaticDecompression stays off: if the client asked for gzip, the client
        // gets gzip — Vessel never decodes or re-encodes. Config-independent — built once.
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
    }

    public async Task Handle(HttpContext context)
    {
        // D7/R02 — exactly one snapshot read per request, used for *both* routing and this
        // request's limits/timeouts. Previously the limits came from ConfigStore.Current and
        // routing from the registry's own independently-refreshed view, so a PUT landing
        // between them could apply revision N's timeouts to revision N+1's backend.
        ConfigSnapshot snapshot = _configStore.Snapshot;
        VesselConfig config = snapshot.Config;
        BackendSet backends = _registry.Resolve(snapshot);
        long maxBodyBytes = CaptureBudget.MaxWireBytes(config);
        var requestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromSeconds(config.Timeouts.ActivitySeconds),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var capture = new CaptureContext(maxBodyBytes, _currentSession.Id, _captureEvents, _modelSniffer);
        context.Items[CaptureContext.ItemsKey] = capture;

        // The response tee: bytes written to the client first, then buffered. The feature
        // wrap covers both the Stream and PipeWriter write paths.
        IHttpResponseBodyFeature priorBody = context.Features.Get<IHttpResponseBodyFeature>()!;
        context.Features.Set<IHttpResponseBodyFeature>(
            new StreamResponseBodyFeature(new ResponseTeeStream(priorBody.Stream, capture, context), priorBody));

        RouteDecision decision = RouteResolver.Resolve(context.Request.Path, context.Request.Headers, backends);

        // D5 — as early as backend/tags are known, before any forwarding work begins. This
        // allocates the seq *and* registers it as in-flight in one step (I0b(1)).
        long? replayOf = TryParseReplayOf(context.Request.Headers);
        capture.SetReplayOf(replayOf);
        capture.Register(
            context.Request.Method,
            decision.ForwardPath.Value + context.Request.QueryString.Value,
            decision.Backend?.Name ?? decision.RequestedName ?? "", decision.Tags, replayOf);

        // R26/I1 — everything after registration runs inside the guarded span, so "registered →
        // terminal" holds for *every* exit. Request preparation used to sit above this try: with
        // injectStreamUsage it reads the request body, and a client that disconnected mid-upload
        // threw straight past the finalizer — no record, no `completed`, and the seq stranded in
        // the authoritative active set forever (the viewer showed it running).
        try
        {
            // The request tee: request bytes observed as YARP reads them upstream. For
            // injectStreamUsage-eligible backends the body is prepared specially (D11);
            // otherwise it is teed as-is.
            try
            {
                await PrepareRequestBody(context, capture, decision, maxBodyBytes);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // The client went away while we were reading its body. Same policy as every
                // other client-side failure below: mark the row and fall through to the
                // finalizer, which still enqueues it and still ends the lifecycle. Nothing can
                // be forwarded and nobody is listening for a response.
                capture.Error = VesselErrors.ClientDisconnect;
                _logger.LogDebug("client disconnected while reading the request body: {Error}", ex.Message);
                return;
            }

            if (decision.Backend is null)
            {
                capture.Error = VesselErrors.UnknownBackend;
                capture.ResponseAuthoredByVessel = true; // R08 — the body below is Vessel's own
                await VesselErrors.Write(
                    context, StatusCodes.Status404NotFound, VesselErrors.UnknownBackend,
                    $"unknown backend '{decision.RequestedName}'", backends.Names);
                return;
            }

            context.Items[RouteDecision.ItemsKey] = decision;

            ForwarderError error = await _forwarder.SendAsync(
                context, decision.Backend.BaseUrl, _invoker, requestConfig, VesselTransformer.Instance);

            if (error != ForwarderError.None)
            {
                await HandleForwarderError(context, capture, decision.Backend, error);
            }
        }
        finally
        {
            // On an error path YARP may never read the request body (e.g. the connection
            // was refused). Drain it now so failed rows still carry model + prompt_text
            // from the request side (D2/F4) — request bodies are single JSON documents,
            // so this is bounded by the capture cap, not a stream.
            await CaptureUnreadRequestBody(context, capture);

            // Fire-and-forget from the request's point of view; redaction happens
            // inside BuildRecord, so plaintext secrets never reach the channel.
            //
            // R25/H0b(3) — "registered → terminal" is owned here, at the registration site.
            // `Register` (above) put this seq in the hub's active set; the writer normally
            // removes it via `completed`. But when admission is closed (the writer gave up),
            // the capture is dropped and the writer will never emit `completed` for it, so the
            // seq would leak in the active set and the viewer would show it as forever-running.
            // Complete it here so every registered request reaches a terminal transition,
            // regardless of capture health — forwarding already succeeded either way.
            CaptureRecord record = capture.BuildRecord(context, decision);
            _backendHealthTracker.Observe(record);
            if (!_captureChannel.Enqueue(record))
            {
                _captureEvents.Completed(capture.Seq, null);
            }
        }
    }

    private static async Task CaptureUnreadRequestBody(HttpContext context, CaptureContext capture)
    {
        if (capture.RequestForwardedMs is not null)
        {
            return; // the body was already read (and captured) on the forward path
        }

        try
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Client went away or the body is no longer readable — nothing more to capture.
        }
    }

    private const string ChatCompletionsSuffix = "/chat/completions";

    public const string ReplayHeader = "X-Vessel-Replay-Of";

    private static long? TryParseReplayOf(IHeaderDictionary headers) =>
        long.TryParse(headers[ReplayHeader].FirstOrDefault(), out long replayOf) && replayOf > 0 ? replayOf : null;

    /// <summary>
    /// Installs the request-body tee. For an injectStreamUsage-eligible backend (D11), the
    /// body is buffered first so <c>stream_options.include_usage</c> can be added to a
    /// streamed request; the stored copy is always the client's original bytes, and any
    /// disqualifying condition forwards the body unmodified.
    /// </summary>
    private async Task PrepareRequestBody(HttpContext context, CaptureContext capture, RouteDecision decision, long maxBodyBytes)
    {
        if (decision.Backend is not { InjectStreamUsage: true }
            || !decision.ForwardPath.Value!.EndsWith(ChatCompletionsSuffix, StringComparison.Ordinal)
            || context.Request.Headers.ContentEncoding.Count > 0)
        {
            context.Request.Body = new RequestTeeStream(context.Request.Body, capture);
            return;
        }

        (byte[] head, bool overCap, Stream? remainder) = await ReadCapped(context.Request.Body, maxBodyBytes);

        if (overCap)
        {
            // Too large to safely rewrite — forward unmodified, but keep the tee so the
            // body is still captured (and truncated at the cap) as YARP reads it.
            var forward = new ConcatStream(head, remainder!);
            context.Request.Body = new RequestTeeStream(forward, capture);
            return;
        }

        if (TryInjectUsage(head, out byte[] modified))
        {
            // Capture the client's original bytes; forward the modified body.
            capture.RequestBuffer.Append(head);
            capture.MarkRequestForwarded();
            capture.EmitRequestReadyIfParseable();
            capture.UsageInjected = true;
            context.Request.Body = new MemoryStream(modified, writable: false);
            context.Request.ContentLength = modified.Length;
        }
        else
        {
            // Not eligible (non-JSON, not streamed, already has stream_options): tee the
            // buffered original so capture and forwarding both see the same bytes.
            context.Request.Body = new RequestTeeStream(
                new MemoryStream(head, writable: false), capture);
        }
    }

    /// <summary>
    /// Reads up to <paramref name="cap"/> bytes into memory and probes for one more, so the
    /// caller can tell an exactly/under-cap body (fully buffered) from an over-cap one
    /// (buffered head + the untouched remainder of the stream).
    /// </summary>
    private static async Task<(byte[] Head, bool OverCap, Stream? Remainder)> ReadCapped(Stream body, long cap)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        while (buffer.Length < cap)
        {
            int want = (int)Math.Min(chunk.Length, cap - buffer.Length);
            int read = await body.ReadAsync(chunk.AsMemory(0, want));
            if (read == 0)
            {
                return (buffer.ToArray(), false, null);
            }

            buffer.Write(chunk, 0, read);
        }

        // At the cap: one more byte decides whether the body overflowed it.
        byte[] one = new byte[1];
        if (await body.ReadAsync(one.AsMemory(0, 1)) == 0)
        {
            return (buffer.ToArray(), false, null);
        }

        byte[] head = new byte[buffer.Length + 1];
        buffer.GetBuffer().AsSpan(0, (int)buffer.Length).CopyTo(head);
        head[^1] = one[0];
        return (head, true, body);
    }

    /// <summary>
    /// Adds <c>stream_options: {include_usage: true}</c> when <paramref name="body"/> is a
    /// JSON object with <c>"stream": true</c> and no existing <c>stream_options</c>. Any
    /// parse failure or disqualifying shape returns false — forward unmodified, no warning.
    /// </summary>
    private static bool TryInjectUsage(byte[] body, out byte[] modified)
    {
        modified = body;
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(body) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (obj is null
            || obj["stream"] is not JsonValue streamValue
            || !streamValue.TryGetValue(out bool stream) || !stream
            || obj.ContainsKey("stream_options"))
        {
            return false;
        }

        obj["stream_options"] = new JsonObject { ["include_usage"] = true };
        modified = System.Text.Encoding.UTF8.GetBytes(obj.ToJsonString());
        return true;
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
                capture.Error = error.ToString();
                // R08 — what lands in the response buffer from here on is Vessel's, not the
                // backend's; enrichment must not read it as a completion.
                capture.ResponseAuthoredByVessel = true;
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
                capture.Error = error.ToString();
                capture.ResponseAuthoredByVessel = true;
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
