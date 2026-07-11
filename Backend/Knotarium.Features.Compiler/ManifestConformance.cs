using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Compiler;

/// <summary>
/// Checks that a node's actual runtime output matches the field schema its manifest declares.
/// Compile-time field-safety (Phase B) is only sound if manifests don't drift from their tasks;
/// run this in a per-node conformance test to keep a declared <see cref="OutputDefinition.Fields"/>
/// honest. Anything declared "any" is not checked.
/// </summary>
public static class ManifestConformance
{
    public enum ViolationKind
    {
        MissingRequiredField,
        FieldTypeMismatch,
    }

    public sealed record FieldViolation(string OutputName, string FieldName, ViolationKind Kind, string? ActualType);

    /// <summary>Verifies a node's actual output dictionary delivers the required fields its output declares.</summary>
    public static IReadOnlyList<FieldViolation> CheckOutput(
        OutputDefinition output,
        IReadOnlyDictionary<string, object?> actualOutputs)
    {
        var violations = new List<FieldViolation>();
        if (output.Fields == null)
        {
            return violations;
        }

        foreach (var field in output.Fields.Where(f => f.Required))
        {
            if (!actualOutputs.TryGetValue(field.Name, out var value) || value is null)
            {
                violations.Add(new FieldViolation(output.Name, field.Name, ViolationKind.MissingRequiredField, null));
                continue;
            }

            var actualType = InferType(value);
            if (TypeCompatibility.IsKnown(field.Type) &&
                TypeCompatibility.IsKnown(actualType) &&
                !TypeCompatibility.IsAssignable(actualType, field.Type))
            {
                violations.Add(new FieldViolation(output.Name, field.Name, ViolationKind.FieldTypeMismatch, actualType));
            }
        }

        return violations;
    }

    /// <summary>Maps a CLR or <see cref="JsonElement"/> value to a canonical type name.</summary>
    public static string InferType(object? value)
    {
        switch (value)
        {
            case null:
                return TypeCompatibility.Any;
            case bool:
                return "boolean";
            case string:
                return "string";
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return "number";
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.String => "string",
                    JsonValueKind.Number => "number",
                    JsonValueKind.True or JsonValueKind.False => "boolean",
                    JsonValueKind.Object => "object",
                    JsonValueKind.Array => "array",
                    _ => TypeCompatibility.Any,
                };
            case IDictionary:
                return "object";
            case IEnumerable:
                return "array";
            default:
                return "object";
        }
    }
}
