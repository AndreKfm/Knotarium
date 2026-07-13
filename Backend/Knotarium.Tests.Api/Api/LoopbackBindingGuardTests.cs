using System.Collections.Generic;
using System.Linq;
using Knotarium.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Knotarium.Tests.Api;

/// <summary>
/// Unit coverage for the no-auth loopback binding guard (H2). Loopback / wildcard-free / DNS-name handling
/// decides whether "Auth:Enabled=false" is refused at startup.
/// </summary>
public sealed class LoopbackBindingGuardTests
{
    private static IConfiguration Config(params (string key, string value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.key, e => (string?)e.value))
            .Build();

    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://localhost:5000")]
    [InlineData("https://[::1]:5001")]
    public void Loopback_urls_are_allowed(string url)
    {
        var result = LoopbackBindingGuard.NonLoopbackBindings(Config(("urls", url)));
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://*:5000")]
    [InlineData("http://+:80")]
    [InlineData("http://192.168.1.10:5000")]
    [InlineData("http://example.internal:8080")]
    public void Non_loopback_urls_are_flagged(string url)
    {
        var result = LoopbackBindingGuard.NonLoopbackBindings(Config(("urls", url)));
        Assert.Single(result);
    }

    [Fact]
    public void No_binding_configured_is_treated_as_safe()
    {
        Assert.Empty(LoopbackBindingGuard.NonLoopbackBindings(Config()));
    }

    [Fact]
    public void Kestrel_endpoint_bindings_are_inspected()
    {
        var config = Config(("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:5000"));
        Assert.Single(LoopbackBindingGuard.NonLoopbackBindings(config));
    }

    [Fact]
    public void Mixed_bindings_report_only_the_non_loopback_ones()
    {
        var config = Config(("urls", "http://127.0.0.1:5000;http://0.0.0.0:6000"));
        var result = LoopbackBindingGuard.NonLoopbackBindings(config);
        Assert.Single(result);
        Assert.Contains("0.0.0.0", result[0]);
    }
}
