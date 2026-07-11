using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knotarium.Core.Domain;

public class WorkflowDefinitionIdJsonConverter : JsonConverter<WorkflowDefinitionId>
{
    public override WorkflowDefinitionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return value == null ? default : WorkflowDefinitionId.Parse(value);
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? value = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propertyName = reader.GetString();
                    reader.Read();
                    if (string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase))
                    {
                        value = reader.GetString();
                    }
                }
            }
            return value == null ? default : WorkflowDefinitionId.Parse(value);
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} when converting to WorkflowDefinitionId.");
    }

    public override void Write(Utf8JsonWriter writer, WorkflowDefinitionId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
