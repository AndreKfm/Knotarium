using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime;
using Knotarium.Features.Compiler;

namespace Knotarium.Features.Nodes;

public class ForLoopNodeTask : INodeTask
{
    private readonly InMemoryNodePackageManifestProvider _manifestProvider;

    public ForLoopNodeTask(InMemoryNodePackageManifestProvider manifestProvider)
    {
        _manifestProvider = manifestProvider;
    }

    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // 1. Determine the mode
        var mode = "foreach";
        if (context.Inputs.TryGetValue("mode", out var modeObj) && modeObj != null)
        {
            mode = modeObj.ToString()?.ToLowerInvariant() ?? "foreach";
        }

        // Define state keys unique to this loop node
        string indexKey = $"__loop_{context.NodeId}_index";
        string countKey = $"__loop_{context.NodeId}_count";
        string itemsKey = $"__loop_{context.NodeId}_items";

        // Detect a loop-back iteration from the loop's OWN state — the count global is set on the first
        // pass and cleared when the loop finishes, so its presence uniquely means "a loop is in progress".
        // We must NOT key off an "end" input: "end" is also a settable node parameter (and the name of the
        // loop-back input port), so a stray value there made the very first run look like a loop-back —
        // initialization was skipped, totalCount stayed 0, and the loop exited immediately with zero
        // iterations (the body never ran).
        bool isLoopback = context.GlobalVariables.ContainsKey(countKey);

        int currentIndex = 0;
        int totalCount = 0;

        if (isLoopback)
        {
            // Retrieve current state from global variables
            if (context.GlobalVariables.TryGetValue(indexKey, out var idxObj) && idxObj != null)
            {
                currentIndex = Convert.ToInt32(idxObj.ToString());
            }
            if (context.GlobalVariables.TryGetValue(countKey, out var cntObj) && cntObj != null)
            {
                totalCount = Convert.ToInt32(cntObj.ToString());
            }

            // Increment index for loopback
            currentIndex++;
        }
        else
        {
            // First run: Initialize variables
            if (mode == "count")
            {
                if (context.Inputs.TryGetValue("count", out var countVal) && countVal != null)
                {
                    totalCount = Convert.ToInt32(countVal.ToString());
                }
            }
            else // foreach mode
            {
                var items = new List<object>();
                if (context.Inputs.TryGetValue("collection", out var colObj) && colObj != null)
                {
                    if (colObj is string colStr)
                    {
                        try
                        {
                            items = JsonSerializer.Deserialize<List<object>>(colStr) ?? new();
                        }
                        catch
                        {
                            items = colStr.Split(',').Select(s => (object)s.Trim()).ToList();
                        }
                    }
                    else if (colObj is JsonElement elem && elem.ValueKind == JsonValueKind.Array)
                    {
                        items = JsonSerializer.Deserialize<List<object>>(elem.GetRawText()) ?? new();
                    }
                    else if (colObj is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            items.Add(item);
                        }
                    }
                    else
                    {
                        items.Add(colObj);
                    }
                }
                totalCount = items.Count;
                context.GlobalVariables[itemsKey] = items;
            }

            context.GlobalVariables[countKey] = totalCount;
        }

        // Check if we should iterate
        if (currentIndex < totalCount)
        {
            // Save current index
            context.GlobalVariables[indexKey] = currentIndex;

            // Determine the current item value
            object? currentItem = currentIndex;
            if (mode != "count")
            {
                if (context.GlobalVariables.TryGetValue(itemsKey, out var itemsObj) && itemsObj != null)
                {
                    try
                    {
                        var itemsStr = itemsObj is string s ? s : JsonSerializer.Serialize(itemsObj);
                        var items = JsonSerializer.Deserialize<List<object>>(itemsStr) ?? new();
                        if (currentIndex < items.Count)
                        {
                            currentItem = items[currentIndex];
                        }
                    }
                    catch
                    {
                        currentItem = null;
                    }
                }
            }

            // Outputs for the loop iteration
            var iterationPayload = new Dictionary<string, object>
            {
                ["selectedPort"] = "start",
                ["index"] = currentIndex,
                ["item"] = currentItem ?? currentIndex,
            };

            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(iterationPayload));
        }
        else
        {
            // Loop is complete! Clean up loop state from global variables
            context.GlobalVariables.Remove(indexKey);
            context.GlobalVariables.Remove(countKey);
            context.GlobalVariables.Remove(itemsKey);

            // Output payload for exit
            var exitPayload = new Dictionary<string, object>
            {
                ["selectedPort"] = "success",
            };

            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(exitPayload));
        }
    }
}
