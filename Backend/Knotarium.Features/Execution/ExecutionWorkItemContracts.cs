using System;
using System.Text.Json;

namespace Knotarium.Features.Execution;

/// <summary>Payload of a <c>Resume</c> execution work item.</summary>
internal sealed record ResumeWorkItemPayload(string? NodeId, Guid? WorkflowVersionId, JsonElement Output);

/// <summary>Payload of a <c>Retry</c> execution work item.</summary>
internal sealed record RetryWorkItemPayload(string? NodeId, int AttemptNumber, Guid? WorkflowVersionId);

/// <summary>Payload of a <c>ManualDecision</c> execution work item.</summary>
internal sealed record ManualDecisionWorkItemPayload(string? NodeId, string Decision, string? Reason, string? ExpectedAttemptId, Guid? WorkflowVersionId);

/// <summary>An operator's decision for a node stuck in <c>RequiresManualDecision</c>.</summary>
internal enum ManualDecision
{
    Retry,
    Skip,
    Fail
}

internal static class ManualDecisions
{
    public static bool TryNormalize(string decision, out ManualDecision normalizedDecision)
    {
        normalizedDecision = default;

        if (string.IsNullOrWhiteSpace(decision))
        {
            return false;
        }

        return Enum.TryParse(decision, ignoreCase: true, out normalizedDecision);
    }
}
