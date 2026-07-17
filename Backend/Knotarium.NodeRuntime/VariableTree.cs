// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Knotarium.NodeRuntime;

/// <summary>
/// Raised when a deep-set conflicts with the existing structure (e.g. a member
/// write into an array, an index write into an object, or a scalar where a container
/// is required). Surfaced as a node failure. (Out-of-range array indices are not a
/// conflict — the array auto-grows, padding gaps with null.)
/// </summary>
public sealed class VariableTreeException : Exception
{
    public VariableTreeException(string message) : base(message) { }
}

/// <summary>
/// Mutable-tree helpers for path-aware variable writes. <see cref="JsonElement"/>
/// is immutable, so a write materializes the current value into a mutable tree
/// (<see cref="Dictionary{TKey,TValue}"/> for objects, <see cref="List{T}"/> for
/// arrays), deep-sets the leaf, and the caller re-stores the result.
/// </summary>
public static class VariableTree
{
    /// <summary>Materialize a JsonElement into a mutable object/list/scalar tree.</summary>
    public static object? ToMutable(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                    dict[prop.Name] = ToMutable(prop.Value);
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                    list.Add(ToMutable(item));
                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>
    /// Deep-set <paramref name="leafValue"/> at <paramref name="segments"/> within
    /// <paramref name="root"/> (which may be null/absent), auto-creating missing
    /// intermediate containers. Returns the new root. Throws
    /// <see cref="VariableTreeException"/> on a structural conflict.
    /// </summary>
    public static object? Set(object? root, IReadOnlyList<PathSegment> segments, object? leafValue)
    {
        if (segments.Count == 0)
            return leafValue;

        // The root container's type is dictated by the first segment.
        root = EnsureContainer(root, segments[0]);

        object? current = root;
        for (int i = 0; i < segments.Count; i++)
        {
            bool isLeaf = i == segments.Count - 1;
            var segment = segments[i];

            if (segment is PathSegment.Member member)
            {
                if (current is not Dictionary<string, object?> dict)
                    throw new VariableTreeException(
                        $"Cannot set member '{member.Name}': the value at this path is not an object.");

                if (isLeaf)
                {
                    dict[member.Name] = leafValue;
                }
                else
                {
                    if (!dict.TryGetValue(member.Name, out var child) || child is null)
                    {
                        child = NewContainer(segments[i + 1]);
                        dict[member.Name] = child;
                    }
                    current = child;
                }
            }
            else if (segment is PathSegment.Index index)
            {
                if (current is not List<object?> list)
                    throw new VariableTreeException(
                        $"Cannot set index [{index.Value}]: the value at this path is not an array.");

                // Auto-grow: pad with nulls up to the target index so any index can be written
                // (a[1] on an empty array yields [null, value]). Mirrors JS array assignment.
                while (list.Count <= index.Value)
                    list.Add(null);

                if (isLeaf)
                {
                    list[index.Value] = leafValue;
                }
                else
                {
                    var child = list[index.Value];
                    if (child is null)
                    {
                        child = NewContainer(segments[i + 1]);
                        list[index.Value] = child;
                    }
                    current = child;
                }
            }
        }

        return root;
    }

    // Coerce/validate the root against the kind required by the first segment.
    private static object? EnsureContainer(object? value, PathSegment first)
    {
        bool wantObject = first is PathSegment.Member;
        if (value is null)
            return NewContainer(first);

        if (wantObject && value is not Dictionary<string, object?>)
            throw new VariableTreeException(
                "Cannot navigate by member: the variable's current value is not an object.");
        if (!wantObject && value is not List<object?>)
            throw new VariableTreeException(
                "Cannot navigate by index: the variable's current value is not an array.");

        return value;
    }

    private static object NewContainer(PathSegment next)
        => next is PathSegment.Index
            ? new List<object?>()
            : new Dictionary<string, object?>();
}
