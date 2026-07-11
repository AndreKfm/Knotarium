using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Knotarium.Infrastructure.Security;

public sealed class HttpEgressPolicyEvaluator
{
    private readonly HttpEgressPolicyOptions _options;

    public HttpEgressPolicyEvaluator(IConfiguration configuration)
    {
        var section = configuration.GetSection(HttpEgressPolicyOptions.SectionName);
        var allowDomains = section.GetSection("AllowDomains").GetChildren().Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
        var blockDomains = section.GetSection("BlockDomains").GetChildren().Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
        var denyPrivateNetworks = true;
        var denyPrivateNetworksRaw = section["DenyPrivateNetworks"];
        if (!string.IsNullOrWhiteSpace(denyPrivateNetworksRaw) && bool.TryParse(denyPrivateNetworksRaw, out var parsedBool))
        {
            denyPrivateNetworks = parsedBool;
        }

        _options = new HttpEgressPolicyOptions
        {
            AllowDomains = allowDomains,
            BlockDomains = blockDomains,
            DenyPrivateNetworks = denyPrivateNetworks
        };
    }

    public HttpEgressPolicyEvaluator(HttpEgressPolicyOptions options)
    {
        _options = options;
    }

    public void EnsureAllowed(Uri requestUri)
    {
        if (requestUri == null)
        {
            throw new ArgumentNullException(nameof(requestUri));
        }

        if (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException($"Outbound egress denied for unsupported scheme '{requestUri.Scheme}'.");
        }

        var host = requestUri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new HttpRequestException("Outbound egress denied because the request host is empty.");
        }

        if (IsLocalHost(host))
        {
            throw new HttpRequestException($"Outbound egress denied for local host '{host}'.");
        }

        if (IPAddress.TryParse(host, out var ipAddress) && _options.DenyPrivateNetworks && IsPrivateOrLoopback(ipAddress))
        {
            throw new HttpRequestException($"Outbound egress denied for private or loopback address '{host}'.");
        }

        if (_options.BlockDomains.Any(pattern => IsDomainMatch(host, pattern)))
        {
            throw new HttpRequestException($"Outbound egress denied by blocklist for host '{host}'.");
        }

        if (_options.AllowDomains.Count > 0 && !_options.AllowDomains.Any(pattern => IsDomainMatch(host, pattern)))
        {
            throw new HttpRequestException($"Outbound egress denied because host '{host}' is not in the allowlist.");
        }
    }

    private static bool IsLocalHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDomainMatch(string host, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var normalized = pattern.Trim();
        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return host.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{normalized}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateOrLoopback(IPAddress ipAddress)
    {
        if (IPAddress.IsLoopback(ipAddress))
        {
            return true;
        }

        if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ipAddress.GetAddressBytes();

            if (bytes[0] == 10)
            {
                return true;
            }

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            if (bytes[0] == 127)
            {
                return true;
            }
        }

        if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ipAddress.IsIPv6LinkLocal || ipAddress.IsIPv6SiteLocal)
            {
                return true;
            }

            var bytes = ipAddress.GetAddressBytes();
            // Unique local addresses fc00::/7
            if ((bytes[0] & 0b1111_1110) == 0b1111_1100)
            {
                return true;
            }
        }

        return false;
    }
}