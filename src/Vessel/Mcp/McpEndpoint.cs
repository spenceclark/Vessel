using Vessel.Api;
using Vessel.Config;

namespace Vessel.Mcp;

/// <summary>Mounting and live availability gate for Vessel's Streamable HTTP MCP endpoint.</summary>
public static class McpEndpoint
{
    /// <summary>Maps the SDK's Streamable HTTP handler inside Vessel's reserved namespace.</summary>
    public static void Map(WebApplication app) => app.MapMcp("/vessel/mcp");

    /// <summary>Default-on setting read from the current ConfigStore snapshot on every request.</summary>
    public static bool IsEnabled(ConfigStore configStore) => configStore.Current.Mcp.Enabled;

    /// <summary>Uses Vessel's ordinary marked 404 convention when MCP is switched off.</summary>
    public static Task WriteDisabled(HttpContext context) => VesselErrors.Write(
        context, StatusCodes.Status404NotFound, VesselErrors.NotFound, "MCP is disabled by config");
}
