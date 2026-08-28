namespace Vessel.Proxy;

public enum RouteSource
{
    PathPrefix,
    Header,
    Default,
}

/// <summary>
/// The outcome of routing one request. <see cref="Backend"/> is null when the client
/// named a backend that doesn't exist (error path); <see cref="RequestedName"/> then
/// carries what they asked for, for the error message.
/// </summary>
public sealed record RouteDecision(
    ResolvedBackend? Backend,
    string? RequestedName,
    PathString ForwardPath,
    string[] Tags,
    RouteSource Source)
{
    /// <summary>Key under which the decision is stashed in <c>HttpContext.Items</c> for the transformer.</summary>
    public const string ItemsKey = "Vessel.RouteDecision";
}

public static class RouteResolver
{
    public const string BackendHeader = "X-Vessel-Backend";
    public const string TagsHeader = "X-Vessel-Tags";

    /// <summary>
    /// Pure function: (path, headers, backends) → decision. Precedence: <c>/b/{name}</c>
    /// path prefix, then <c>X-Vessel-Backend</c> header, then the default backend. Tags come
    /// from an optional <c>/t/{tags}</c> prefix (after <c>/b/</c>, or standalone) and the
    /// <c>X-Vessel-Tags</c> header.
    /// <para>
    /// R02: takes an already-resolved <see cref="BackendSet"/> rather than the registry, so
    /// the caller decides which config revision the decision belongs to and every lookup
    /// here comes from that one revision.
    /// </para>
    /// </summary>
    public static RouteDecision Resolve(PathString path, IHeaderDictionary headers, BackendSet backends)
    {
        string rest = path.Value ?? "/";
        string? requestedName = null;
        var source = RouteSource.Default;
        var tags = new List<string>();

        if (TryStripPrefix(rest, "/b/", out string backendSegment, out string afterBackend))
        {
            requestedName = backendSegment;
            source = RouteSource.PathPrefix;
            rest = afterBackend;
        }

        if (TryStripPrefix(rest, "/t/", out string tagSegment, out string afterTags))
        {
            AddTags(tags, tagSegment);
            rest = afterTags;
        }

        if (requestedName is null)
        {
            string? headerName = headers[BackendHeader].FirstOrDefault();
            if (!string.IsNullOrEmpty(headerName))
            {
                requestedName = headerName;
                source = RouteSource.Header;
            }
        }

        AddTags(tags, headers[TagsHeader].FirstOrDefault());

        var forwardPath = new PathString(rest.Length == 0 ? "/" : rest);

        if (requestedName is null)
        {
            return new RouteDecision(backends.Default, backends.Default.Name, forwardPath, tags.ToArray(), RouteSource.Default);
        }

        ResolvedBackend? backend = requestedName.Length == 0 ? null : backends.Find(requestedName);
        return new RouteDecision(backend, requestedName, forwardPath, tags.ToArray(), source);
    }

    /// <summary>
    /// If <paramref name="path"/> starts with <paramref name="prefix"/> (e.g. "/b/"),
    /// extracts the next path segment (possibly empty) and the remainder starting at '/'.
    /// </summary>
    private static bool TryStripPrefix(string path, string prefix, out string segment, out string remainder)
    {
        segment = "";
        remainder = path;

        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string afterPrefix = path[prefix.Length..];
        int slash = afterPrefix.IndexOf('/');
        if (slash < 0)
        {
            segment = afterPrefix;
            remainder = "";
        }
        else
        {
            segment = afterPrefix[..slash];
            remainder = afterPrefix[slash..];
        }

        return true;
    }

    private static void AddTags(List<string> tags, string? commaSeparated)
    {
        if (string.IsNullOrWhiteSpace(commaSeparated))
        {
            return;
        }

        foreach (string raw in commaSeparated.Split(','))
        {
            string tag = raw.Trim();
            if (tag.Length > 0 && !tags.Contains(tag, StringComparer.Ordinal))
            {
                tags.Add(tag);
            }
        }
    }
}
