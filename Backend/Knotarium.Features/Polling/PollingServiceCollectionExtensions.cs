// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;
using Knotarium.Features.Polling;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddPolling() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the polling slice: the change-detection evaluation service, the run enqueuer, the
/// pluggable poll-source registry and its built-in sources (HTTP + OpenAPI). The hosted
/// <c>PollingWorker</c> and the polling-trigger synchronizer stay in the host.
/// </summary>
public static class PollingServiceCollectionExtensions
{
    public static IServiceCollection AddPolling(this IServiceCollection services)
    {
        services.AddScoped<IPollEvaluationService, PollEvaluationService>();
        // IPollRunEnqueuer's implementation now lives in the Execution slice (run creation is an
        // Execution concern); it's registered in AddExecution and consumed here via the Core seam.
        services.AddScoped<PollSourceRegistry>();
        services.AddScoped<IPollSource, HttpPollSource>();
        services.AddScoped<IOpenApiOperationInvoker, OpenApiOperationInvoker>();
        services.AddScoped<IPollSource, OpenApiPollSource>();
        return services;
    }
}
