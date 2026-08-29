namespace Vessel.Config;

/// <summary>Resolves Vessel's portable and platform-managed state locations (Phase 6 D4).</summary>
public static class ConfigPathResolver
{
    public static ResolvedPaths Resolve(string? explicitConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            return FromConfigPath(explicitConfigPath, isPortable: false);
        }

        string portablePath = Path.Combine(AppContext.BaseDirectory, "vessel.json");
        if (File.Exists(portablePath))
        {
            return FromConfigPath(portablePath, isPortable: true);
        }

        return FromConfigPath(Path.Combine(GetPlatformConfigDirectory(), "vessel.json"), isPortable: false);
    }

    private static ResolvedPaths FromConfigPath(string configPath, bool isPortable)
    {
        string fullPath = Path.GetFullPath(configPath);
        return new ResolvedPaths(fullPath, Path.Combine(Path.GetDirectoryName(fullPath)!, "vessel.db"), isPortable);
    }

    private static string GetPlatformConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vessel-proxy");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "vessel-proxy");
        }

        string xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
        return Path.Combine(
            string.IsNullOrWhiteSpace(xdgConfigHome) ? Path.Combine(home, ".config") : xdgConfigHome,
            "vessel-proxy");
    }
}

public sealed record ResolvedPaths(string ConfigPath, string DatabasePath, bool IsPortable);
