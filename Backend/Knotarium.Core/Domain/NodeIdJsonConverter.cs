using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knotarium.Core.Domain;

public class NodeIdJsonConverter : JsonConverter<NodeId>
{
    public override NodeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return value == null ? default : NodeId.Create(value);
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
            return value == null ? default : NodeId.Create(value);
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} when converting to NodeId.");
    }

    public override void Write(Utf8JsonWriter writer, NodeId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
