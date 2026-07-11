# Step 3 — B4.1: Scheduler Trigger Registry

## Goal
Register the `Scheduler` node as a trigger-only entry-point package. Rather than hardcoding structural layout checks based on node type strings, the compiler must evaluate the manifest metadata (`TriggerOnly: true`). The workflow compiler must validate the DAG topology, throwing compilation exceptions if a trigger-only node is positioned with incoming connections.

---

## Invariant Alignment
* **Invariant 4.1 (Trigger-Only Boundary):** Schedulers are entry-point triggers with no input ports, and they do not execute via `INodeExecutor`.
* **Metadata-Driven Layout Rules**: The compiler checks the manifest property `TriggerOnly` to enforce edge connectivity.

---

## Proposed Changes

### 1. Register [manifest.yaml](file:///d:/Private/Source/AknSideProjects/Automate/nodes/scheduler/manifest.yaml) [NEW]
Define the Scheduler trigger package manifest:
```yaml
id: scheduler
displayName: Cron Scheduler
version: 1.0.0
category: Trigger
triggerOnly: true          # Dynamic layout constraint
description: Triggers workflow runs based on a defined cron expression.
parameters:
  - name: cronExpression
    type: String
    required: true
    expression: false
  - name: timezoneId
    type: String
    required: true
    expression: false
outputs:
  - name: triggeredAt
```

### 2. Update Compiler Validations in [WorkflowCompiler.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Core/Compiler/WorkflowCompiler.cs) [MODIFY]
Introduce dynamic trigger validations:
```csharp
public void ValidateWorkflowStructure(WorkflowDefinition definition)
{
    foreach (var node in definition.Nodes)
    {
        var manifest = GetNodeManifest(node.Type);
        if (manifest.TriggerOnly)
        {
            // Enforce Invariant 4.1: Check for incoming connections
            var incomingEdges = definition.Edges.Any(e => e.TargetNodeId == node.Id);
            if (incomingEdges)
            {
                throw new CompilationException(
                    $"Node '{node.Id}' of type '{node.Type}' is a trigger entry-point and cannot have incoming connections."
                );
            }
        }
    }
}
```

---

## Verification & Test Checklist

### 1. Unit Tests
* Write unit tests in `SchedulerTriggerTests.cs` verifying:
  * **Invalid Connections**: Compile a workflow where a regular node connects *to* a `triggerOnly` Scheduler node. Assert that the compiler throws a validation/compilation exception.
  * **Valid Entry-Point Layout**: Compile a workflow where the `Scheduler` node acts strictly as the entry point (no incoming edges, output edges connecting to downstream tasks). Assert the compiler compiles successfully with zero warnings.

### 2. Manual Verification
* Deploy canvas and verify trigger nodes cannot receive incoming edge connections.
