using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Execution;

public class JournalFoldService
{
    public (Dictionary<string, JsonElement> Variables, ExecutionStatus Status) FoldJournal(IEnumerable<ExecutionJournal> entries)
    {
        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var status = ExecutionStatus.Pending;

        // Sequence events chronologically based on Timestamp
        foreach (var entry in entries.OrderBy(e => e.Timestamp))
        {
            switch (entry.EventType)
            {
                case JournalEventTypes.WorkflowStarted:
                    status = ExecutionStatus.Running;
                    break;

                case JournalEventTypes.WorkflowSuspended:
                    status = ExecutionStatus.Suspended;
                    
                    // Reconstruct from suspension metadata carrying variable snapshots
                    if (entry.Data != null && entry.Data.TryGetValue("Variables", out var varsObj))
                    {
                        var varsJson = JsonSerializer.Serialize(varsObj);
                        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(varsJson);
                        if (dict != null)
                        {
                            foreach (var kvp in dict)
                            {
                                variables[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    break;

                case JournalEventTypes.WorkflowResumed:
                    status = ExecutionStatus.Running;
                    
                    // Bind outputs recorded during resume callbacks
                    if (entry.NodeId.HasValue && entry.Data != null && entry.Data.TryGetValue("Output", out var outputObj))
                    {
                        var outputJson = JsonSerializer.Serialize(outputObj);
                        var element = JsonSerializer.Deserialize<JsonElement>(outputJson);
                        
                        var nodeIdValue = entry.NodeId.Value.Value;
                        variables[nodeIdValue + ".output"] = element;
                    }
                    break;

                case JournalEventTypes.NodeExecutionCompleted:
                    // Extract node outputs from data payload (Must Fix — fold outputs for complete rehydration)
                    if (entry.NodeId.HasValue && entry.Data != null)
                    {
                        var nodeIdValue = entry.NodeId.Value.Value;
                        foreach (var kvp in entry.Data)
                        {
                            if (kvp.Key != "_v") // Skip schema version labels
                            {
                                var valJson = JsonSerializer.Serialize(kvp.Value);
                                var element = JsonSerializer.Deserialize<JsonElement>(valJson);
                                variables[nodeIdValue + "." + kvp.Key] = element;
                            }
                        }
                    }
                    break;

                case JournalEventTypes.WorkflowCompleted:
                    status = ExecutionStatus.Completed;
                    break;

                case JournalEventTypes.WorkflowFailed:
                    status = ExecutionStatus.Failed;
                    break;
            }
        }

        return (variables, status);
    }
}
