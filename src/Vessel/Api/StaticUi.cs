using System.Reflection;

namespace Vessel.Api;

/// <summary>
/// D1 — serves the embedded frontend build under <c>/vessel/</c>. The dist files are
/// embedded via <c>&lt;EmbeddedResource&gt;</c> with an explicit <c>vessel-ui/</c> logical
/// name prefix (see Vessel.csproj); the index built here just strips that prefix and
/// normalizes path separators, so it doesn't depend on how MSBuild happened to spell the
/// relative-dir metadata on a given OS. When no dist was embedded (a dev binary built
/// without the frontend), every path under <c>/vessel/</c> gets a small built-in
/// placeholder — the backend must never fail to build or run because the frontend wasn't.
/// </summary>
public static class StaticUi
{
    private const string ResourcePrefix = "vessel-ui/";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _resources = new(BuildIndex);

    public static bool HasEmbeddedUi => _resources.Value.Count > 0;

    public static async Task Handle(HttpContext context)
    {
        if (!HasEmbeddedUi)
        {
            await WritePlaceholder(context);
            return;
        }

        string path = context.Request.Path.Value ?? "";
        string rel = path.Length > "/vessel".Length ? path["/vessel".Length..] : "/";
        if (rel.Length == 0 || rel == "/")
        {
            rel = "/index.html";
        }

        if (!_resources.Value.TryGetValue(rel, out string? resourceName))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using Stream? stream = typeof(StaticUi).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = ContentTypeFor(rel);
        context.Response.Headers.CacheControl = rel == "/index.html"
            ? "no-cache"
            : "public, max-age=31536000, immutable";
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static IReadOnlyDictionary<string, string> BuildIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relative = name[ResourcePrefix.Length..].Replace('\\', '/');
            map["/" + relative] = name;
        }

        return map;
    }

    private static string ContentTypeFor(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".map" => "application/json; charset=utf-8",
            _ => "application/octet-stream",
        };
    }

    private static Task WritePlaceholder(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(
            """
            <!doctype html>
            <html><head><meta charset="utf-8"><title>Vessel</title></head>
            <body style="font-family: system-ui, sans-serif; max-width: 40rem; margin: 4rem auto; line-height: 1.6;">
            <h1>Vessel UI not built into this binary</h1>
            <p>This binary was built without an embedded frontend. For development, run the
            Vite dev server against this Vessel instance:</p>
            <pre style="background:#eee; padding: 1rem;">cd frontend
            npm install
            npm run dev</pre>
            <p>The dev server proxies <code>/vessel/api</code> to this process. To get an
            embedded UI in the binary itself, run <code>dotnet publish</code> (which builds
            the frontend as part of publishing) instead of <code>dotnet build</code>.</p>
            </body></html>
            """, context.RequestAborted);
    }
}
