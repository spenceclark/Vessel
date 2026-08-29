using System.Text.Json;
using Vessel.Config;
using Xunit;

namespace Vessel.Tests;

public class ConfigLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vessel-tests-").FullName;

    private string PathFor(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void MissingFile_CreatesDefaultConfig()
    {
        string path = PathFor("vessel.json");

        (VesselConfig config, bool created) = ConfigLoader.LoadOrCreate(path);

        Assert.True(created);
        Assert.True(File.Exists(path));
        Assert.Equal("127.0.0.1:4550", config.Listen);
        Assert.Equal("ollama", config.DefaultBackend);
        Assert.Equal("http://localhost:11434", config.Backends["ollama"].BaseUrl);
        Assert.Equal("ollama", config.Backends["ollama"].Type);
        Assert.Equal(["ollama"], config.Backends.Keys);
        Assert.Equal(1800, config.Timeouts.ActivitySeconds);
        Assert.Equal(10_000, config.Retention.MaxRequests);
        Assert.Equal(500, config.Retention.MaxDbSizeMb);
        Assert.Equal(32, config.Capture.MaxBodyMb);

        (VesselConfig reloaded, bool createdAgain) = ConfigLoader.LoadOrCreate(path);
        Assert.False(createdAgain);
        Assert.Equal(config.Listen, reloaded.Listen);
    }

    [Fact]
    public void UnknownProperties_SurviveLoadSaveRoundTrip()
    {
        // A phase-0 binary must not destroy settings written by a later version.
        string path = PathFor("vessel.json");
        File.WriteAllText(path, """
            {
              "listen": "127.0.0.1:4550",
              "defaultBackend": "ollama",
              "backends": {
                "ollama": { "baseUrl": "http://localhost:11434", "type": "ollama", "injectStreamUsage": true }
              },
              "timeouts": { "activitySeconds": 60, "futureTimeout": 5 },
              "retention": { "maxRequests": 5000 }
            }
            """);

        (VesselConfig config, _) = ConfigLoader.LoadOrCreate(path);
        ConfigLoader.Save(path, config);

        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = saved.RootElement;
        Assert.Equal(5000, root.GetProperty("retention").GetProperty("maxRequests").GetInt32());
        Assert.True(root.GetProperty("backends").GetProperty("ollama").GetProperty("injectStreamUsage").GetBoolean());
        Assert.Equal(5, root.GetProperty("timeouts").GetProperty("futureTimeout").GetInt32());
        Assert.Equal(60, root.GetProperty("timeouts").GetProperty("activitySeconds").GetInt32());
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        string path = PathFor("vessel.json");
        File.WriteAllText(path, "{ not json");

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadOrCreate(path));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void DefaultBackendNotConfigured_Throws()
    {
        string path = PathFor("vessel.json");
        File.WriteAllText(path, """
            {
              "defaultBackend": "nope",
              "backends": { "ollama": { "baseUrl": "http://localhost:11434" } }
            }
            """);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadOrCreate(path));
        Assert.Contains("defaultBackend 'nope'", ex.Message);
    }

    [Fact]
    public void NoBackends_Throws()
    {
        string path = PathFor("vessel.json");
        File.WriteAllText(path, """{ "defaultBackend": "ollama", "backends": {} }""");

        Assert.Throws<ConfigException>(() => ConfigLoader.LoadOrCreate(path));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    [InlineData("localhost:11434")]
    public void InvalidBaseUrl_Throws(string baseUrl)
    {
        string path = PathFor("vessel.json");
        File.WriteAllText(path, $$"""
            {
              "defaultBackend": "ollama",
              "backends": { "ollama": { "baseUrl": "{{baseUrl}}" } }
            }
            """);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadOrCreate(path));
        Assert.Contains("baseUrl", ex.Message);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("127.0.0.1")]
    [InlineData(":4550")]
    [InlineData("127.0.0.1:notaport")]
    public void InvalidListen_Throws(string listen)
    {
        string path = PathFor("vessel.json");
        File.WriteAllText(path, $$"""
            {
              "listen": "{{listen}}",
              "defaultBackend": "ollama",
              "backends": { "ollama": { "baseUrl": "http://localhost:11434" } }
            }
            """);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadOrCreate(path));
        Assert.Contains("listen", ex.Message);
    }

    // C13: retention/capture settings load, and absent sections get the defaults.
    [Fact]
    public void RetentionAndCaptureSections_LoadWithPartialDefaults()
    {
        string path = PathFor("vessel.json");
        File.WriteAllText(path, """
            {
              "defaultBackend": "ollama",
              "backends": { "ollama": { "baseUrl": "http://localhost:11434" } },
              "retention": { "maxRequests": 42 },
              "capture": { "maxBodyMb": 8 }
            }
            """);

        (VesselConfig config, _) = ConfigLoader.LoadOrCreate(path);
        Assert.Equal(42, config.Retention.MaxRequests);
        Assert.Equal(500, config.Retention.MaxDbSizeMb); // absent → default
        Assert.Equal(8, config.Capture.MaxBodyMb);
    }

    [Theory]
    [InlineData("""  "retention": { "maxRequests": 0 }  """, "retention.maxRequests")]
    [InlineData("""  "retention": { "maxDbSizeMb": -1 }  """, "retention.maxDbSizeMb")]
    [InlineData("""  "capture": { "maxBodyMb": 0 }  """, "capture.maxBodyMb")]
    public void NonPositiveRetentionOrCaptureValues_Throw(string section, string expectedInMessage)
    {
        string path = PathFor("vessel.json");
        File.WriteAllText(path, $$"""
            {
              "defaultBackend": "ollama",
              "backends": { "ollama": { "baseUrl": "http://localhost:11434" } },
              {{section}}
            }
            """);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadOrCreate(path));
        Assert.Contains(expectedInMessage, ex.Message);
    }

    // R15: a null-shaped section deserializes fine despite the non-nullable C# declaration
    // (JSON `null` overrides the property default) — Validate must reject it with a
    // ConfigException, not let the null reach a member access and NullReferenceException.
    [Theory]
    [InlineData(
        """{ "defaultBackend": "ollama", "backends": null }""",
        "backends")]
    [InlineData(
        """{ "defaultBackend": "ollama", "backends": { "ollama": null } }""",
        "backend 'ollama' is null")]
    [InlineData(
        """{ "defaultBackend": "ollama", "backends": { "ollama": { "baseUrl": "http://localhost:11434" } }, "retention": null }""",
        "retention is null")]
    [InlineData(
        """{ "defaultBackend": "ollama", "backends": { "ollama": { "baseUrl": "http://localhost:11434" } }, "capture": null }""",
        "capture is null")]
    [InlineData(
        """{ "defaultBackend": "ollama", "backends": { "ollama": { "baseUrl": "http://localhost:11434" } }, "warnings": null }""",
        "warnings is null")]
    [InlineData(
        """{ "defaultBackend": "ollama", "backends": { "ollama": { "baseUrl": "http://localhost:11434" } }, "timeouts": null }""",
        "timeouts is null")]
    [InlineData(
        """{ "defaultBackend": "ollama", "backends": { "ollama": { "baseUrl": "http://localhost:11434" } }, "listen": null }""",
        "listen")]
    public void NullSection_ThrowsConfigException(string body, string expectedInMessage)
    {
        string path = PathFor("vessel.json");
        File.WriteAllText(path, body);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadOrCreate(path));
        Assert.Contains(expectedInMessage, ex.Message);
    }

    // R21: a permission-denied save must never destroy the last valid config file. Windows
    // rejects replacing a readonly destination; POSIX allows that rename when the directory
    // is writable, so its equivalent denial is a non-writable containing directory.
    [Fact]
    public void Save_PermissionDenied_ThrowsAndLeavesOriginalFileIntact()
    {
        string path = PathFor("vessel.json");
        VesselConfig original = CreateDefault();
        ConfigLoader.Save(path, original);
        string originalContent = File.ReadAllText(path);

        UnixFileMode? originalDirectoryMode = null;
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        else
        {
            originalDirectoryMode = File.GetUnixFileMode(_dir);
            File.SetUnixFileMode(_dir, originalDirectoryMode.Value &
                ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }

        try
        {
            VesselConfig changed = CreateDefault();
            changed.DefaultBackend = "changed";
            changed.Backends["changed"] = new BackendConfig { BaseUrl = "http://localhost:9" };

            Assert.Throws<ConfigException>(() => ConfigLoader.Save(path, changed));

            Assert.Equal(originalContent, File.ReadAllText(path));

            // No orphaned temp file left behind in the directory.
            string[] leftovers = Directory.GetFiles(_dir, ".vessel.json.tmp-*");
            Assert.Empty(leftovers);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            else if (originalDirectoryMode is not null)
            {
                File.SetUnixFileMode(_dir, originalDirectoryMode.Value);
            }
        }
    }

    [Fact]
    public void Save_Succeeds_NoLeftoverTempFile()
    {
        string path = PathFor("vessel.json");
        ConfigLoader.Save(path, CreateDefault());

        string[] entries = Directory.GetFiles(_dir);
        Assert.Single(entries);
        Assert.Equal(path, entries[0]);
    }

    private static VesselConfig CreateDefault() => new()
    {
        Listen = "127.0.0.1:4550",
        DefaultBackend = "ollama",
        Backends = new Dictionary<string, BackendConfig>
        {
            ["ollama"] = new() { BaseUrl = "http://localhost:11434", Type = "ollama" },
        },
    };

    [Fact]
    public void CaseInsensitiveDefaultBackendName_IsAccepted()
    {
        string path = PathFor("vessel.json");
        File.WriteAllText(path, """
            {
              "defaultBackend": "Ollama",
              "backends": { "ollama": { "baseUrl": "http://localhost:11434" } }
            }
            """);

        (VesselConfig config, _) = ConfigLoader.LoadOrCreate(path);
        Assert.Equal("Ollama", config.DefaultBackend);
    }
}
