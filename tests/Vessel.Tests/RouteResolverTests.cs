using Microsoft.AspNetCore.Http;
using Vessel.Config;
using Vessel.Proxy;
using Xunit;

namespace Vessel.Tests;

public class RouteResolverTests
{
    private static readonly BackendRegistry _registry = new(new VesselConfig
    {
        DefaultBackend = "ollama",
        Backends = new Dictionary<string, BackendConfig>
        {
            ["ollama"] = new() { BaseUrl = "http://localhost:11434", Type = "ollama" },
            ["openai"] = new() { BaseUrl = "https://api.openai.com", Type = "openai" },
        },
    });

    private static RouteDecision Resolve(string path, params (string Name, string Value)[] headers)
    {
        var headerDict = new HeaderDictionary();
        foreach ((string name, string value) in headers)
        {
            headerDict[name] = value;
        }

        return RouteResolver.Resolve(new PathString(path), headerDict, _registry);
    }

    [Fact]
    public void PathPrefix_RoutesAndStrips()
    {
        RouteDecision d = Resolve("/b/ollama/api/chat");
        Assert.Equal("ollama", d.Backend?.Name);
        Assert.Equal("/api/chat", d.ForwardPath.Value);
        Assert.Equal(RouteSource.PathPrefix, d.Source);
        Assert.Empty(d.Tags);
    }

    [Fact]
    public void PathPrefix_BackendNamesAreCaseInsensitive()
    {
        RouteDecision d = Resolve("/b/OLLAMA/api/chat");
        Assert.Equal("ollama", d.Backend?.Name);
        Assert.Equal("/api/chat", d.ForwardPath.Value);
    }

    [Fact]
    public void PathPrefix_UnknownBackend_IsErrorPath()
    {
        RouteDecision d = Resolve("/b/nope/api/chat");
        Assert.Null(d.Backend);
        Assert.Equal("nope", d.RequestedName);
        Assert.Equal(RouteSource.PathPrefix, d.Source);
    }

    [Theory]
    [InlineData("/b/ollama")]
    [InlineData("/b/ollama/")]
    public void PathPrefix_BareBackend_ForwardsRoot(string path)
    {
        RouteDecision d = Resolve(path);
        Assert.Equal("ollama", d.Backend?.Name);
        Assert.Equal("/", d.ForwardPath.Value);
    }

    [Fact]
    public void PathPrefix_EmptyName_IsErrorPath()
    {
        RouteDecision d = Resolve("/b/");
        Assert.Null(d.Backend);
        Assert.Equal("", d.RequestedName);
    }

    [Fact]
    public void PathPrefix_WithTags_ParsesBoth()
    {
        RouteDecision d = Resolve("/b/ollama/t/planner,run42/api/chat");
        Assert.Equal("ollama", d.Backend?.Name);
        Assert.Equal(new[] { "planner", "run42" }, d.Tags);
        Assert.Equal("/api/chat", d.ForwardPath.Value);
    }

    [Fact]
    public void TagPrefix_WithoutBackendPrefix_UsesDefaultBackend()
    {
        RouteDecision d = Resolve("/t/planner/v1/chat/completions");
        Assert.Equal("ollama", d.Backend?.Name);
        Assert.Equal(new[] { "planner" }, d.Tags);
        Assert.Equal("/v1/chat/completions", d.ForwardPath.Value);
        Assert.Equal(RouteSource.Default, d.Source);
    }

    [Fact]
    public void PathPrefix_BeatsHeader()
    {
        RouteDecision d = Resolve("/b/openai/api", ("X-Vessel-Backend", "ollama"));
        Assert.Equal("openai", d.Backend?.Name);
        Assert.Equal(RouteSource.PathPrefix, d.Source);
    }

    [Fact]
    public void PlainPath_UsesDefaultBackend()
    {
        RouteDecision d = Resolve("/api/chat");
        Assert.Equal("ollama", d.Backend?.Name);
        Assert.Equal("/api/chat", d.ForwardPath.Value);
        Assert.Equal(RouteSource.Default, d.Source);
    }

    [Fact]
    public void Header_RoutesBackend()
    {
        RouteDecision d = Resolve("/v1/chat/completions", ("X-Vessel-Backend", "openai"));
        Assert.Equal("openai", d.Backend?.Name);
        Assert.Equal("/v1/chat/completions", d.ForwardPath.Value);
        Assert.Equal(RouteSource.Header, d.Source);
    }

    [Fact]
    public void Header_UnknownBackend_IsErrorPath()
    {
        RouteDecision d = Resolve("/api/chat", ("X-Vessel-Backend", "nope"));
        Assert.Null(d.Backend);
        Assert.Equal("nope", d.RequestedName);
        Assert.Equal(RouteSource.Header, d.Source);
    }

    [Fact]
    public void Tags_FromPathAndHeader_AreMerged()
    {
        RouteDecision d = Resolve("/t/planner/api/chat", ("X-Vessel-Tags", "run42, planner"));
        Assert.Equal(new[] { "planner", "run42" }, d.Tags);
    }

    [Fact]
    public void PathNotStartingWithPrefixSlash_IsNotAPrefix()
    {
        // "/b" (no trailing slash) is a legitimate backend path, not a prefix.
        RouteDecision d = Resolve("/b");
        Assert.Equal("ollama", d.Backend?.Name);
        Assert.Equal("/b", d.ForwardPath.Value);
        Assert.Equal(RouteSource.Default, d.Source);
    }
}
