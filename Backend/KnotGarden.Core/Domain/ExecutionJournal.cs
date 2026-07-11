using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnotGarden.Core.Domain;

public class ExecutionJournal
{
    public Guid Id { get; set; }
    public ExecutionInstanceId ExecutionInstanceId { get; set; }
    public NodeId? NodeId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
}

[JsonConverter(typeof(JournalEntryJsonConverter))]
public record JournalEntry(
    Guid Id,
    Guid ExecutionInstanceId,
    string EventType,
    string Payload,
    string PayloadVersion = "v2",
    DateTime CreatedAtUtc = default
);

public class JournalEntryJsonConverter : JsonConverter<JournalEntry>
{
    public override JournalEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        
        var id = root.GetProperty("Id").GetGuid();
        var execId = root.GetProperty("ExecutionInstanceId").GetGuid();
        var eventType = root.GetProperty("EventType").GetString() ?? "";
        var payload = root.GetProperty("Payload").GetString() ?? "";
        
        var payloadVersion = "v1";
        if (root.TryGetProperty("PayloadVersion", out var versionProp))
        {
            payloadVersion = versionProp.GetString() ?? "v1";
        }
        
        DateTime createdAt = default;
        if (root.TryGetProperty("CreatedAtUtc", out var createdProp))
        {
            createdAt = createdProp.GetDateTime();
        }
        
        return new JournalEntry(id, execId, eventType, payload, payloadVersion, createdAt);
    }

    public override void Write(Utf8JsonWriter writer, JournalEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", value.Id);
        writer.WriteString("ExecutionInstanceId", value.ExecutionInstanceId);
        writer.WriteString("EventType", value.EventType);
        writer.WriteString("Payload", value.Payload);
        writer.WriteString("PayloadVersion", value.PayloadVersion);
        writer.WriteString("CreatedAtUtc", value.CreatedAtUtc);
        writer.WriteEndObject();
    }
}
