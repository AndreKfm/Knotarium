// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Resolve <paramref name="host"/> and validate every candidate address against the policy, returning the
    /// addresses to connect to. This is the DNS-aware companion to <see cref="EnsureAllowed(Uri)"/>: a
    /// hostname that resolves to a private/loopback/link-local/ULA address (the SSRF-to-metadata / internal
    /// services bypass) is rejected here even though its literal form is a public-looking name. Used by
    /// <see cref="ConnectAsync"/> so the check runs for the initial request AND every redirect hop, and so the
    /// connection is pinned to an address we validated (defeating DNS-rebinding between check and connect).
    /// </summary>
    public async ValueTask<IPAddress[]> ResolveAndValidateAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new HttpRequestException("Outbound egress denied because the request host is empty.");
        }

        if (IsLocalHost(host))
        {
            throw new HttpRequestException($"Outbound egress denied for local host '{host}'.");
        }

        if (_options.BlockDomains.Any(pattern => IsDomainMatch(host, pattern)))
        {
            throw new HttpRequestException($"Outbound egress denied by blocklist for host '{host}'.");
        }

        if (_options.AllowDomains.Count > 0 && !_options.AllowDomains.Any(pattern => IsDomainMatch(host, pattern)))
        {
            throw new HttpRequestException($"Outbound egress denied because host '{host}' is not in the allowlist.");
        }

        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"Outbound egress denied because host '{host}' did not resolve to any address.");
        }

        // Conservative: reject if ANY resolved address is private/loopback/link-local/ULA. A DNS-rebind
        // attacker controls which record is returned, so a single private answer is disqualifying.
        if (_options.DenyPrivateNetworks && addresses.Any(IsPrivateOrLoopback))
        {
            throw new HttpRequestException($"Outbound egress denied: host '{host}' resolves to a private, loopback, or link-local address.");
        }

        return addresses;
    }

    /// <summary>
    /// A <see cref="System.Net.Sockets.SocketsHttpHandler"/> <c>ConnectCallback</c>: validate the target host
    /// (DNS-aware) and open the connection to a vetted address. Because every physical connection — including
    /// those made when following a redirect — flows through here, redirects are re-evaluated per hop and the
    /// socket is pinned to an address the policy approved.
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var addresses = await ResolveAndValidateAsync(host, cancellationToken).ConfigureAwait(false);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
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
        // Unwrap IPv4-mapped IPv6 (e.g. ::ffff:169.254.169.254) so the IPv4 rules below
        // apply. Without this, an attacker-controlled AAAA record can smuggle a private /
        // loopback / metadata address past the IPv6 branch, which does not classify it.
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }

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