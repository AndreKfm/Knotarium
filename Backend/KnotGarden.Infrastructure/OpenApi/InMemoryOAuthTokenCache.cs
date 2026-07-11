using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts.OpenApi;

namespace KnotGarden.Infrastructure.OpenApi;

/// <summary>
/// Thread-safe in-process OAuth2 token cache. Fetches via client-credentials flow only.
/// Authorization-Code and Implicit flows are not supported (v1).
/// </summary>
public sealed class InMemoryOAuthTokenCache : IOAuthTokenCache
{
    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly TimeSpan _expiryBuffer = TimeSpan.FromSeconds(30);

    public InMemoryOAuthTokenCache(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetTokenAsync(
        string cacheKey,
        string tokenUrl,
        string clientId,
        string clientSecret,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default)
    {
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt - DateTimeOffset.UtcNow > _expiryBuffer)
        {
            return cached.AccessToken;
        }

        var token = await FetchTokenAsync(tokenUrl, clientId, clientSecret, scopes, ct);
        _cache[cacheKey] = token;
        return token.AccessToken;
    }

    public void Invalidate(string cacheKey) => _cache.TryRemove(cacheKey, out _);

    private async Task<CachedToken> FetchTokenAsync(
        string tokenUrl,
        string clientId,
        string clientSecret,
        IReadOnlyList<string> scopes,
        CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient();

        var body = new List<KeyValuePair<string, string>>
        {
            new("grant_type",    "client_credentials"),
            new("client_id",     clientId),
            new("client_secret", clientSecret),
        };
        if (scopes.Count > 0)
            body.Add(new("scope", string.Join(" ", scopes)));

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(body)
        };

        using var response = await http.SendAsync(req, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OAuth2 token endpoint returned {(int)response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("access_token", out var tokenEl) ||
            tokenEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("OAuth2 response missing 'access_token'.");

        var accessToken = tokenEl.GetString()!;
        var expiresIn   = root.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : 3600;
        var expiresAt   = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        return new CachedToken(accessToken, expiresAt);
    }
}
