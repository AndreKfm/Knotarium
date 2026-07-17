// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Knotarium.Features.Condition;

/// <summary>
/// The exhaustive legacy <see cref="ConditionOperator"/> → OperatorId map (B6) and the conversion of a
/// legacy <c>left/operator/right</c> node into a single-comparator <see cref="ResolvedCondition"/>.
/// A contract test asserts every legacy enum value is mapped. An unmappable operator name is foreign/
/// corrupt data → <see cref="ConditionErrorCode.INVALID_LOGIC"/> (D4) — never a silent default.
/// </summary>
public static class LegacyConditionMap
{
    // Exhaustive — keyed by the SHIPPED legacy enum. not-equals → "ne" (the spec id), per the D7 rider.
    public static readonly IReadOnlyDictionary<ConditionOperator, string> OperatorIds =
        new Dictionary<ConditionOperator, string>
        {
            [ConditionOperator.Equal] = "eq",
            [ConditionOperator.NotEqual] = "ne",
            [ConditionOperator.GreaterThan] = "gt",
            [ConditionOperator.LessThan] = "lt",
            [ConditionOperator.GreaterThanOrEqual] = "gte",
            [ConditionOperator.LessThanOrEqual] = "lte",
            [ConditionOperator.Contains] = "contains",
        };

    public static bool TryMapOperatorName(string? legacyName, out string operatorId)
    {
        operatorId = string.Empty;
        if (string.IsNullOrWhiteSpace(legacyName)) return false;
        if (!Enum.TryParse<ConditionOperator>(legacyName, ignoreCase: true, out var op)) return false;
        return OperatorIds.TryGetValue(op, out operatorId!);
    }

    /// <summary>
    /// Build a resolved single-comparator condition from a legacy node's ALREADY-RESOLVED operand
    /// values (legacy <c>left</c>/<c>right</c> are Expression:true, so the executor resolved them
    /// before the task ran). Declared types are inferred from the runtime values.
    /// </summary>
    public static bool TryBuildResolved(object? left, string? operatorName, object? right,
        out ResolvedCondition? condition, out ConditionError? error)
    {
        condition = null;
        error = null;

        if (!TryMapOperatorName(operatorName, out var opId))
        {
            error = new ConditionError(ConditionErrorCode.INVALID_LOGIC,
                $"Unknown legacy operator '{operatorName}'.", "legacy", null);
            return false;
        }

        var a = ResolvedOperand.Value(InferType(left), left);
        ResolvedOperand? b = ConditionOperatorCatalog.TryGet(opId, out var def) && def.Arity == OperatorArity.Binary
            ? ResolvedOperand.Value(InferType(right), right)
            : null;

        condition = new ResolvedCondition(Combinator.And, new[] { new ResolvedComparator("legacy", opId, a, b) });
        return true;
    }

    // Infer the declared type from an already-resolved runtime value. null/unknown → String (a null
    // operand coerces to null regardless of declared type, so the choice is immaterial for nulls).
    private static OperandType InferType(object? value)
    {
        switch (value)
        {
            case null: return OperandType.String;
            case bool: return OperandType.Boolean;
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return OperandType.Number;
            case string: return OperandType.String;
            case JsonElement je:
                return je.ValueKind switch
                {
                    JsonValueKind.Number => OperandType.Number,
                    JsonValueKind.True or JsonValueKind.False => OperandType.Boolean,
                    _ => OperandType.String,
                };
            default:
                // arrays/objects/other: treat as String so existence/text ops still see the raw value.
                return OperandType.String;
        }
    }
}
