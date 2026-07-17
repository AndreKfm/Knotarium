// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

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

    [Theory]
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped IPv6 of cloud metadata endpoint
    [InlineData("::ffff:127.0.0.1")]        // IPv4-mapped IPv6 of loopback
    [InlineData("::ffff:10.0.0.1")]         // IPv4-mapped IPv6 of private class A
    [InlineData("::ffff:192.168.1.1")]      // IPv4-mapped IPv6 of private class C
    public async Task ResolveAndValidateAsync_RejectsIPv4MappedIPv6PrivateAddresses(string host)
    {
        // Regression: an IPv4-mapped IPv6 literal (e.g. an attacker-controlled AAAA record) must be
        // unwrapped and classified against the IPv4 private/loopback rules, not slip through the IPv6 branch.
        var evaluator = new HttpEgressPolicyEvaluator(new HttpEgressPolicyOptions());
        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await evaluator.ResolveAndValidateAsync(host, CancellationToken.None));
    }

    [Theory]
    [InlineData("[::ffff:169.254.169.254]")] // bracketed IPv6 host form as it appears in a URI
    [InlineData("[::ffff:127.0.0.1]")]
    public void EnsureAllowed_RejectsIPv4MappedIPv6PrivateAddresses(string bracketedHost)
    {
        var evaluator = new HttpEgressPolicyEvaluator(new HttpEgressPolicyOptions());
        Assert.Throws<HttpRequestException>(
            () => evaluator.EnsureAllowed(new Uri($"http://{bracketedHost}/test")));
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