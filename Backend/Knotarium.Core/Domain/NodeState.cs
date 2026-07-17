// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Knotarium.Core.Domain;

public class NodeState
{
    public Guid Id { get; set; }
    public ExecutionInstanceId ExecutionInstanceId { get; set; }
    public NodeId NodeId { get; set; }
    public NodeStatus Status { get; set; }
    public Dictionary<string, object> Inputs { get; set; } = new();
    public Dictionary<string, object> Outputs { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public int ExecutionCount { get; set; }

    /// <summary>
    /// Gets or sets a JSON snapshot of the execution's <see cref="ExecutionInstance.GlobalVariables"/>
    /// captured at the moment this node started executing. Enables exact cut-point variable
    /// reconstruction for replay / time-travel debugging without journal folding.
    /// </summary>
    public string? VariablesBefore { get; set; }
}
