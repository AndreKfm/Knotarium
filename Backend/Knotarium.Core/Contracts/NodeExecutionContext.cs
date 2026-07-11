using Knotarium.Core.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Knotarium.Core.Contracts;

public class VariableBag
{
    private readonly IDictionary<string, object> _variables;

    public VariableBag(IDictionary<string, object> variables)
    {
        _variables = variables ?? new Dictionary<string, object>();
    }

    public T? Get<T>(string name)
    {
        if (!_variables.TryGetValue(name, out var val))
            return default;

        if (val is T typedVal)
            return typedVal;

        if (val is JsonElement element)
        {
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<T>(element.GetRawText(), options);
            }
            catch
            {
                return default;
            }
        }

        try
        {
            return (T)Convert.ChangeType(val, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return default;
        }
    }

    public void Set(string name, object? value)
    {
        if (value == null)
            _variables.Remove(name);
        else
            _variables[name] = value;
    }
}

public record NodeExecutionContext(
    WorkflowDefinitionId WorkflowId,
    Guid ExecutionId,
    NodeId NodeId,
    IReadOnlyDictionary<string, object> Inputs,
    IDictionary<string, object> GlobalVariables,
    // The live workflow state, exposed so a task can resolve its OWN references with found-ness
    // (e.g. the Condition node — see D7). Optional so existing callers/tests need no change; when
    // null, a task must fall back to GlobalVariables.
    IWorkflowState? State = null
)
{
    public VariableBag Variables { get; } = new(GlobalVariables);
}

