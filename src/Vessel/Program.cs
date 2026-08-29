using Vessel;
using Vessel.Config;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net.Sockets;

string? configPath = null;
bool noOpen = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--config")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("vessel: --config requires a path");
            return 1;
        }

        configPath = args[i + 1];
        i++;
    }
    else if (args[i] == "--no-open")
    {
        noOpen = true;
    }
    else if (args[i] == "--version")
    {
        Console.WriteLine(Vessel.Api.StatusEndpoint.Version);
        return 0;
    }
    else if (args[i] == "--help" || args[i] == "-h")
    {
        ResolvedPaths helpPaths = ConfigPathResolver.Resolve(configPath);
        VesselConfig helpConfig;
        try
        {
            helpConfig = File.Exists(helpPaths.ConfigPath)
                ? ConfigLoader.LoadOrCreate(helpPaths.ConfigPath).Config
                : ConfigLoader.CreateDefault();
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine($"vessel: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"""
            vessel — local-first LLM traffic observability proxy

            Usage: vessel [--config <path>] [--no-open] [--help] [--version]
              --config <path>  Use this config file (always takes precedence).
              --no-open         Do not open the UI browser on first run.
              --help, -h        Print this help.
              --version         Print version and commit.

            Config location: --config, then vessel.json beside the executable when it exists
            (portable mode), otherwise the platform config directory.
            Resolved config: {helpPaths.ConfigPath}
            Resolved data:   {helpPaths.DatabasePath}
            UI:              http://{helpConfig.Listen}/vessel/
            """);
        return 0;
    }
    else
    {
        Console.Error.WriteLine($"vessel: unknown argument '{args[i]}' (usage: vessel [--config <path>] [--no-open] [--version])");
        return 1;
    }
}

ResolvedPaths paths = ConfigPathResolver.Resolve(configPath);

VesselConfig config;
bool created;
try
{
    (config, created) = ConfigLoader.LoadOrCreate(paths.ConfigPath);
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"vessel: {ex.Message}");
    return 1;
}

WebApplication app = VesselApp.Build(config, paths.DatabasePath, paths.ConfigPath);
try
{
    await app.StartAsync();
    app.RecordBoundListen();
}
catch (Exception ex) when (IsPortInUse(ex))
{
    Console.Error.WriteLine($"vessel: listen address {config.Listen} is already in use — is Vessel already running? Change \"listen\" in {Path.GetFileName(paths.ConfigPath)} or pass --config.");
    await app.DisposeAsync();
    return 1;
}
catch (Exception ex) when (IsDatabaseFailure(ex))
{
    Console.Error.WriteLine($"vessel: database '{paths.DatabasePath}' is locked or cannot be opened — another Vessel instance may be using it, or the directory may not be writable.");
    await app.DisposeAsync();
    return 1;
}

string listen = app.ListenAddress();
var registry = app.Services.GetRequiredService<Vessel.Proxy.BackendRegistry>();
string backendSummary = string.Join(", ", registry.All
    .OrderByDescending(b => b.IsDefault)
    .Select(b => b.IsDefault ? $"{b.Name} (default, {b.BaseUrl})" : $"{b.Name} ({b.BaseUrl})"));

ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vessel");
startupLogger.LogInformation("Vessel {Version} listening on {Listen} - backends: {Backends}",
    Vessel.Api.StatusEndpoint.Version, listen, backendSummary);

foreach (string warning in ConfigLoader.CollectWarnings(config))
{
    startupLogger.LogWarning("config: {Warning}", warning);
}

Uri boundUri = new(listen);
bool nonLoopback = System.Net.IPAddress.TryParse(boundUri.Host, out System.Net.IPAddress? boundAddress)
    && !System.Net.IPAddress.IsLoopback(boundAddress);
if (nonLoopback)
{
    if (ConfigLoader.IsRunningInContainer)
    {
        startupLogger.LogInformation("Vessel is listening on {Listen} inside a container", listen);
    }
    else
    {
        startupLogger.LogWarning("Vessel is listening on {Listen}; anyone on your network can read captured prompts{McpExposure}",
            listen, config.Mcp.Enabled ? ", and MCP clients can reach /vessel/mcp" : "");
    }
}

if (created)
{
    Console.WriteLine($"Created default config at {paths.ConfigPath}");
    Console.WriteLine($"Vessel listening on {listen}  ->  default backend: {registry.Default.Name} ({registry.Default.BaseUrl})");
    Console.WriteLine($"Point your client at {listen} - UI at {listen}/vessel/");

    if (!noOpen && !ConfigLoader.IsRunningInContainer)
    {
        TryOpenBrowser($"{listen}/vessel/", startupLogger);
    }
}

await app.WaitForShutdownAsync();
return 0;

static bool IsPortInUse(Exception ex) =>
    ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
    || ex.InnerException is not null && IsPortInUse(ex.InnerException)
    || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase);

static bool IsDatabaseFailure(Exception ex) =>
    ex is SqliteException
    || ex.InnerException is not null && IsDatabaseFailure(ex.InnerException);

static void TryOpenBrowser(string url, ILogger logger)
{
    try
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true }
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", url)
                : new ProcessStartInfo("xdg-open", url);
        startInfo.UseShellExecute = false;
        Process.Start(startInfo);
    }
    catch (Exception ex)
    {
        logger.LogDebug(ex, "Could not open the Vessel UI in the system browser");
    }
}
