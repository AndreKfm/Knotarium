// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knotarium.Core.Domain;

[JsonConverter(typeof(NodePackageVersionIdJsonConverter))]
public readonly record struct NodePackageVersionId(Guid Value)
{
    public static NodePackageVersionId New() => new(Guid.NewGuid());
    public static NodePackageVersionId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString();
}

public class NodePackageVersionIdJsonConverter : JsonConverter<NodePackageVersionId>
{
    public override NodePackageVersionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return value == null ? default : NodePackageVersionId.Parse(value);
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
            return value == null ? default : NodePackageVersionId.Parse(value);
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} when converting to NodePackageVersionId.");
    }

    public override void Write(Utf8JsonWriter writer, NodePackageVersionId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value.ToString());
    }
}
