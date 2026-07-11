using System;
using System.Collections.Generic;

namespace KnotGarden.Core.Domain;

public class ExecutionInstance
{
    public ExecutionInstanceId Id { get; set; }
    public WorkflowDefinitionId WorkflowDefinitionId { get; set; }
    public WorkflowVersionId? WorkflowVersionId { get; set; }
    public ExecutionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>
    /// Gets or sets the origin that started the execution, such as manual, webhook, or schedule.
    /// </summary>
    public string TriggerOrigin { get; set; } = "manual";
    public Dictionary<string, object> GlobalVariables { get; set; } = new();
    public string? VariableState { get; set; }

    /// <summary>
    /// Gets or sets the source execution this run is a replay of, or null for normal runs.
    /// The source run is never mutated; a replay is always a new, linked <see cref="ExecutionInstance"/>.
    /// </summary>
    public ExecutionInstanceId? ReplayOfExecutionId { get; set; }

    /// <summary>
    /// Gets or sets the cut-point node a replay was started from, or null for normal runs.
    /// </summary>
    public NodeId? ReplayFromNodeId { get; set; }

    /// <summary>
    /// Gets or sets the failed execution this run is the error-handler for, or null for normal runs.
    /// Set when an error workflow run is started (TriggerOrigin "error") so the failed run and its
    /// handler run can be navigated between in the UI.
    /// </summary>
    public ExecutionInstanceId? ErrorOfExecutionId { get; set; }


    // Navigation Properties
    public List<NodeState> NodeStates { get; set; } = new();
    public List<ExecutionJournal> JournalEntries { get; set; } = new();
}
