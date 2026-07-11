namespace Knotarium.Core.Domain;

public static class JournalEventTypes
{
    public const string WorkflowStarted = "WorkflowStarted";
    public const string WorkflowSuspended = "WorkflowSuspended";
    public const string WorkflowResumed = "WorkflowResumed";
    public const string WorkflowCompleted = "WorkflowCompleted";
    public const string WorkflowFailed = "WorkflowFailed";

    public const string NodeExecutionStarted = "NodeExecutionStarted";
    public const string NodeExecutionFailed = "NodeExecutionFailed";
    public const string NodeExecutionCompleted = "NodeExecutionCompleted"; // Aligned with database schema

    public const string VariableUpdated = "VariableUpdated";
    public const string AttemptingExternalEffect = "AttemptingExternalEffect";
    public const string ManualDecisionRecorded = "ManualDecisionRecorded";

    public const string NotificationSent = "NotificationSent";
    public const string NotificationFailed = "NotificationFailed";
}
