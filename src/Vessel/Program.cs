using Vessel;
using Vessel.Config;

string? configPath = null;
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
    else
    {
        Console.Error.WriteLine($"vessel: unknown argument '{args[i]}' (usage: vessel [--config <path>])");
        return 1;
    }
}

configPath ??= Path.Combine(AppContext.BaseDirectory, "vessel.json");

VesselConfig config;
bool created;
try
{
    (config, created) = ConfigLoader.LoadOrCreate(configPath);
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"vessel: {ex.Message}");
    return 1;
}

WebApplication app = VesselApp.Build(config);
await app.StartAsync();

string listen = app.ListenAddress();
var registry = app.Services.GetRequiredService<Vessel.Proxy.BackendRegistry>();
string backendSummary = string.Join(", ", registry.All
    .OrderByDescending(b => b.IsDefault)
    .Select(b => b.IsDefault ? $"{b.Name} (default, {b.BaseUrl})" : $"{b.Name} ({b.BaseUrl})"));

app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Vessel")
    .LogInformation("Vessel {Version} listening on {Listen} - backends: {Backends}",
        Vessel.Api.StatusEndpoint.Version, listen, backendSummary);

if (created)
{
    Console.WriteLine($"Created default config at {configPath}");
    Console.WriteLine($"Vessel listening on {listen}  ->  default backend: {registry.Default.Name} ({registry.Default.BaseUrl})");
    Console.WriteLine($"Point your client at {listen} - UI at {listen}/vessel/ (phase 3)");
}

await app.WaitForShutdownAsync();
return 0;
