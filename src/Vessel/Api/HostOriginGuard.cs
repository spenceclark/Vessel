using Microsoft.AspNetCore.Http;
using Vessel.Config;

namespace Vessel.Api;

/// <summary>
/// D03 — Vessel accepted an arbitrary Host header on its control plane, and applied no
/// same-origin check to mutating config/deletion requests. Neither is a completed
/// DNS-rebinding exploit by itself, but together they mean a page in the browser's
/// current tab (or one reached via rebinding) could read prompts or mutate config just by
/// getting the user's browser to issue a same-machine request. Two cheap layers, scoped to
/// the control plane only — ordinary proxied SDK traffic (the catch-all route) is never
/// touched by either check:
/// <list type="bullet">
/// <item><c>/vessel/*</c> (both the API and the embedded UI) requires a Host that is
/// loopback or the configured <c>listen</c> host — this is what stops an attacker page
/// from reaching Vessel's control surface via DNS rebinding to begin with.</item>
/// <item>Mutating <c>/vessel/api/*</c> requests (PUT/POST/DELETE) additionally require
/// same-origin, using <c>Sec-Fetch-Site</c> where a browser sends it and falling back to
/// an <c>Origin</c> match otherwise — this is what stops a same-origin-Host but
/// cross-site page (a third-party site the rebinding attacker doesn't control) from
/// issuing a state-changing request via the victim's browser.</item>
/// </list>
/// A request with neither header (curl, scripts, the test suite, non-browser tooling in
/// general) is not a browser at all and is let through by the origin check — this is not
/// UI authentication, just closing the specific browser-reachable gap D03 identified.
/// </summary>
public static class HostOriginGuard
{
    private static readonly string[] _loopbackHostNames = ["localhost"];

    /// <summary>Host allowlist for every <c>/vessel/*</c> request.</summary>
    public static bool IsAllowedHost(HttpContext context, ConfigStore configStore)
    {
        string host = context.Request.Host.Host; // port already stripped; brackets stripped for IPv6
        if (host.Length == 0)
        {
            return false;
        }

        if (_loopbackHostNames.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? hostAddress))
        {
            if (System.Net.IPAddress.IsLoopback(hostAddress))
            {
                return true;
            }

            if (ConfigLoader.TryParseListen(configStore.Current.Listen, out System.Net.IPAddress configuredAddress, out _)
                && hostAddress.Equals(configuredAddress))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Same-origin requirement for mutating <c>/vessel/api/*</c> requests only — read-only
    /// requests and everything outside <c>/vessel/api</c> (the static UI shell, and every
    /// proxied route) are unaffected.
    /// </summary>
    public static bool IsAllowedMutationOrigin(HttpContext context)
    {
        HttpRequest request = context.Request;
        bool mutating = HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPost(request.Method)
            || HttpMethods.IsDelete(request.Method);
        if (!mutating || !request.Path.StartsWithSegments("/vessel/api"))
        {
            return true;
        }

        string? secFetchSite = request.Headers["Sec-Fetch-Site"];
        if (!string.IsNullOrEmpty(secFetchSite))
        {
            return secFetchSite is "same-origin" or "none";
        }

        string? origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
        {
            // No Sec-Fetch-Site and no Origin at all: not a browser request (curl, the
            // SDK's HTTP client, scripts) — nothing here to check same-origin against.
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri)
            && string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
