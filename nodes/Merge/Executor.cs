// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Nodes;

public class MergeExecutor : INodeExecutor
{
    public ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken)
    {
        var merged = new List<JsonElement>();

        try
        {
            foreach (var parameterName in new[] { "array1", "array2" })
            {
                if (input.Parameters.TryGetValue(parameterName, out var elem) && elem.ValueKind != JsonValueKind.Null)
                {
                    if (elem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in elem.EnumerateArray())
                        {
                            merged.Add(item);
                        }
                    }
                    else if (elem.ValueKind == JsonValueKind.String)
                    {
                        var strVal = elem.GetString();
                        if (!string.IsNullOrWhiteSpace(strVal))
                        {
                            var trimmed = strVal.Trim();
                            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                            {
                                using var doc = JsonDocument.Parse(trimmed);
                                foreach (var item in doc.RootElement.EnumerateArray())
                                {
                                    merged.Add(item.Clone());
                                }
                            }
                            else
                            {
                                merged.Add(elem);
                            }
                        }
                    }
                    else
                    {
                        merged.Add(elem);
                    }
                }
            }

            var payload = JsonSerializer.SerializeToElement(merged);
            return new ValueTask<NodeResult>(new NodeResult("success", payload, NodeExecutionStatus.Succeeded));
        }
        catch (Exception ex)
        {
            return new ValueTask<NodeResult>(new NodeResult("error", 
                JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = ex.Message }), 
                NodeExecutionStatus.Failed));
        }
    }
}
