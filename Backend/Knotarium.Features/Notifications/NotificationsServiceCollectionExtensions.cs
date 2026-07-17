// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;
using Knotarium.Features.Notifications;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddNotifications() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the notification / failure-alert spine and the error-workflow spine. Each spine pairs a
/// singleton queue (written from the scoped executor) with a hosted worker that drains it — the queue
/// MUST be a singleton so the executor injects the same instance the worker reads. The error-workflow
/// enqueuer itself lives with the execution slice (see AddExecution).
/// </summary>
public static class NotificationsServiceCollectionExtensions
{
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        // Failure-alert spine.
        services.AddSingleton<FailureAlertQueue>();
        // Alias the producer seam to the same singleton so the executor signals failures through
        // IFailureAlertSink without depending on the Notifications slice. This registration is
        // load-bearing: the executor's sink param is optional (defaults to null = no alerting), so the
        // container must resolve the interface here or production alerting silently no-ops.
        services.AddSingleton<IFailureAlertSink>(sp => sp.GetRequiredService<FailureAlertQueue>());
        services.AddHostedService<FailureAlertWorker>();
        services.AddScoped<NotificationDispatcher>();
        // Alias the dispatch seam to the same scoped instance so the SendNotification node can dispatch
        // without depending on the Notifications slice. The test endpoint / failure-alert worker keep
        // injecting the concrete dispatcher.
        services.AddScoped<INotificationDispatcher>(sp => sp.GetRequiredService<NotificationDispatcher>());

        // Error-workflow spine.
        services.AddSingleton<ErrorWorkflowQueue>();
        // Alias the producer seam to the same singleton (same rationale as IFailureAlertSink above).
        services.AddSingleton<IErrorWorkflowSink>(sp => sp.GetRequiredService<ErrorWorkflowQueue>());
        services.AddHostedService<ErrorWorkflowWorker>();

        // Channel senders.
        services.AddTransient<INotificationSender, WebhookNotificationSender>();
        services.AddTransient<INotificationSender, SlackNotificationSender>();
        services.AddTransient<INotificationSender, EmailNotificationSender>();
        return services;
    }
}
