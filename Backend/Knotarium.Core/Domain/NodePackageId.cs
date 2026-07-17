// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knotarium.Core.Domain;

[JsonConverter(typeof(NodePackageIdJsonConverter))]
public readonly record struct NodePackageId(string Value)
{
    public static NodePackageId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("NodePackageId cannot be null or whitespace.", nameof(value));
        
        return new NodePackageId(value);
    }

    public override string ToString() => Value;
}

public class NodePackageIdJsonConverter : JsonConverter<NodePackageId>
{
    public override NodePackageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return value == null ? default : NodePackageId.Create(value);
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
            return value == null ? default : NodePackageId.Create(value);
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} when converting to NodePackageId.");
    }

    public override void Write(Utf8JsonWriter writer, NodePackageId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
