using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;
using Xunit;

namespace KnotGarden.Tests;

public class SchedulerTriggerTests
{
    private readonly WorkflowCompiler _compiler;

    public SchedulerTriggerTests()
    {
        var definitionProvider = new InMemoryWorkflowDefinitionProvider();
        var manifestProvider = new InMemoryNodePackageManifestProvider();
        _compiler = new WorkflowCompiler(definitionProvider, manifestProvider);
    }

    [Fact]
    public async Task Compile_InvalidConnections_TriggerNodeWithIncomingEdge_FailsCompilation()
    {
        // Define a workflow where a 'Log' node connects to a trigger 'Scheduler' node (Invalid connection!)
        var id = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start-1"), "Start", new Dictionary<string, object>());
        var logNode = new NodeDefinition(NodeId.Create("log-1"), "Log", new Dictionary<string, object> { ["message"] = "Hello" });
        var schedulerNode = new NodeDefinition(NodeId.Create("scheduler-1"), "scheduler", new Dictionary<string, object>
        {
            ["cronExpression"] = "0 0 * * *",
            ["timezoneId"] = "UTC"
        });

        var edge1 = new EdgeDefinition("edge-1", startNode.Id, "result", logNode.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", logNode.Id, "result", schedulerNode.Id, "in"); // Invalid incoming connection to scheduler!

        var workflow = new WorkflowDefinition(
            id,
            "Invalid Scheduler Setup",
            new[] { startNode, logNode, schedulerNode },
            new[] { edge1, edge2 }
        );

        var result = await _compiler.CompileAsync(workflow);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, d => d.Code == "ERR_TRIGGER_WITH_INCOMING_CONNECTIONS");
    }

    [Fact]
    public async Task Compile_ValidEntryPointLayout_SchedulerAsTrigger_CompilesSuccessfully()
    {
        // Define a workflow where 'Scheduler' acts strictly as an entry point trigger
        var id = WorkflowDefinitionId.New();
        var schedulerNode = new NodeDefinition(NodeId.Create("scheduler-1"), "scheduler", new Dictionary<string, object>
        {
            ["cronExpression"] = "0 0 * * *",
            ["timezoneId"] = "UTC"
        });
        var logNode = new NodeDefinition(NodeId.Create("log-1"), "Log", new Dictionary<string, object> { ["message"] = "Triggered!" });

        var edge1 = new EdgeDefinition("edge-1", schedulerNode.Id, "triggeredAt", logNode.Id, "in");

        var workflow = new WorkflowDefinition(
            id,
            "Valid Scheduler Setup",
            new[] { schedulerNode, logNode },
            new[] { edge1 }
        );

        var result = await _compiler.CompileAsync(workflow);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Plan);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains(result.Plan!.EntryNodes, nodeId => nodeId == schedulerNode.Id);
    }

    private class InMemoryWorkflowDefinitionProvider : IWorkflowDefinitionProvider
    {
        private readonly Dictionary<WorkflowDefinitionId, WorkflowDefinition> _definitions = new();

        public Task<WorkflowDefinition?> GetDefinitionAsync(WorkflowDefinitionId id, System.Threading.CancellationToken cancellationToken = default)
        {
            _definitions.TryGetValue(id, out var definition);
            return Task.FromResult(definition);
        }
    }
}
