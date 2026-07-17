// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Ai;

/// <summary>
/// Assembles the prompts handed to the model for workflow generation. The <em>system</em> prompt is
/// stable per request set — role, the inline node catalog (<see cref="CatalogProjection"/>), the rules
/// the <c>WorkflowCompiler</c> will later enforce, and the exact output contract. The <em>user</em>
/// message carries the natural-language intent and, on a repair pass, the compiler/parse errors from the
/// previous attempt.
///
/// The output contract is a deliberately <b>flat</b> JSON shape (string ids, no <c>{ "value": … }</c>
/// wrappers, no coordinates) — easier and less error-prone for the model than the domain record shape.
/// The generator's parser bridges this flat shape onto <c>WorkflowDefinition</c>; geometry is assigned
/// afterward by auto-layout.
/// </summary>
public static class GenerationPromptBuilder
{
    public static string BuildSystemPrompt(
        IEnumerable<NodePackageManifest> manifests,
        IReadOnlySet<string>? excludedCategories = null)
    {
        var catalog = CatalogProjection.ProjectAndRender(manifests, excludedCategories);

        var sb = new StringBuilder();
        sb.Append(
"""
You design automation workflows for Knotarium, a node-based workflow engine. Given a user's intent,
you produce ONE workflow as a directed graph of nodes connected by edges.

# Available node types

Each entry is `id (Display Name) [TRIGGER] — Category: optional description`, then its parameters and
output ports. Prefer the node whose Category and description best match the user's intent — match on what
a node *does*, not just a similar-looking name.
A parameter reads `name:type`; a trailing `!` means required; `type(a|b|c)` lists the allowed enum
values; a quoted string is a usage hint. Output ports are listed as `a|b`.

""");
        sb.Append(catalog);
        sb.Append(
"""

# Rules

- Use ONLY node `type` values from the catalog above. An unknown type fails compilation.
- Every workflow begins with exactly one [TRIGGER] node (usually `manualTrigger` unless the intent
  implies a schedule, webhook, or poll). Trigger nodes must have NO incoming edges.
- Give every node a short, unique, stable `id` (e.g. "n1", "fetch", "notify"). Edges reference nodes
  by these ids — every edge's `from` and `to` must match a node `id` you defined.
- An edge's `output` must be one of the SOURCE node's listed output ports. Its `input` is the TARGET
  node's control entry — use `"in"` unless wiring into a specific named parameter/data input.
- Put each node's configuration in its `properties` object, keyed by parameter name. Provide every
  required (`!`) parameter with a non-empty value.
- Reference an upstream node's output inline in a property with `{{ $node.<nodeId>.output.<field> }}` —
  e.g. a `forLoop`'s current iteration index is `{{ $node.<loopId>.output.index }}` and its current item
  is `{{ $node.<loopId>.output.item }}`. Do NOT add a `setVariable` node just to copy such a value into a
  variable and read it back; wire the expression straight into the consuming parameter. Use `setVariable`
  only to accumulate a value across iterations or to hold a genuinely computed/transformed result.
- The graph must be acyclic except through loop constructs (e.g. `forLoop`).
- A loop body MUST close the cycle back to the loop, or the body runs only once. For a `forLoop` /
  `parallelForEach`: wire the loop's `start` output into the first body node, chain the body, then wire the
  LAST body node's output back into the loop node with `input: "end"` — that loop-back is what advances the
  loop to the next iteration. The loop emits `start` for each iteration and `success` when finished; wire
  `success` to whatever runs after the loop (or leave it unconnected if the workflow ends there). Example
  edges for a 1-node body: `{from: loop, output: "start", to: body, input: "in"}`,
  `{from: body, output: "result", to: loop, input: "end"}`, `{from: loop, output: "success", to: after, input: "in"}`.
- Do NOT invent credentials, API keys, or connection ids. When a node needs one, set that property to a
  placeholder of the form `slot:<kebab-case-name>` (e.g. `slot:weather-api`). The user binds real
  credentials after generation.
- Do NOT include node positions/coordinates — layout is assigned automatically.

# Output

Respond with a SINGLE JSON object and nothing else — no prose, no markdown fences. Shape:

{
  "name": "<short workflow name>",
  "nodes": [
    { "id": "n1", "type": "manualTrigger", "properties": {} },
    { "id": "n2", "type": "httpRequest", "properties": { "url": "https://…", "method": "GET" } }
  ],
  "edges": [
    { "id": "e1", "from": "n1", "output": "result", "to": "n2", "input": "in" }
  ]
}
""");
        return sb.ToString();
    }

    /// <summary>
    /// The per-attempt user message: the intent, plus — on a repair pass — the exact errors the previous
    /// attempt produced so the model corrects the specific failures rather than regenerating blind. When
    /// <paramref name="currentWorkflow"/> is supplied, the model MODIFIES that workflow (returning the
    /// complete updated version) instead of building a new one from scratch.
    /// </summary>
    public static string BuildUserMessage(
        string intent,
        IReadOnlyList<string>? priorErrors = null,
        WorkflowDefinition? currentWorkflow = null)
    {
        var sb = new StringBuilder();

        if (currentWorkflow is not null)
        {
            sb.Append(
                "You are MODIFYING an existing workflow. Its current definition (same flat shape you must " +
                "return) is:\n");
            sb.Append(GeneratedWorkflowMapper.ToFlatJson(currentWorkflow)).Append('\n');
            sb.Append(
                "\nApply the change below and return the COMPLETE updated workflow — keep every node, edge, " +
                "and property that the change doesn't touch, and preserve existing node ids where they stay.\n");
            sb.Append(
                "IMPORTANT: the workflow may contain node TYPES that are NOT in the catalog above (e.g. " +
                "`externalDevice` — an external device block that is the workflow's event-driven entry point / " +
                "trigger). Keep every such node EXACTLY as-is: do not remove it, do not replace it with a " +
                "`manualTrigger`/`actionTrigger`, and do not treat its absence from the catalog as an error. " +
                "The 'use only catalog types' rule applies ONLY to brand-new nodes you add. Never swap out the " +
                "existing trigger/entry node unless the change explicitly asks you to.\n\n");
            sb.Append("Change to apply:\n").Append(intent.Trim()).Append('\n');
        }
        else
        {
            sb.Append("Intent:\n").Append(intent.Trim()).Append('\n');
        }

        if (priorErrors is { Count: > 0 })
        {
            sb.Append(
                "\nYour previous workflow failed validation with the errors below. Return a corrected " +
                "workflow that fixes every one of them. Keep everything that was already valid.\n");
            foreach (var err in priorErrors)
            {
                sb.Append("- ").Append(err).Append('\n');
            }
        }

        return sb.ToString();
    }
}
