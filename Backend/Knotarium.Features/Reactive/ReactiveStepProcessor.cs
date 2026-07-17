// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text.Json;
using Knotarium.Core.Contracts;
using Knotarium.Core.Reactive;

namespace Knotarium.Features.Reactive;

/// <summary>
/// Runs a reactive rule's ordered path steps against an inbound signal to decide whether its effects
/// fire. Transform steps (Set Variable(s)) mutate the <see cref="ReactiveContext"/> in order; guard
/// steps (Condition) gate the path. Processing short-circuits on the first guard that fails closed.
///
/// Transform values: a literal is set as-is; a <c>variable_ref</c> copies whatever the named reference
/// currently resolves to (an earlier wire variable, a payload field, or an envelope convenience); an
/// expression (<c>{{ }}</c>) value is unsupported on the wire and leaves the variable unset.
/// </summary>
public static class ReactiveStepProcessor
{
    /// <summary>True when every guard on the path clears (after applying the transforms before it).</summary>
    public static bool Passes(IReadOnlyList<ReactiveStep> steps, InboundEnvelope envelope)
    {
        if (steps is null || steps.Count == 0)
        {
            return true;
        }

        var context = new ReactiveContext(envelope);
        foreach (var step in steps)
        {
            switch (step)
            {
                case ReactiveTransform transform:
                    Apply(transform, context);
                    break;
                case ReactiveGuard guard:
                    if (!ReactiveConditionEvaluator.Passes(guard, context))
                    {
                        return false;
                    }
                    break;
            }
        }
        return true;
    }

    private static void Apply(ReactiveTransform transform, ReactiveContext context)
    {
        foreach (var assignment in transform.Assignments)
        {
            if (ReactiveRefs.IsVariableRef(assignment.Value))
            {
                var name = ReactiveRefs.ReadVariableName(assignment.Value);
                if (name is not null && context.TryResolve(name, out var resolved))
                {
                    context.Set(assignment.Name, resolved);
                }
                // unresolved ref → leave the target variable unset (a later condition fails closed on it)
                continue;
            }

            // A bare string carrying an expression is unsupported on the wire → skip (leave unset).
            if (assignment.Value is string s && s.Contains("{{") && s.Contains("}}"))
            {
                continue;
            }
            if (assignment.Value is JsonElement je && je.ValueKind == JsonValueKind.String)
            {
                var str = je.GetString();
                if (str is not null && str.Contains("{{") && str.Contains("}}"))
                {
                    continue;
                }
            }

            // Literal (string / number / boolean / object / array) → set as-is.
            context.Set(assignment.Name, assignment.Value);
        }
    }
}
