using System.Text.Json;

namespace Knotarium.Core.Domain;

/// <summary>
/// Canonical <see cref="JsonSerializerOptions"/> for persisting and serializing domain graphs:
/// the strongly-typed id converters plus compact (non-indented) output. Lives in Core so the
/// portability serializer and other Core-only leaves can reuse the exact same shape the EF value
/// converters use, without taking an Infrastructure dependency.
/// </summary>
public static class PersistenceJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        Converters =
        {
            new NodeIdJsonConverter(),
            new WorkflowDefinitionIdJsonConverter()
        },
        WriteIndented = false
    };
}
