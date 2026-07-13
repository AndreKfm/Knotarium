using System;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knotarium.Api;

/// <summary>
/// Rate-limiting policies for the two abuse-prone surfaces: interactive login (online brute force) and the
/// anonymous machine-facing routes — the webhook trigger and the token-authenticated resume (flooding). Both
/// are per-client fixed windows; a client is identified by remote IP. Limits are configurable and default
/// generously enough for normal automation while blunting scripted abuse.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Interactive <c>POST /api/auth/login</c>.</summary>
    public const string Login = "login";

    /// <summary>Anonymous machine routes: the webhook trigger and resume.</summary>
    public const string AnonymousMachine = "anon-machine";

    public static IServiceCollection AddKnotariumRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var loginPerMinute = configuration.GetValue("Security:RateLimit:LoginPerMinute", 10);
        var machinePerMinute = configuration.GetValue("Security:RateLimit:AnonymousMachinePerMinute", 60);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(Login, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ClientKey(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, loginPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            options.AddPolicy(AnonymousMachine, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ClientKey(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, machinePerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        return services;
    }

    private static string ClientKey(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
