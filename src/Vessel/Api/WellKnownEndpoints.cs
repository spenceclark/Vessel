namespace Vessel.Api;

/// <summary>
/// Control-plane endpoints for OAuth discovery and favicon, reserved to prevent them
/// from falling through to the proxy catch-all and polluting the capture with failures.
/// D5 marking convention: answered directly with X-Vessel-Error 404, never proxied,
/// never captured.
/// </summary>
public static class WellKnownEndpoints
{
    /// <summary>
    /// OAuth discovery probes per MCP spec: /.well-known/oauth-authorization-server*,
    /// /.well-known/oauth-protected-resource*, /.well-known/openid-configuration*.
    /// Also covers /.well-known/appspecific/* (e.g. Chrome DevTools' own
    /// com.chrome.devtools.json probe against the UI origin). Returns 404 with
    /// X-Vessel-Error marking so the client knows this is a Vessel response, not a
    /// proxied backend response. These paths are never proxied and never captured, so
    /// they don't pollute capture stats.
    /// </summary>
    public static Task HandleWellKnown(HttpContext context)
    {
        return VesselErrors.Write(
            context, StatusCodes.Status404NotFound, VesselErrors.NotFound,
            $"well-known discovery path is a control-plane surface, not proxied");
    }

    /// <summary>
    /// Serve the Vessel favicon as control-plane. This is the favicon.svg embedded
    /// in the binary and served at /favicon.ico (per browser convention). Never
    /// proxied, never captured.
    /// </summary>
    public static Task HandleFavicon(HttpContext context)
    {
        // Favicon SVG — 24x24 viewBox, Tailwind palette token colors. Served as raw SVG
        // markup (not a data: URI) so a browser requesting /favicon.ico renders it directly.
        const string favicon =
            """<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><rect x="4" y="5" width="16" height="14" rx="4" stroke="#94b8c8" stroke-width="2"/><line x1="0" y1="12" x2="4" y2="12" stroke="#94b8c8" stroke-width="2" stroke-linecap="round"/><line x1="20" y1="12" x2="24" y2="12" stroke="#94b8c8" stroke-width="2" stroke-linecap="round"/><line x1="7" y1="12" x2="17" y2="12" stroke="#2dd4bf" stroke-width="2" stroke-linecap="round"/><circle cx="12" cy="12" r="1" fill="#2dd4bf"/></svg>""";

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "image/svg+xml";
        context.Response.Headers["Cache-Control"] = "public, max-age=31536000"; // 1 year
        return context.Response.WriteAsync(favicon, context.RequestAborted);
    }
}
