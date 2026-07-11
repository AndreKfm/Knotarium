using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace KnotGarden.Tests.Compiler;

public class CompilerTests
{
    private readonly MockWorkflowDefinitionProvider _provider = new();
    private readonly InMemoryNodePackageManifestProvider _manifestProvider = new();
    private readonly WorkflowCompiler _compiler;

    public CompilerTests()
    {
        _compiler = new WorkflowCompiler(_provider, _manifestProvider);
    }

    [Fact]
    public async Task Successful_Compilation_With_Valid_Workflow()
    {
        var id = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logNode = new NodeDefinition(NodeId.Create("log-1"), "log", new Dictionary<string, object> { { "message", "hello" } });
        var endNode = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("edge-1", startNode.Id, "result", logNode.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", logNode.Id, "result", endNode.Id, "in");

        var workflow = new WorkflowDefinition(id, "Valid Flow", new[] { startNode, logNode, endNode }, new[] { edge1, edge2 });

        var result = await _compiler.CompileAsync(workflow);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Plan);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Plan.Nodes.Length);
        Assert.Equal(2, result.Plan.Edges.Length);
        Assert.Single(result.Plan.EntryNodes);
        Assert.Equal("start-1", result.Plan.EntryNodes[0].Value);
    }

    [Fact]
    public async Task Compiles_With_Inert_Annotation_Nodes()
    {
        // Sticky notes and groups are editor-only annotations registered as inert, port-less
        // node types. They sit on the canvas with no edges, so they must compile cleanly
        // alongside the executable graph rather than tripping ERR_INVALID_NODE_TYPE.
        var id = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logNode = new NodeDefinition(NodeId.Create("log-1"), "log", new Dictionary<string, object> { { "message", "hello" } });
        var endNode = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());
        var note = new NodeDefinition(NodeId.Create("note-1"), "stickyNote", new Dictionary<string, object> { { "text", "TODO: tune the retry policy" } });
        var group = new NodeDefinition(NodeId.Create("group-1"), "group", new Dictionary<string, object> { { "label", "Ingestion" } });

        var edge1 = new EdgeDefinition("edge-1", startNode.Id, "result", logNode.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", logNode.Id, "result", endNode.Id, "in");

        var workflow = new WorkflowDefinition(id, "Annotated Flow", new[] { startNode, logNode, endNode, note, group }, new[] { edge1, edge2 });

        var result = await _compiler.CompileAsync(workflow);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Plan);
        // The inert annotations are kept in the plan's node set but carry no edges.
        Assert.Equal(5, result.Plan.Nodes.Length);
        Assert.Single(result.Plan.EntryNodes);
    }

    [Fact]
    public async Task Fails_When_Start_Node_Is_Missing()
    {
        var id = WorkflowDefinitionId.New();
        var logNode = new NodeDefinition(NodeId.Create("log-1"), "log", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());
        var edge = new EdgeDefinition("edge-1", logNode.Id, "result", endNode.Id, "in");

        var workflow = new WorkflowDefinition(id, "No Start Node Flow", new[] { logNode, endNode }, new[] { edge });

        var result = await _compiler.CompileAsync(workflow);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ERR_MISSING_START_NODE", diagnostic.Code);
    }

    [Fact]
    public async Task Device_Only_Graph_Compiles_Without_A_Trigger_And_Skips_Pin_Socket_Validation()
    {
        // A reactive device-block graph has no control-flow trigger (it's "always live while enabled")
        // and wires DYNAMIC evt:/act: pins that aren't manifest-declared sockets. The control-flow
        // compiler must accept both: no ERR_MISSING_START_NODE, no ERR_INVALID_SOCKET_MAPPING.
        var id = WorkflowDefinitionId.New();
        var deviceA = new NodeDefinition(NodeId.Create("A"), "externalDevice", new Dictionary<string, object>
        {
            ["targetId"] = new Dictionary<string, object> { ["value"] = "siteA" },
        });
        var deviceB = new NodeDefinition(NodeId.Create("B"), "externalDevice", new Dictionary<string, object>
        {
            ["targetId"] = new Dictionary<string, object> { ["value"] = "siteB" },
        });
        var edge = new EdgeDefinition("e1", deviceA.Id, "evt:Motion", deviceB.Id, "act:Record");

        var workflow = new WorkflowDefinition(id, "Device Graph", new[] { deviceA, deviceB }, new[] { edge });

        var result = await _compiler.CompileAsync(workflow);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Plan);
        Assert.Empty(result.Plan.EntryNodes); // reactive-only: nothing runs on a control-flow trigger
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ERR_MISSING_START_NODE");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ERR_INVALID_SOCKET_MAPPING");
    }

    [Fact]
    public async Task Fails_When_Invalid_Node_Type_Used()
    {
        var id = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var invalidNode = new NodeDefinition(NodeId.Create("invalid-1"), "someUnknownType", new Dictionary<string, object>());
        var edge = new EdgeDefinition("edge-1", startNode.Id, "result", invalidNode.Id, "in");

        var workflow = new WorkflowDefinition(id, "Invalid Node Type Flow", new[] { startNode, invalidNode }, new[] { edge });

        var result = await _compiler.CompileAsync(workflow);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ERR_INVALID_NODE_TYPE", diagnostic.Code);
        Assert.Equal("invalid-1", diagnostic.NodeId?.Value);
    }

    [Fact]
    public async Task Fails_When_Edges_Are_Invalid()
    {
        var id = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("edge-1", NodeId.Create("missing-node"), "success", endNode.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", startNode.Id, "result", NodeId.Create("missing-target"), "in");

        var workflow = new WorkflowDefinition(id, "Invalid Edges Flow", new[] { startNode, endNode }, new[] { edge1, edge2 });

        var result = await _compiler.CompileAsync(workflow);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Plan);
        Assert.Equal(2, result.Diagnostics.Length);
        Assert.Contains(result.Diagnostics, d => d.Code == "ERR_INVALID_EDGE_SOURCE" && d.NodeId?.Value == "missing-node");
        Assert.Contains(result.Diagnostics, d => d.Code == "ERR_INVALID_EDGE_TARGET" && d.NodeId?.Value == "missing-target");
    }

    [Fact]
    public async Task Fails_When_Cycle_Is_Detected()
    {
        var id = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logNode1 = new NodeDefinition(NodeId.Create("log-1"), "log", new Dictionary<string, object>());
        var logNode2 = new NodeDefinition(NodeId.Create("log-2"), "log", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("edge-1", startNode.Id, "result", logNode1.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", logNode1.Id, "result", logNode2.Id, "in");
        var edge3 = new EdgeDefinition("edge-3", logNode2.Id, "result", logNode1.Id, "in");

        var workflow = new WorkflowDefinition(id, "Cyclic Flow", new[] { startNode, logNode1, logNode2 }, new[] { edge1, edge2, edge3 });

        var result = await _compiler.CompileAsync(workflow);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ERR_CYCLE_DETECTED", diagnostic.Code);
    }

    [Fact]
    public async Task Compiles_And_Inlines_Subflow_Correctly()
    {
        var subflowId = WorkflowDefinitionId.New();
        var subflowStart = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var subflowEnd = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());
        var subflowEdge = new EdgeDefinition("sub-edge-1", subflowStart.Id, "result", subflowEnd.Id, "in");
        var subflowDef = new WorkflowDefinition(subflowId, "Subflow definition", new[] { subflowStart, subflowEnd }, new[] { subflowEdge });
        _provider.AddDefinition(subflowDef);

        var parentId = WorkflowDefinitionId.New();
        var parentStart = new NodeDefinition(NodeId.Create("start-parent"), "start", new Dictionary<string, object>());
        var subflowNode = new NodeDefinition(NodeId.Create("sub-node"), "subflow", new Dictionary<string, object> { { "subflowId", subflowId.Value.ToString() } });
        var parentEnd = new NodeDefinition(NodeId.Create("end-parent"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("edge-1", parentStart.Id, "result", subflowNode.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", subflowNode.Id, "end", parentEnd.Id, "in");

        var parentWorkflow = new WorkflowDefinition(parentId, "Parent Flow", new[] { parentStart, subflowNode, parentEnd }, new[] { edge1, edge2 });

        var result = await _compiler.CompileAsync(parentWorkflow);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Plan);
        Assert.Empty(result.Diagnostics);

        Assert.Equal(4, result.Plan.Nodes.Length);
        Assert.Contains(result.Plan.Nodes, n => n.Id.Value == "start-parent");
        Assert.Contains(result.Plan.Nodes, n => n.Id.Value == "end-parent");
        Assert.Contains(result.Plan.Nodes, n => n.Id.Value == "sub-node/start-1");
        Assert.Contains(result.Plan.Nodes, n => n.Id.Value == "sub-node/end-1");

        Assert.Equal(3, result.Plan.Edges.Length);
        Assert.Contains(result.Plan.Edges, e => e.From.Value == "start-parent" && e.To.Value == "sub-node/start-1");
        Assert.Contains(result.Plan.Edges, e => e.From.Value == "sub-node/start-1" && e.To.Value == "sub-node/end-1");
        Assert.Contains(result.Plan.Edges, e => e.From.Value == "sub-node/end-1" && e.To.Value == "end-parent");
    }

    [Fact]
    public async Task Compiles_Subflow_When_SubflowId_Is_JsonElement()
    {
        // Over the HTTP publish/validate endpoints, NodeDefinition.Properties values deserialize as
        // JsonElement rather than native strings. The subflow inliner must read the id either way.
        var subflowId = WorkflowDefinitionId.New();
        var subflowStart = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var subflowEnd = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());
        var subflowEdge = new EdgeDefinition("sub-edge-1", subflowStart.Id, "result", subflowEnd.Id, "in");
        var subflowDef = new WorkflowDefinition(subflowId, "Subflow definition", new[] { subflowStart, subflowEnd }, new[] { subflowEdge });
        _provider.AddDefinition(subflowDef);

        // Simulate the deserialized HTTP shape: subflowId arrives as a JsonElement string.
        var subflowIdElement = System.Text.Json.JsonSerializer.SerializeToElement(subflowId.Value.ToString());

        var parentId = WorkflowDefinitionId.New();
        var parentStart = new NodeDefinition(NodeId.Create("start-parent"), "start", new Dictionary<string, object>());
        var subflowNode = new NodeDefinition(NodeId.Create("sub-node"), "subflow", new Dictionary<string, object> { { "subflowId", subflowIdElement } });
        var parentEnd = new NodeDefinition(NodeId.Create("end-parent"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("edge-1", parentStart.Id, "result", subflowNode.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", subflowNode.Id, "end", parentEnd.Id, "in");

        var parentWorkflow = new WorkflowDefinition(parentId, "Parent Flow", new[] { parentStart, subflowNode, parentEnd }, new[] { edge1, edge2 });

        var result = await _compiler.CompileAsync(parentWorkflow);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Plan);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ERR_MISSING_SUBFLOW_ID");
        Assert.Contains(result.Plan.Nodes, n => n.Id.Value == "sub-node/start-1");
        Assert.Contains(result.Plan.Nodes, n => n.Id.Value == "sub-node/end-1");
    }

    [Fact]
    public async Task Inlining_Attaches_Subflow_Input_And_Output_Maps_To_Start_And_End()
    {
        var subflowId = WorkflowDefinitionId.New();
        var subflowStart = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var subflowEnd = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());
        var subflowEdge = new EdgeDefinition("sub-edge-1", subflowStart.Id, "result", subflowEnd.Id, "in");
        var subflowDef = new WorkflowDefinition(subflowId, "Subflow definition", new[] { subflowStart, subflowEnd }, new[] { subflowEdge });
        _provider.AddDefinition(subflowDef);

        var inputsMap = new List<object> { new Dictionary<string, object> { { "target", "id" }, { "value", 7L } } };
        var outputsMap = new List<object> { new Dictionary<string, object> { { "source", "total" }, { "target", "orderTotal" } } };

        var parentId = WorkflowDefinitionId.New();
        var parentStart = new NodeDefinition(NodeId.Create("start-parent"), "start", new Dictionary<string, object>());
        var subflowNode = new NodeDefinition(NodeId.Create("sub-node"), "subflow", new Dictionary<string, object>
        {
            { "subflowId", subflowId.Value.ToString() },
            { "subflowInputs", inputsMap },
            { "subflowOutputs", outputsMap },
        });
        var parentEnd = new NodeDefinition(NodeId.Create("end-parent"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("edge-1", parentStart.Id, "result", subflowNode.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", subflowNode.Id, "end", parentEnd.Id, "in");
        var parentWorkflow = new WorkflowDefinition(parentId, "Parent Flow", new[] { parentStart, subflowNode, parentEnd }, new[] { edge1, edge2 });

        var result = await _compiler.CompileAsync(parentWorkflow);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Plan);
        var inlinedStart = Assert.Single(result.Plan.Nodes, n => n.Id.Value == "sub-node/start-1");
        var inlinedEnd = Assert.Single(result.Plan.Nodes, n => n.Id.Value == "sub-node/end-1");
        Assert.True(inlinedStart.Properties.ContainsKey("__subflowInputs"));
        Assert.True(inlinedEnd.Properties.ContainsKey("__subflowOutputs"));
    }

    [Fact]
    public async Task Subflow_Used_Twice_Isolates_Internal_Variables_Per_Instance()
    {
        // A subflow that writes a 'counter' variable and reads it back in a log message.
        var subflowId = WorkflowDefinitionId.New();
        var subStart = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var subSet = new NodeDefinition(NodeId.Create("set-1"), "setVariable", new Dictionary<string, object> { { "variableName", "counter" }, { "value", 1L } });
        var subLog = new NodeDefinition(NodeId.Create("log-1"), "log", new Dictionary<string, object> { { "message", "val={{ $variables.counter }}" } });
        var subEnd = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());
        var subEdges = new[]
        {
            new EdgeDefinition("se1", subStart.Id, "result", subSet.Id, "in"),
            new EdgeDefinition("se2", subSet.Id, "result", subLog.Id, "in"),
            new EdgeDefinition("se3", subLog.Id, "result", subEnd.Id, "in"),
        };
        _provider.AddDefinition(new WorkflowDefinition(subflowId, "Counter Sub", new[] { subStart, subSet, subLog, subEnd }, subEdges));

        var parentId = WorkflowDefinitionId.New();
        var pStart = new NodeDefinition(NodeId.Create("start-p"), "start", new Dictionary<string, object>());
        var subA = new NodeDefinition(NodeId.Create("sub-a"), "subflow", new Dictionary<string, object> { { "subflowId", subflowId.Value.ToString() } });
        var subB = new NodeDefinition(NodeId.Create("sub-b"), "subflow", new Dictionary<string, object> { { "subflowId", subflowId.Value.ToString() } });
        var pEnd = new NodeDefinition(NodeId.Create("end-p"), "end", new Dictionary<string, object>());
        var pEdges = new[]
        {
            new EdgeDefinition("pe1", pStart.Id, "result", subA.Id, "in"),
            new EdgeDefinition("pe2", subA.Id, "end", subB.Id, "in"),
            new EdgeDefinition("pe3", subB.Id, "end", pEnd.Id, "in"),
        };
        var parent = new WorkflowDefinition(parentId, "Parent", new[] { pStart, subA, subB, pEnd }, pEdges);

        var result = await _compiler.CompileAsync(parent);

        Assert.True(result.IsSuccess);
        var setA = Assert.Single(result.Plan!.Nodes, n => n.Id.Value == "sub-a/set-1");
        var setB = Assert.Single(result.Plan.Nodes, n => n.Id.Value == "sub-b/set-1");
        var logA = Assert.Single(result.Plan.Nodes, n => n.Id.Value == "sub-a/log-1");
        var logB = Assert.Single(result.Plan.Nodes, n => n.Id.Value == "sub-b/log-1");

        var nameA = setA.Properties["variableName"]?.ToString();
        var nameB = setB.Properties["variableName"]?.ToString();

        // Each instance's internal variable is namespaced away from the raw name and from each other.
        Assert.NotEqual("counter", nameA);
        Assert.NotEqual("counter", nameB);
        Assert.NotEqual(nameA, nameB);

        // Within an instance, the read (log message) resolves to the same scoped name as the write.
        Assert.Contains(nameA!, logA.Properties["message"]?.ToString());
        Assert.Contains(nameB!, logB.Properties["message"]?.ToString());
    }

    [Fact]
    public async Task Fails_When_Recursive_Subflow_Detected()
    {
        var subflow1Id = WorkflowDefinitionId.New();
        var subflow2Id = WorkflowDefinitionId.New();

        var s1Start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var s1Sub = new NodeDefinition(NodeId.Create("sub-2"), "subflow", new Dictionary<string, object> { { "subflowId", subflow2Id.Value.ToString() } });
        var s1End = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());
        var s1Workflow = new WorkflowDefinition(subflow1Id, "Subflow 1", new[] { s1Start, s1Sub, s1End }, Array.Empty<EdgeDefinition>());
        _provider.AddDefinition(s1Workflow);

        var s2Start = new NodeDefinition(NodeId.Create("start-2"), "start", new Dictionary<string, object>());
        var s2Sub = new NodeDefinition(NodeId.Create("sub-1"), "subflow", new Dictionary<string, object> { { "subflowId", subflow1Id.Value.ToString() } });
        var s2End = new NodeDefinition(NodeId.Create("end-2"), "end", new Dictionary<string, object>());
        var s2Workflow = new WorkflowDefinition(subflow2Id, "Subflow 2", new[] { s2Start, s2Sub, s2End }, Array.Empty<EdgeDefinition>());
        _provider.AddDefinition(s2Workflow);

        var parentId = WorkflowDefinitionId.New();
        var pStart = new NodeDefinition(NodeId.Create("start-p"), "start", new Dictionary<string, object>());
        var pSub = new NodeDefinition(NodeId.Create("sub-1-node"), "subflow", new Dictionary<string, object> { { "subflowId", subflow1Id.Value.ToString() } });
        var parentWorkflow = new WorkflowDefinition(parentId, "Parent", new[] { pStart, pSub }, Array.Empty<EdgeDefinition>());

        var result = await _compiler.CompileAsync(parentWorkflow);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ERR_RECURSIVE_SUBFLOW", diagnostic.Code);
    }

    [Fact]
    public async Task Fails_When_Required_Parameter_Is_Missing()
    {
        var customManifestProvider = new MockNodePackageManifestProvider();
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("start"),
            "1.0.0", "Start", "Triggers", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 0, new(), new(), new() { new("success") }
        ));
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("test-node"),
            "1.0.0", "Test Node", "Utility", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 10, new(),
            new() { new("req-param", "string", true, true) },
            new() { new("success") }
        ));

        var compiler = new WorkflowCompiler(_provider, customManifestProvider);

        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var testNode = new NodeDefinition(NodeId.Create("test-1"), "test-node", new Dictionary<string, object>()); // Missing 'req-param'
        var workflow = new WorkflowDefinition(WorkflowDefinitionId.New(), "Flow", new[] { startNode, testNode }, Array.Empty<EdgeDefinition>());

        var result = await compiler.CompileAsync(workflow);

        Assert.False(result.IsSuccess);
        var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == "ERR_MISSING_REQUIRED_PARAMETER");
        Assert.NotNull(diagnostic);
        Assert.Equal("test-1", diagnostic.NodeId?.Value);
    }

    [Fact]
    public async Task Fails_When_Parameter_Type_Is_Invalid_Number()
    {
        var customManifestProvider = new MockNodePackageManifestProvider();
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("start"),
            "1.0.0", "Start", "Triggers", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 0, new(), new(), new() { new("success") }
        ));
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("test-node"),
            "1.0.0", "Test Node", "Utility", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 10, new(),
            new() { new("num-param", "number", false, true) },
            new() { new("success") }
        ));

        var compiler = new WorkflowCompiler(_provider, customManifestProvider);

        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var testNode = new NodeDefinition(NodeId.Create("test-1"), "test-node", new Dictionary<string, object> { { "num-param", "not-a-number" } });
        var workflow = new WorkflowDefinition(WorkflowDefinitionId.New(), "Flow", new[] { startNode, testNode }, Array.Empty<EdgeDefinition>());

        var result = await compiler.CompileAsync(workflow);

        Assert.False(result.IsSuccess);
        var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == "ERR_INVALID_PARAMETER_TYPE");
        Assert.NotNull(diagnostic);
        Assert.Equal("test-1", diagnostic.NodeId?.Value);
    }

    [Fact]
    public async Task Fails_When_Parameter_Type_Is_Invalid_Boolean()
    {
        var customManifestProvider = new MockNodePackageManifestProvider();
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("start"),
            "1.0.0", "Start", "Triggers", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 0, new(), new(), new() { new("success") }
        ));
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("test-node"),
            "1.0.0", "Test Node", "Utility", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 10, new(),
            new() { new("bool-param", "bool", false, true) },
            new() { new("success") }
        ));

        var compiler = new WorkflowCompiler(_provider, customManifestProvider);

        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var testNode = new NodeDefinition(NodeId.Create("test-1"), "test-node", new Dictionary<string, object> { { "bool-param", "not-a-bool" } });
        var workflow = new WorkflowDefinition(WorkflowDefinitionId.New(), "Flow", new[] { startNode, testNode }, Array.Empty<EdgeDefinition>());

        var result = await compiler.CompileAsync(workflow);

        Assert.False(result.IsSuccess);
        var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == "ERR_INVALID_PARAMETER_TYPE");
        Assert.NotNull(diagnostic);
        Assert.Equal("test-1", diagnostic.NodeId?.Value);
    }

    [Fact]
    public async Task Succeeds_When_Parameter_Type_Is_Expression()
    {
        var customManifestProvider = new MockNodePackageManifestProvider();
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("start"),
            "1.0.0", "Start", "Triggers", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 0, new(), new(), new() { new("success") }
        ));
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("test-node"),
            "1.0.0", "Test Node", "Utility", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 10, new(),
            new() { new("num-param", "number", false, true) },
            new() { new("success") }
        ));

        var compiler = new WorkflowCompiler(_provider, customManifestProvider);

        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var testNode = new NodeDefinition(NodeId.Create("test-1"), "test-node", new Dictionary<string, object> { { "num-param", "{{ $node.start-1.success }}" } });
        var workflow = new WorkflowDefinition(WorkflowDefinitionId.New(), "Flow", new[] { startNode, testNode }, Array.Empty<EdgeDefinition>());

        var result = await compiler.CompileAsync(workflow);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Fails_When_Edge_Sockets_Are_Invalid()
    {
        var customManifestProvider = new MockNodePackageManifestProvider();
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("start"),
            "1.0.0", "Start", "Triggers", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 0, new(), new(), new() { new("success") }
        ));
        customManifestProvider.Register(new NodePackageManifest(
            new NodePackageId("test-node"),
            "1.0.0", "Test Node", "Utility", NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately, 10, new(),
            new() { new("param1", "string", false, true) },
            new() { new("success") }
        ));

        var compiler = new WorkflowCompiler(_provider, customManifestProvider);

        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var testNode = new NodeDefinition(NodeId.Create("test-1"), "test-node", new Dictionary<string, object>());

        // edge1: invalid output socket "nonexistent-out"
        // edge2: invalid input socket "nonexistent-in"
        var edge1 = new EdgeDefinition("edge-1", startNode.Id, "nonexistent-out", testNode.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", startNode.Id, "success", testNode.Id, "nonexistent-in");
        var workflow = new WorkflowDefinition(WorkflowDefinitionId.New(), "Flow", new[] { startNode, testNode }, new[] { edge1, edge2 });

        var result = await compiler.CompileAsync(workflow);

        Assert.False(result.IsSuccess);
        var outDiagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == "ERR_INVALID_SOCKET_MAPPING" && d.Message.Contains("output socket"));
        var inDiagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == "ERR_INVALID_SOCKET_MAPPING" && d.Message.Contains("input socket"));
        
        Assert.NotNull(outDiagnostic);
        Assert.Equal("start-1", outDiagnostic.NodeId?.Value);
        Assert.NotNull(inDiagnostic);
        Assert.Equal("test-1", inDiagnostic.NodeId?.Value);
    }

    private class MockNodePackageManifestProvider : INodePackageManifestProvider
    {
        private readonly Dictionary<NodePackageId, NodePackageManifest> _manifests = new();

        public void Register(NodePackageManifest manifest)
        {
            _manifests[manifest.Id] = manifest;
        }

        public Task<NodePackageManifest?> GetManifestAsync(NodePackageId packageId, CancellationToken cancellationToken = default)
        {
            _manifests.TryGetValue(packageId, out var manifest);
            return Task.FromResult<NodePackageManifest?>(manifest);
        }
    }
}
