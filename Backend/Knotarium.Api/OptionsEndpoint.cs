// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts.Options;
using Knotarium.Features.Options;

namespace Knotarium.Api;

/// <summary>Request body for a design-time options query.</summary>
public sealed record LoadOptionsRequest(
    string? ConnectionId,
    Dictionary<string, string>? DependsOn,
    string? Search,
    string? Page);

/// <summary>The error half of the options envelope. Null on success.</summary>
public sealed record OptionsError(string Code, string Message);

/// <summary>
/// Always-200 envelope. A design-time read must never hard-block authoring, so transport / system
/// failures are reported in <see cref="Error"/> with empty <see cref="Options"/> rather than a 4xx/5xx.
/// </summary>
public sealed record LoadOptionsResponse(
    IReadOnlyList<OptionItem> Options,
    bool HasMore,
    string? NextPage,
    OptionsError? Error);

public static class OptionsEndpoint
{
    // Design-time loading is interactive; cap it so a hung external system can't stall the editor.
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(8);

    public static void MapOptionsEndpoint(this WebApplication app)
    {
        app.MapPost("/api/integrations/{integrationType}/options/{loaderName}", async (
            string integrationType,
            string loaderName,
            LoadOptionsRequest request,
            IOptionsLoaderRegistry registry,
            OptionsCache cache,
            ILoggerFactory loggerFactory,
            HttpRequest httpRequest,
            CancellationToken requestToken) =>
        {
            var logger = loggerFactory.CreateLogger("OptionsEndpoint");

            // Allowlist: reject anything not explicitly registered before invoking it.
            var loader = registry.Get(loaderName);
            if (loader is null)
            {
                return Results.NotFound(new { message = $"Unknown options loader '{loaderName}'." });
            }

            var context = new OptionLoadContext(
                request.ConnectionId,
                request.DependsOn ?? new Dictionary<string, string>(),
                string.IsNullOrWhiteSpace(request.Search) ? null : request.Search,
                string.IsNullOrWhiteSpace(request.Page) ? null : request.Page);

            // Manual refresh busts the cached entry: ?refresh=1 or Cache-Control: no-cache.
            var refresh = httpRequest.Query["refresh"] == "1"
                || httpRequest.Headers.CacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            timeoutCts.CancelAfter(LoadTimeout);

            try
            {
                var result = await cache.GetOrLoadAsync(loader, context, refresh, timeoutCts.Token);
                // Credentials never reach the response — only labels + opaque values.
                return Results.Ok(new LoadOptionsResponse(result.Options, result.HasMore, result.NextPage, null));
            }
            catch (OptionsLoadException ex)
            {
                logger.LogInformation(ex, "Options load failed for loader {Loader}", loaderName);
                return Results.Ok(EmptyWithError("SYSTEM_UNREACHABLE", ex.Message));
            }
            catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
            {
                logger.LogInformation("Options load timed out for loader {Loader}", loaderName);
                return Results.Ok(EmptyWithError("SYSTEM_UNREACHABLE", "The resource system did not respond in time."));
            }
        });
    }

    private static LoadOptionsResponse EmptyWithError(string code, string message)
        => new(Array.Empty<OptionItem>(), false, null, new OptionsError(code, message));
}
