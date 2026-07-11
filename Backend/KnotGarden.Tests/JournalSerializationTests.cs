using System;
using System.Text.Json;
using KnotGarden.Core.Domain;
using Xunit;

namespace KnotGarden.Tests;

public class JournalSerializationTests
{
    [Fact]
    public void EnumIndexSafety_ExecutionStatus_MapsExactly()
    {
        Assert.Equal(0, (int)ExecutionStatus.Pending);
        Assert.Equal(1, (int)ExecutionStatus.Running);
        Assert.Equal(2, (int)ExecutionStatus.Suspended);
        Assert.Equal(3, (int)ExecutionStatus.Cancelled);
        Assert.Equal(4, (int)ExecutionStatus.Completed);
        Assert.Equal(5, (int)ExecutionStatus.Failed);
        Assert.Equal(6, (int)ExecutionStatus.WaitingForRetry);
    }

    [Fact]
    public void EnumIndexSafety_NodeExecutionStatus_MapsExactly()
    {
        Assert.Equal(0, (int)NodeExecutionStatus.Pending);
        Assert.Equal(1, (int)NodeExecutionStatus.Running);
        Assert.Equal(2, (int)NodeExecutionStatus.Succeeded);
        Assert.Equal(3, (int)NodeExecutionStatus.Failed);
        Assert.Equal(4, (int)NodeExecutionStatus.Retrying);
        Assert.Equal(5, (int)NodeExecutionStatus.RequiresManualDecision);
        Assert.Equal(6, (int)NodeExecutionStatus.TimedOut);
        Assert.Equal(7, (int)NodeExecutionStatus.Cancelled);
    }

    [Fact]
    public void SerializationSafety_WritesPayloadVersionV2()
    {
        var id = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;
        var entry = new JournalEntry(
            Id: id,
            ExecutionInstanceId: execId,
            EventType: "WorkflowSuspended",
            Payload: "{\"SuspendedNodeId\":\"delay-1\",\"Variables\":{}}",
            PayloadVersion: "v2",
            CreatedAtUtc: createdAt
        );

        var json = JsonSerializer.Serialize(entry);

        // Verify that PayloadVersion "v2" is written in serialized JSON
        Assert.Contains("\"PayloadVersion\":\"v2\"", json);

        // Deserializing should preserve "v2"
        var deserialized = JsonSerializer.Deserialize<JournalEntry>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("v2", deserialized.PayloadVersion);
        Assert.Equal(id, deserialized.Id);
        Assert.Equal(execId, deserialized.ExecutionInstanceId);
        Assert.Equal("WorkflowSuspended", deserialized.EventType);
        Assert.Equal("{\"SuspendedNodeId\":\"delay-1\",\"Variables\":{}}", deserialized.Payload);
    }

    [Fact]
    public void BackwardCompatibility_DefaultsMissingVersionToV1()
    {
        var id = Guid.NewGuid();
        var execId = Guid.NewGuid();
        
        // JSON representation of legacy v1 Journal entry (omits PayloadVersion)
        var legacyJson = "{" +
            $"\"Id\":\"{id}\"," +
            $"\"ExecutionInstanceId\":\"{execId}\"," +
            "\"EventType\":\"WorkflowSuspended\"," +
            "\"Payload\":\"{\\\"SuspendedNodeId\\\":\\\"delay-1\\\"}\"," +
            "\"CreatedAtUtc\":\"2026-05-30T19:00:00Z\"" +
            "}";

        var deserialized = JsonSerializer.Deserialize<JournalEntry>(legacyJson);

        Assert.NotNull(deserialized);
        Assert.Equal("v1", deserialized.PayloadVersion); // Assert legacy defaults to v1
        Assert.Equal(id, deserialized.Id);
        Assert.Equal(execId, deserialized.ExecutionInstanceId);
        Assert.Equal("WorkflowSuspended", deserialized.EventType);
        Assert.Equal("{\"SuspendedNodeId\":\"delay-1\"}", deserialized.Payload);
        Assert.Equal(DateTime.Parse("2026-05-30T19:00:00Z").ToUniversalTime(), deserialized.CreatedAtUtc.ToUniversalTime());
    }
}
