using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knotarium.Core.Domain;

[JsonConverter(typeof(ExecutionInstanceIdJsonConverter))]
public readonly record struct ExecutionInstanceId(Guid Value)
{
    public static ExecutionInstanceId New() => new(Guid.NewGuid());
    public static ExecutionInstanceId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString();
}

public class ExecutionInstanceIdJsonConverter : JsonConverter<ExecutionInstanceId>
{
    public override ExecutionInstanceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return value == null ? default : ExecutionInstanceId.Parse(value);
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
            return value == null ? default : ExecutionInstanceId.Parse(value);
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} when converting to ExecutionInstanceId.");
    }

    public override void Write(Utf8JsonWriter writer, ExecutionInstanceId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value.ToString());
    }
}
