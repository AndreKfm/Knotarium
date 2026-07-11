using System;
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
}