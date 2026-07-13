using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Knotarium.Api.Services;

/// <summary>
/// Guards the "authentication disabled" deployment mode against accidental LAN/internet exposure. With
/// <c>Auth:Enabled=false</c> there is no auth middleware and every endpoint — including the capability
/// toggle and Inline Code — is anonymous, which is only safe when the server is reachable from the local
/// machine alone. This helper inspects the configured Kestrel binding addresses and reports any that are
/// NOT loopback, so the composition root can refuse to start (production) or warn (development) rather than
/// silently serving an unauthenticated RCE surface on <c>0.0.0.0</c>.
///
/// The escape hatch is <c>Security:AllowUnauthenticatedNonLoopback=true</c> for operators who front the app
/// with their own auth (reverse proxy, mTLS, network ACL) and accept the risk deliberately.
/// </summary>
public static class LoopbackBindingGuard
{
    public const string OverrideConfigKey = "Security:AllowUnauthenticatedNonLoopback";

    /// <summary>
    /// The non-loopback binding addresses found in configuration. Empty means the effective binding is
    /// loopback-only (or the framework default, which is loopback), i.e. safe for no-auth mode.
    /// </summary>
    public static IReadOnlyList<string> NonLoopbackBindings(IConfiguration configuration)
        => CollectConfiguredUrls(configuration).Where(url => !IsLoopbackUrl(url)).ToList();

    private static IEnumerable<string> CollectConfiguredUrls(IConfiguration configuration)
    {
        // The aggregate "urls" key covers ASPNETCORE_URLS / DOTNET_URLS / --urls.
        var urls = configuration["urls"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return url;
            }
        }

        // Explicit Kestrel endpoint bindings (appsettings Kestrel:Endpoints:*:Url).
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                yield return url;
            }
        }
    }

    internal static bool IsLoopbackUrl(string url)
    {
        var host = ExtractHost(url);
        if (string.IsNullOrEmpty(host))
        {
            // No host we can reason about (shouldn't happen) — treat as non-loopback so we err on the safe side.
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Wildcards bind every interface — explicitly NOT loopback.
        if (host is "*" or "+" or "0.0.0.0" or "::" or "[::]")
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return IPAddress.IsLoopback(ip);
        }

        // A DNS hostname could resolve anywhere — not loopback for the purposes of this guard.
        return false;
    }

    private static string ExtractHost(string url)
    {
        var s = url.Trim();
        var schemeIndex = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            s = s[(schemeIndex + 3)..];
        }

        var slash = s.IndexOf('/');
        if (slash >= 0)
        {
            s = s[..slash];
        }

        // IPv6 literal: [::1]:port
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            return end > 0 ? s[1..end] : s;
        }

        var colon = s.LastIndexOf(':');
        if (colon >= 0)
        {
            s = s[..colon];
        }

        return s;
    }
}
