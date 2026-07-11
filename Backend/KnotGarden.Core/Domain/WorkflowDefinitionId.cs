using System;
using System.Text.Json.Serialization;

namespace KnotGarden.Core.Domain;

[JsonConverter(typeof(WorkflowDefinitionIdJsonConverter))]
public readonly record struct WorkflowDefinitionId(string Value)
{
    public static WorkflowDefinitionId New() => new(Guid.NewGuid().ToString());
    
    public static WorkflowDefinitionId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("WorkflowDefinitionId cannot be null or whitespace.", nameof(value));
        
        return new WorkflowDefinitionId(value);
    }

    public static WorkflowDefinitionId Parse(string value) => Create(value);
    
    public override string ToString() => Value;
}
