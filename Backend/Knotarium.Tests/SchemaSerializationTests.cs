// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Knotarium.Tests;

public class SchemaSerializationTests
{
    [Fact]
    public void NodeId_Serializes_And_Deserializes_As_String()
    {
        var id = NodeId.Create("node-1");
        var json = JsonSerializer.Serialize(id);
        Assert.Equal("\"node-1\"", json);

        var deserialized = JsonSerializer.Deserialize<NodeId>(json);
        Assert.Equal(id, deserialized);
    }

    [Fact]
    public void WorkflowDefinitionId_Serializes_And_Deserializes_As_Guid_String()
    {
        var guid = Guid.NewGuid();
        var id = new WorkflowDefinitionId(guid.ToString());
        var json = JsonSerializer.Serialize(id);
        Assert.Equal($"\"{guid}\"", json);

        var deserialized = JsonSerializer.Deserialize<WorkflowDefinitionId>(json);
        Assert.Equal(id, deserialized);
    }

    [Fact]
    public void WorkflowDefinition_Serializes_And_Deserializes_Correctly()
    {
        var id = WorkflowDefinitionId.New();
        var node1 = new NodeDefinition(NodeId.Create("start-1"), "Start", new Dictionary<string, object>());
        var node2 = new NodeDefinition(NodeId.Create("end-1"), "End", new Dictionary<string, object>());
        var edge = new EdgeDefinition("edge-1", node1.Id, "success", node2.Id, "in");

        var workflow = new WorkflowDefinition(
            id,
            "My Workflow",
            new[] { node1, node2 },
            new[] { edge }
        );

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(workflow, options);
        var deserialized = JsonSerializer.Deserialize<WorkflowDefinition>(json, options);

        Assert.NotNull(deserialized);
        Assert.Equal(id, deserialized.Id);
        Assert.Equal("My Workflow", deserialized.Name);
        Assert.Equal(2, deserialized.Nodes.Count);
        Assert.Equal("start-1", deserialized.Nodes[0].Id.Value);
        Assert.Single(deserialized.Edges);
    }
}
