using System;

namespace Knotarium.Features.Compiler;

/// <summary>
/// Scalar type lattice for compile-time edge type-checking (Phase A). Deliberately lenient:
/// "any" (the default for undeclared sockets/parameters) is compatible with everything, and
/// only clear mismatches are rejected, to keep false positives near zero during rollout.
/// </summary>
public static class TypeCompatibility
{
    public const string Any = "any";

    /// <summary>Collapses aliases to a canonical type: string, number, boolean, object, array, or any.</summary>
    public static string Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return Any;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "string" or "text" or "str" => "string",
            "number" or "int" or "integer" or "long" or "float" or "double" or "decimal" => "number",
            "bool" or "boolean" => "boolean",
            "object" or "json" or "record" or "map" => "object",
            "array" or "list" or "collection" => "array",
            // Enums and credential references are surfaced to the user as strings.
            "enum" or "credentialref" => "string",
            "any" => Any,
            // Unknown/custom type names stay permissive rather than noisy.
            _ => Any,
        };
    }

    /// <summary>True when the type is concrete enough to participate in a mismatch check.</summary>
    public static bool IsKnown(string? type) => Normalize(type) != Any;

    /// <summary>
    /// Whether a value of <paramref name="fromType"/> can flow into an input expecting
    /// <paramref name="toType"/>. "any" on either side always succeeds.
    /// </summary>
    public static bool IsAssignable(string? fromType, string? toType)
    {
        var from = Normalize(fromType);
        var to = Normalize(toType);

        if (from == Any || to == Any)
        {
            return true;
        }

        if (from == to)
        {
            return true;
        }

        return to switch
        {
            // String is a universal sink: every value has a textual form.
            "string" => true,
            // Numeric/boolean inputs also accept strings (which may carry the value).
            "number" => from == "string",
            "boolean" => from == "string",
            // Structured inputs require a matching structured source.
            "object" => false,
            "array" => false,
            _ => false,
        };
    }
}
