// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Knotarium.Core.Domain;

[JsonConverter(typeof(NodeIdJsonConverter))]
public readonly record struct NodeId(string Value)
{
    public static NodeId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("NodeId cannot be null or whitespace.", nameof(value));
        
        return new NodeId(value);
    }

    public override string ToString() => Value;
}
