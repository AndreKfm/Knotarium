using System;
using System.Text;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Notifications;

/// <summary>
/// The transport-agnostic payload describing a failed workflow run. Built once when an alert is
/// dispatched and handed to every <see cref="INotificationSender"/> so each channel formats it
/// for its own medium.
/// </summary>
public record FailureAlertMessage(
    string WorkflowName,
    string WorkflowId,
    string ExecutionId,
    string? FailedNodeId,
    string ErrorMessage,
    string TriggerOrigin,
    DateTimeOffset TimestampUtc)
{
    /// <summary>Short one-line subject suitable for an e-mail subject or notification title.</summary>
    public string Title => $"⚠️ Workflow \"{WorkflowName}\" failed";

    /// <summary>Relative deep-link path into the app for the failed run.</summary>
    public string ExecutionPath => $"/executions/{ExecutionId}";

    /// <summary>Human-readable multi-line body shared by the plain-text and e-mail channels.</summary>
    public string PlainText
    {
        get
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Workflow \"{WorkflowName}\" ({WorkflowId}) failed.");
            builder.AppendLine($"Run: {ExecutionId}");
            if (!string.IsNullOrWhiteSpace(FailedNodeId))
            {
                builder.AppendLine($"Failed node: {FailedNodeId}");
            }

            builder.AppendLine($"Trigger: {TriggerOrigin}");
            builder.AppendLine($"Time (UTC): {TimestampUtc:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine();
            builder.AppendLine($"Error: {ErrorMessage}");
            return builder.ToString();
        }
    }

    /// <summary>Projects the failure details onto the transport-agnostic <see cref="NotificationMessage"/>.</summary>
    public NotificationMessage ToNotification() => new(
        Title,
        PlainText,
        new Dictionary<string, object?>
        {
            ["type"] = "workflow.failed",
            ["workflowId"] = WorkflowId,
            ["workflowName"] = WorkflowName,
            ["executionId"] = ExecutionId,
            ["failedNodeId"] = FailedNodeId,
            ["error"] = ErrorMessage,
            ["triggerOrigin"] = TriggerOrigin,
            ["timestampUtc"] = TimestampUtc,
        });
}
