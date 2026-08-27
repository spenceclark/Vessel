using Yarp.ReverseProxy.Forwarder;

namespace Vessel.Proxy;

/// <summary>
/// Forward-as-is, with exactly two deviations from a byte-for-byte copy:
/// the destination path is the route decision's forward path (prefixes stripped),
/// and every <c>X-Vessel-*</c> header is removed — Vessel's control plane, not payload.
/// Host is not restored: it comes from the backend URI (required for TLS/SNI on remote
/// APIs). Responses are untouched.
/// </summary>
public sealed class VesselTransformer : HttpTransformer
{
    public static readonly VesselTransformer Instance = new();

    private VesselTransformer()
    {
    }

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        var decision = (RouteDecision)httpContext.Items[RouteDecision.ItemsKey]!;
        proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(
            destinationPrefix, decision.ForwardPath, httpContext.Request.QueryString);

        // Host comes from the backend URI, standard reverse-proxy behavior — required
        // for TLS/SNI on remote APIs. (The base transformer copies the client's Host.)
        proxyRequest.Headers.Host = null;

        RemoveVesselHeaders(proxyRequest.Headers);
        if (proxyRequest.Content is not null)
        {
            RemoveVesselHeaders(proxyRequest.Content.Headers);
        }

        // The outbound request is now fully prepared — everything up to here is
        // Vessel's own per-request cost (§4.2 vessel_overhead_ms).
        if (httpContext.Items.TryGetValue(Capture.CaptureContext.ItemsKey, out object? item)
            && item is Capture.CaptureContext capture)
        {
            capture.MarkOverhead();
        }
    }

    private static void RemoveVesselHeaders(System.Net.Http.Headers.HttpHeaders headers)
    {
        List<string>? toRemove = null;
        foreach (KeyValuePair<string, System.Net.Http.Headers.HeaderStringValues> header in headers.NonValidated)
        {
            if (header.Key.StartsWith("X-Vessel-", StringComparison.OrdinalIgnoreCase))
            {
                (toRemove ??= []).Add(header.Key);
            }
        }

        if (toRemove is not null)
        {
            foreach (string name in toRemove)
            {
                headers.Remove(name);
            }
        }
    }
}
