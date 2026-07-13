using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Infrastructure.Security;
using Xunit;

namespace Knotarium.Tests.Security;

public class HttpEgressPolicyEvaluatorTests
{
    [Fact]
    public void EnsureAllowed_BlocksLoopbackByDefault()
    {
        var evaluator = new HttpEgressPolicyEvaluator(new HttpEgressPolicyOptions());

        Assert.Throws<HttpRequestException>(() => evaluator.EnsureAllowed(new Uri("http://127.0.0.1/test")));
        Assert.Throws<HttpRequestException>(() => evaluator.EnsureAllowed(new Uri("http://localhost/test")));
    }

    [Fact]
    public void EnsureAllowed_BlocksConfiguredBlocklistHost()
    {
        var evaluator = new HttpEgressPolicyEvaluator(new HttpEgressPolicyOptions
        {
            BlockDomains = { "blocked.example.com", "*.corp.local" }
        });

        Assert.Throws<HttpRequestException>(() => evaluator.EnsureAllowed(new Uri("https://blocked.example.com/path")));
        Assert.Throws<HttpRequestException>(() => evaluator.EnsureAllowed(new Uri("https://service.corp.local/path")));
    }

    [Fact]
    public void EnsureAllowed_RequiresAllowlistWhenConfigured()
    {
        var evaluator = new HttpEgressPolicyEvaluator(new HttpEgressPolicyOptions
        {
            AllowDomains = { "api.example.com", "*.trusted.net" }
        });

        evaluator.EnsureAllowed(new Uri("https://api.example.com/v1"));
        evaluator.EnsureAllowed(new Uri("https://sub.trusted.net/v2"));

        Assert.Throws<HttpRequestException>(() => evaluator.EnsureAllowed(new Uri("https://unknown.example.org/v1")));
    }

    [Theory]
    [InlineData("10.1.2.3")]     // private class A
    [InlineData("172.16.5.4")]   // private class B
    [InlineData("192.168.0.9")]  // private class C
    [InlineData("169.254.169.254")] // link-local / cloud metadata
    [InlineData("127.0.0.1")]    // loopback
    public async Task ResolveAndValidateAsync_RejectsPrivateOrLoopbackLiteralAddresses(string host)
    {
        var evaluator = new HttpEgressPolicyEvaluator(new HttpEgressPolicyOptions());
        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await evaluator.ResolveAndValidateAsync(host, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAndValidateAsync_AllowsPublicLiteralAddress()
    {
        var evaluator = new HttpEgressPolicyEvaluator(new HttpEgressPolicyOptions());
        var addresses = await evaluator.ResolveAndValidateAsync("93.184.216.34", CancellationToken.None);
        Assert.Single(addresses);
    }

    [Fact]
    public async Task ResolveAndValidateAsync_RejectsLocalHostAndBlocklistedDomain()
    {
        var evaluator = new HttpEgressPolicyEvaluator(new HttpEgressPolicyOptions
        {
            BlockDomains = { "blocked.example.com" }
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await evaluator.ResolveAndValidateAsync("localhost", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await evaluator.ResolveAndValidateAsync("blocked.example.com", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAndValidateAsync_PrivateAddressesAllowedWhenDenyDisabled()
    {
        var evaluator = new HttpEgressPolicyEvaluator(new HttpEgressPolicyOptions { DenyPrivateNetworks = false });
        var addresses = await evaluator.ResolveAndValidateAsync("10.1.2.3", CancellationToken.None);
        Assert.Single(addresses);
    }
}