using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Features.Compiler;

public class WorkflowCompiler
{
    private readonly IWorkflowDefinitionProvider _definitionProvider;
    private readonly INodePackageManifestProvider _manifestProvider;

    /// <summary>
    /// Gets the manifest provider used during workflow compilation. Public so the Execution
    /// slice (a separate assembly) can resolve manifests via the compiler during replay/run.
    /// TODO(Tier C): inject INodePackageManifestProvider into Execution directly instead.
    /// </summary>
    public INodePackageManifestProvider ManifestProvider => _manifestProvider;

    public WorkflowCompiler(
        IWorkflowDefinitionProvider definitionProvider,
        INodePackageManifestProvider manifestProvider)
    {
        _definitionProvider = definitionProvider;
        _manifestProvider = manifestProvider;
    }

    public async Task<CompilationResult> CompileAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<CompilationDiagnostic>();
        var subflowPath = new List<WorkflowDefinitionId> { definition.Id };
        var triggerEntryNodes = new List<NodeId>();

        var (flatNodes, flatEdges, success) = await InlineSubflowsAsync(
            definition, 
            subflowPath, 
            prefix: "", 
            diagnostics, 
            cancellationToken);

        if (!success)
        {
            return new CompilationResult(null, diagnostics.ToImmutableArray());
        }

        // Validate node types and configuration properties against manifests
        foreach (var node in flatNodes)
        {
            var manifest = await _manifestProvider.GetManifestAsync(new NodePackageId(node.Type), cancellationToken);
            if (manifest == null)
            {
                diagnostics.Add(new CompilationDiagnostic(
                    DiagnosticSeverity.Error,
                    "ERR_INVALID_NODE_TYPE",
                    $"Node '{node.Id}' has an invalid or unsupported node type '{node.Type}'.",
                    node.Id));
                continue;
            }

            // Invariant 5.2 (Default-Deny Side-Effects): Default null/omitted to NonIdempotentSideEffect and default RetryPolicy
            var sideEffect = manifest.SideEffectKind ?? NodeSideEffectKind.NonIdempotentSideEffect;
            var retryPolicy = manifest.RetryPolicy ?? new RetryPolicy();
            manifest = manifest with { SideEffectKind = sideEffect, RetryPolicy = retryPolicy };

            var isTriggerEntryNode = manifest.TriggerOnly || IsBuiltInTriggerNodeType(node.Type);
            var isInlinedSubflowNode = node.Id.Value.Contains('/', StringComparison.Ordinal);

            if (isTriggerEntryNode && !isInlinedSubflowNode)
            {
                var incomingEdges = flatEdges.Any(e => e.To == node.Id);
                if (incomingEdges)
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Error,
                        "ERR_TRIGGER_WITH_INCOMING_CONNECTIONS",
                        $"Node '{node.Id}' of type '{node.Type}' is a trigger entry-point and cannot have incoming connections.",
                        node.Id));
                    continue;
                }

                triggerEntryNodes.Add(node.Id);
            }

            // Validate properties against manifest definition
            foreach (var param in manifest.Parameters)
            {
                var hasProp = node.Properties.TryGetValue(param.Name, out var rawVal);
                var isNullOrEmpty = !hasProp || rawVal == null || 
                                    (rawVal is string s && string.IsNullOrWhiteSpace(s));

                if (param.Required && isNullOrEmpty)
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Error,
                        "ERR_MISSING_REQUIRED_PARAMETER",
                        $"Node '{node.Id}' of type '{node.Type}' is missing required parameter '{param.Name}'.",
                        node.Id));
                    continue;
                }

                if (!isNullOrEmpty && rawVal != null)
                {
                    var valStr = rawVal.ToString();
                    var isExpression = valStr != null && valStr.Trim().StartsWith("{{") && valStr.Trim().EndsWith("}}");

                    if (!isExpression)
                    {
                        if (param.Type.Equals("number", StringComparison.OrdinalIgnoreCase) || param.Type.Equals("int", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!double.TryParse(valStr, out _) && !(rawVal is double || rawVal is float || rawVal is int || rawVal is long || rawVal is decimal))
                            {
                                diagnostics.Add(new CompilationDiagnostic(
                                    DiagnosticSeverity.Error,
                                    "ERR_INVALID_PARAMETER_TYPE",
                                    $"Node '{node.Id}' parameter '{param.Name}' must be a valid number.",
                                    node.Id));
                            }
                        }
                        else if (param.Type.Equals("bool", StringComparison.OrdinalIgnoreCase) || param.Type.Equals("boolean", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!bool.TryParse(valStr, out _) && !(rawVal is bool))
                            {
                                diagnostics.Add(new CompilationDiagnostic(
                                    DiagnosticSeverity.Error,
                                    "ERR_INVALID_PARAMETER_TYPE",
                                    $"Node '{node.Id}' parameter '{param.Name}' must be a valid boolean.",
                                    node.Id));
                            }
                        }
                    }
                }
            }
        }

        // Edge validation: check if From and To point to existing nodes and validate socket mappings
        var nodeIds = flatNodes.Select(n => n.Id).ToHashSet();
        foreach (var edge in flatEdges)
        {
            if (!nodeIds.Contains(edge.From))
            {
                diagnostics.Add(new CompilationDiagnostic(
                    DiagnosticSeverity.Error,
                    "ERR_INVALID_EDGE_SOURCE",
                    $"Edge '{edge.Id}' references non-existent source node '{edge.From}'.",
                    edge.From,
                    edge.Id));
            }
            if (!nodeIds.Contains(edge.To))
            {
                diagnostics.Add(new CompilationDiagnostic(
                    DiagnosticSeverity.Error,
                    "ERR_INVALID_EDGE_TARGET",
                    $"Edge '{edge.Id}' references non-existent target node '{edge.To}'.",
                    edge.To,
                    edge.Id));
            }

            // Socket mappings validation
            var fromNode = flatNodes.FirstOrDefault(n => n.Id == edge.From);
            var toNode = flatNodes.FirstOrDefault(n => n.Id == edge.To);

            var fromManifest = fromNode != null
                ? await _manifestProvider.GetManifestAsync(new NodePackageId(fromNode.Type), cancellationToken)
                : null;
            var toManifest = toNode != null
                ? await _manifestProvider.GetManifestAsync(new NodePackageId(toNode.Type), cancellationToken)
                : null;

            // The external-device block exposes DYNAMIC pins (evt:<type> outputs, act:<type> inputs)
            // generated from its config, not declared in its manifest. Those edges are validated by the
            // reactive layer (ReactiveRuleCompiler/ReactiveGraphValidator), so the control-flow socket
            // check — which only knows manifest-declared sockets — must skip device-pin endpoints.
            const string ExternalDeviceType = Knotarium.Core.Reactive.ReactiveRuleCompiler.ExternalDeviceNodeType;
            bool fromIsDevice = fromNode != null && string.Equals(fromNode.Type, ExternalDeviceType, StringComparison.OrdinalIgnoreCase);
            bool toIsDevice = toNode != null && string.Equals(toNode.Type, ExternalDeviceType, StringComparison.OrdinalIgnoreCase);

            if (fromNode != null && fromManifest != null && !fromIsDevice)
            {
                var validOutputs = fromManifest.Outputs.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (validOutputs.Count > 0 && !validOutputs.Contains(edge.Output))
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Error,
                        "ERR_INVALID_SOCKET_MAPPING",
                        $"Edge '{edge.Id}' references non-existent output socket '{edge.Output}' on node '{edge.From}' of type '{fromNode.Type}'.",
                        edge.From,
                        edge.Id));
                }
            }

            if (toNode != null && toManifest != null && !toIsDevice)
            {
                var validInputs = toManifest.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var declaredInput in toManifest.Inputs)
                {
                    validInputs.Add(declaredInput.Name);
                }
                validInputs.Add("in");
                validInputs.Add("payload");
                if (validInputs.Count > 0 && !validInputs.Contains(edge.Input))
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Error,
                        "ERR_INVALID_SOCKET_MAPPING",
                        $"Edge '{edge.Id}' references non-existent input socket '{edge.Input}' on node '{edge.To}' of type '{toNode.Type}'.",
                        edge.To,
                        edge.Id));
                }
            }

            if (fromManifest != null && toManifest != null)
            {
                var outputDef = fromManifest.Outputs
                    .FirstOrDefault(o => o.Name.Equals(edge.Output, StringComparison.OrdinalIgnoreCase));

                // Phase A: when an edge feeds a declared output into a declared, typed config
                // parameter, warn (non-blocking) on a clear scalar mismatch. Inputs mapping to the
                // generic "in"/"payload" sockets (no matching parameter) are treated as "any".
                var inputParam = toManifest.Parameters
                    .FirstOrDefault(p => p.Name.Equals(edge.Input, StringComparison.OrdinalIgnoreCase));

                if (outputDef != null && inputParam != null &&
                    TypeCompatibility.IsKnown(outputDef.Type) &&
                    TypeCompatibility.IsKnown(inputParam.Type) &&
                    !TypeCompatibility.IsAssignable(outputDef.Type, inputParam.Type))
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Warning,
                        "WARN_TYPE_MISMATCH",
                        $"Edge '{edge.Id}': output '{edge.Output}' ({outputDef.Type}) of node '{edge.From}' is not assignable to input '{edge.Input}' ({inputParam.Type}) of node '{edge.To}'.",
                        edge.To,
                        edge.Id));
                }

                // Phase B: when the target declares a typed data input with required fields and the
                // source output declares a field schema, verify the producer actually delivers them.
                var inputDef = toManifest.Inputs
                    .FirstOrDefault(i => i.Name.Equals(edge.Input, StringComparison.OrdinalIgnoreCase));

                if (outputDef?.Fields != null && inputDef?.Fields != null)
                {
                    foreach (var required in inputDef.Fields.Where(f => f.Required))
                    {
                        var provided = outputDef.Fields
                            .FirstOrDefault(f => f.Name.Equals(required.Name, StringComparison.OrdinalIgnoreCase));

                        if (provided == null)
                        {
                            diagnostics.Add(new CompilationDiagnostic(
                                DiagnosticSeverity.Warning,
                                "WARN_MISSING_FIELD",
                                $"Edge '{edge.Id}': input '{edge.Input}' of node '{edge.To}' requires field '{required.Name}' ({required.Type}), which output '{edge.Output}' of node '{edge.From}' does not provide.",
                                edge.To,
                                edge.Id));
                        }
                        else if (TypeCompatibility.IsKnown(provided.Type) &&
                                 TypeCompatibility.IsKnown(required.Type) &&
                                 !TypeCompatibility.IsAssignable(provided.Type, required.Type))
                        {
                            diagnostics.Add(new CompilationDiagnostic(
                                DiagnosticSeverity.Warning,
                                "WARN_FIELD_TYPE_MISMATCH",
                                $"Edge '{edge.Id}': field '{required.Name}' ({provided.Type}) of output '{edge.Output}' on node '{edge.From}' is not assignable to required field '{required.Name}' ({required.Type}) of input '{edge.Input}' on node '{edge.To}'.",
                                edge.To,
                                edge.Id));
                        }
                    }
                }
            }
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return new CompilationResult(null, diagnostics.ToImmutableArray());
        }

        // Build Adjacency List
        var adjacencyList = flatNodes.ToDictionary(
            n => n.Id,
            n => new List<NodeId>()
        );

        foreach (var edge in flatEdges)
        {
            if (adjacencyList.ContainsKey(edge.From))
            {
                adjacencyList[edge.From].Add(edge.To);
            }
        }

        // Cycle Detection & Entry Nodes Identification
        var visited = new Dictionary<NodeId, int>(); // 0 = unvisited, 1 = visiting, 2 = visited
        var hasCycle = false;

        bool HasCycleDfs(NodeId nodeId)
        {
            visited[nodeId] = 1; // Visiting
            if (adjacencyList.TryGetValue(nodeId, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (visited.TryGetValue(neighbor, out var state))
                    {
                        if (state == 1)
                        {
                            var neighborNode = flatNodes.FirstOrDefault(n => n.Id == neighbor);
                            var isLoopback = neighborNode != null &&
                                (neighborNode.Type.Equals("forLoop", StringComparison.OrdinalIgnoreCase) ||
                                 neighborNode.Type.Equals("parallelForEach", StringComparison.OrdinalIgnoreCase));

                            if (!isLoopback)
                            {
                                diagnostics.Add(new CompilationDiagnostic(
                                    DiagnosticSeverity.Error,
                                    "ERR_CYCLE_DETECTED",
                                    $"A cycle was detected containing node '{neighbor}'.",
                                    neighbor));
                                return true;
                            }
                        }
                        if (state == 0 && HasCycleDfs(neighbor))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        visited[neighbor] = 0;
                        if (HasCycleDfs(neighbor)) return true;
                    }
                }
            }
            visited[nodeId] = 2; // Visited
            return false;
        }

        foreach (var nodeId in flatNodes.Select(n => n.Id))
        {
            visited.TryAdd(nodeId, 0);
        }

        foreach (var nodeId in flatNodes.Select(n => n.Id))
        {
            if (visited[nodeId] == 0)
            {
                if (HasCycleDfs(nodeId))
                {
                    hasCycle = true;
                }
            }
        }

        if (hasCycle)
        {
            return new CompilationResult(null, diagnostics.ToImmutableArray());
        }

        var entryNodes = triggerEntryNodes.ToImmutableArray();

        // A device-block graph is reactive-only: it has no control-flow trigger (it's "always live while
        // enabled", dispatched by the reactive layer, never a run), so the missing-trigger rule doesn't
        // apply when the workflow is made of external-device blocks.
        var hasDeviceNodes = flatNodes.Any(n =>
            string.Equals(n.Type, Knotarium.Core.Reactive.ReactiveRuleCompiler.ExternalDeviceNodeType, StringComparison.OrdinalIgnoreCase));

        if (entryNodes.IsEmpty && !hasDeviceNodes)
        {
            diagnostics.Add(new CompilationDiagnostic(
                DiagnosticSeverity.Error,
                "ERR_MISSING_START_NODE",
                "Workflow must contain at least one trigger entry-point node."));
            return new CompilationResult(null, diagnostics.ToImmutableArray());
        }

        var plan = new ExecutionPlan(
            definition.Id,
            1, // Version defaults to 1 for compiled plans
            flatNodes.ToImmutableArray(),
            flatEdges.ToImmutableArray(),
            adjacencyList.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableArray()),
            entryNodes
        );

        return new CompilationResult(plan, diagnostics.ToImmutableArray());
    }

    private static bool IsBuiltInTriggerNodeType(string nodeType)
    {
        return nodeType.Equals("start", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("manualTrigger", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("webhookTrigger", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("scheduler", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(List<PlannedNode> Nodes, List<PlannedEdge> Edges, bool Success)> InlineSubflowsAsync(
        WorkflowDefinition currentWorkflow,
        List<WorkflowDefinitionId> subflowPath,
        string prefix,
        List<CompilationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var resultNodes = new List<PlannedNode>();
        var resultEdges = new List<PlannedEdge>();
        var success = true;

        foreach (var node in currentWorkflow.Nodes)
        {
            var absoluteNodeIdStr = string.IsNullOrEmpty(prefix) ? node.Id.Value : $"{prefix}/{node.Id.Value}";
            var absoluteNodeId = NodeId.Create(absoluteNodeIdStr);

            if (node.Type.Equals("subflow", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetSubflowId(node.Properties, out var subflowIdStr))
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Error,
                        "ERR_MISSING_SUBFLOW_ID",
                        $"Subflow node '{absoluteNodeId}' is missing 'subflowId' property.",
                        absoluteNodeId));
                    success = false;
                    continue;
                }

                var subflowWorkflowId = WorkflowDefinitionId.Parse(subflowIdStr);

                if (subflowPath.Contains(subflowWorkflowId))
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Error,
                        "ERR_RECURSIVE_SUBFLOW",
                        $"Recursive subflow reference detected to '{subflowWorkflowId}' in subflow path: {string.Join(" -> ", subflowPath.Select(id => id.Value))}",
                        absoluteNodeId));
                    success = false;
                    continue;
                }

                var subflowDefinition = await _definitionProvider.GetDefinitionAsync(subflowWorkflowId, cancellationToken);
                if (subflowDefinition == null)
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Error,
                        "ERR_SUBFLOW_NOT_FOUND",
                        $"Subflow '{subflowWorkflowId}' referenced by node '{absoluteNodeId}' was not found.",
                        absoluteNodeId));
                    success = false;
                    continue;
                }

                subflowPath.Add(subflowWorkflowId);
                var (innerNodes, innerEdges, innerSuccess) = await InlineSubflowsAsync(
                    subflowDefinition,
                    subflowPath,
                    prefix: absoluteNodeIdStr,
                    diagnostics,
                    cancellationToken);
                subflowPath.RemoveAt(subflowPath.Count - 1);

                if (!innerSuccess)
                {
                    success = false;
                    continue;
                }

                resultNodes.AddRange(innerNodes);
                resultEdges.AddRange(innerEdges);

                var subflowStartNode = innerNodes.FirstOrDefault(n => n.Type.Equals("start", StringComparison.OrdinalIgnoreCase));
                var subflowEndNode = innerNodes.FirstOrDefault(n => n.Type.Equals("end", StringComparison.OrdinalIgnoreCase));

                if (subflowStartNode == null)
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Error,
                        "ERR_SUBFLOW_MISSING_START",
                        $"Subflow '{subflowWorkflowId}' (referenced in '{absoluteNodeId}') does not contain a 'start' node.",
                        absoluteNodeId));
                    success = false;
                }
                if (subflowEndNode == null)
                {
                    diagnostics.Add(new CompilationDiagnostic(
                        DiagnosticSeverity.Error,
                        "ERR_SUBFLOW_MISSING_END",
                        $"Subflow '{subflowWorkflowId}' (referenced in '{absoluteNodeId}') does not contain an 'end' node.",
                        absoluteNodeId));
                    success = false;
                }

                // Typed variable passing with instance isolation. The subflow's internal variables are
                // namespaced to this instance (childScope) so reusing the same subflow doesn't collide.
                // The input/output maps are the only bridge across the boundary:
                //  - inputs:  target = a subflow-local (childScope), value = a caller expression (parentScope)
                //  - outputs: source = a subflow-local (childScope), target = a caller global (parentScope)
                var childScope = ScopeFromPrefix(absoluteNodeIdStr);
                var parentScope = ScopeFromPrefix(prefix);
                if (subflowStartNode != null && node.Properties.TryGetValue("subflowInputs", out var subflowInputs) && subflowInputs != null)
                {
                    var scoped = ScopeSubflowMap(subflowInputs, "target", childScope, "value", otherIsExpression: true, prefix, parentScope);
                    ReplacePlannedNodeProperty(resultNodes, subflowStartNode.Id, "__subflowInputs", scoped);
                }
                if (subflowEndNode != null && node.Properties.TryGetValue("subflowOutputs", out var subflowOutputs) && subflowOutputs != null)
                {
                    var scoped = ScopeSubflowMap(subflowOutputs, "source", childScope, "target", otherIsExpression: false, prefix, parentScope);
                    ReplacePlannedNodeProperty(resultNodes, subflowEndNode.Id, "__subflowOutputs", scoped);
                }
            }
            else
            {
                var rewrittenProperties = RewriteNodeProperties(node.Properties, node.Type, prefix, ScopeFromPrefix(prefix));
                resultNodes.Add(new PlannedNode(absoluteNodeId, node.Type, rewrittenProperties));
            }
        }

        foreach (var edge in currentWorkflow.Edges)
        {
            var edgeFromAbsStr = string.IsNullOrEmpty(prefix) ? edge.From.Value : $"{prefix}/{edge.From.Value}";
            var edgeToAbsStr = string.IsNullOrEmpty(prefix) ? edge.To.Value : $"{prefix}/{edge.To.Value}";

            var edgeFromNode = currentWorkflow.Nodes.FirstOrDefault(n => n.Id == edge.From);
            var edgeToNode = currentWorkflow.Nodes.FirstOrDefault(n => n.Id == edge.To);

            if (edgeToNode != null && edgeToNode.Type.Equals("subflow", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetSubflowId(edgeToNode.Properties, out _))
                {
                    var subflowStartPrefix = $"{edgeToAbsStr}/";
                    var resolvedStartNode = resultNodes.FirstOrDefault(n => n.Id.Value.StartsWith(subflowStartPrefix) && n.Type.Equals("start", StringComparison.OrdinalIgnoreCase));
                    if (resolvedStartNode != null)
                    {
                        edgeToAbsStr = resolvedStartNode.Id.Value;
                    }
                }
            }

            if (edgeFromNode != null && edgeFromNode.Type.Equals("subflow", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetSubflowId(edgeFromNode.Properties, out _))
                {
                    var subflowEndPrefix = $"{edgeFromAbsStr}/";
                    var resolvedEndNode = resultNodes.FirstOrDefault(n => n.Id.Value.StartsWith(subflowEndPrefix) && n.Type.Equals("end", StringComparison.OrdinalIgnoreCase));
                    if (resolvedEndNode != null)
                    {
                        edgeFromAbsStr = resolvedEndNode.Id.Value;
                    }
                }
            }

            resultEdges.Add(new PlannedEdge(
                string.IsNullOrEmpty(prefix) ? edge.Id : $"{prefix}/{edge.Id}",
                NodeId.Create(edgeFromAbsStr),
                edge.Output,
                NodeId.Create(edgeToAbsStr),
                edge.Input
            ));
        }

        return (resultNodes, resultEdges, success);
    }

    // Node properties arrive from two paths: in tests/persistence they're native CLR strings, but
    // over the HTTP publish/validate endpoints the `object` values deserialize as JsonElement
    // (no inferred-type converter is registered). Accept both so a 'subflowId' set in the editor
    // isn't misreported as missing.
    private static bool TryGetSubflowId(IReadOnlyDictionary<string, object> properties, out string subflowId)
    {
        subflowId = string.Empty;
        if (properties == null || !properties.TryGetValue("subflowId", out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case string s when !string.IsNullOrWhiteSpace(s):
                subflowId = s;
                return true;
            case JsonElement je when je.ValueKind == JsonValueKind.String:
                var value = je.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }
                subflowId = value;
                return true;
            default:
                return false;
        }
    }

    // Replace an already-inlined node with a copy that has one extra property merged in. Used to
    // attach subflow input/output maps to the inlined start/end nodes.
    private static void ReplacePlannedNodeProperty(List<PlannedNode> nodes, NodeId nodeId, string key, object value)
    {
        var index = nodes.FindIndex(n => n.Id == nodeId);
        if (index < 0)
        {
            return;
        }

        var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in nodes[index].Properties)
        {
            merged[kvp.Key] = kvp.Value;
        }
        merged[key] = value;
        nodes[index] = new PlannedNode(nodes[index].Id, nodes[index].Type, merged);
    }

    // Subflow variable namespacing is centralized in SubflowScope so the compile-time rewriting here
    // and the runtime Inline Code scoping stay in lock-step.
    private static string ScopeFromPrefix(string prefix) => SubflowScope.FromPrefix(prefix);

    private static string PrefixVarName(string name, string scope) => SubflowScope.Apply(scope, name);

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    // Rewrites a leaf node's properties when it's inlined inside a subflow: node references ($node.X)
    // are prefixed with the inline path, and variable references are namespaced to the instance so
    // reusing the same subflow doesn't share variable storage.
    private IReadOnlyDictionary<string, object> RewriteNodeProperties(
        IReadOnlyDictionary<string, object> properties,
        string nodeType,
        string nodePrefix,
        string varScope)
    {
        if (string.IsNullOrEmpty(nodePrefix) && string.IsNullOrEmpty(varScope))
        {
            return properties;
        }

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in properties)
        {
            // The write side of Set Variable(s) names a variable directly (not as an expression).
            if (!string.IsNullOrEmpty(varScope)
                && nodeType.Equals("setVariable", StringComparison.OrdinalIgnoreCase)
                && kvp.Key.Equals("variableName", StringComparison.OrdinalIgnoreCase))
            {
                result[kvp.Key] = PrefixVarName(AsStringValue(kvp.Value) ?? string.Empty, varScope);
            }
            else if (!string.IsNullOrEmpty(varScope)
                && nodeType.Equals("setVariables", StringComparison.OrdinalIgnoreCase)
                && kvp.Key.Equals("variables", StringComparison.OrdinalIgnoreCase))
            {
                result[kvp.Key] = RewriteSetVariablesArray(kvp.Value, nodePrefix, varScope);
            }
            else
            {
                result[kvp.Key] = RewriteValue(kvp.Value, nodePrefix, varScope)!;
            }
        }
        return result;
    }

    private object RewriteSetVariablesArray(object? raw, string nodePrefix, string varScope)
    {
        var list = new List<object>();
        foreach (var entry in EnumerateEntries(raw))
        {
            var newEntry = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in EnumerateFields(entry))
            {
                newEntry[key] = key.Equals("name", StringComparison.OrdinalIgnoreCase)
                    ? PrefixVarName(AsStringValue(value) ?? string.Empty, varScope)
                    : RewriteValue(value, nodePrefix, varScope)!;
            }
            list.Add(newEntry);
        }
        return list;
    }

    // Scopes a subflow input/output map: the subflow-local side (localKey) is namespaced to the
    // instance (childScope); the caller side (otherKey) is either a caller expression (inputs) or a
    // caller variable name (outputs), namespaced to the parent scope.
    private object ScopeSubflowMap(
        object? raw,
        string localKey,
        string childScope,
        string otherKey,
        bool otherIsExpression,
        string parentPrefix,
        string parentScope)
    {
        var list = new List<object>();
        foreach (var entry in EnumerateEntries(raw))
        {
            var newEntry = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in EnumerateFields(entry))
            {
                if (key.Equals(localKey, StringComparison.OrdinalIgnoreCase))
                {
                    newEntry[key] = PrefixVarName(AsStringValue(value) ?? string.Empty, childScope);
                }
                else if (key.Equals(otherKey, StringComparison.OrdinalIgnoreCase))
                {
                    newEntry[key] = otherIsExpression
                        ? RewriteValue(value, parentPrefix, parentScope)!
                        : PrefixVarName(AsStringValue(value) ?? string.Empty, parentScope);
                }
                else if (value != null)
                {
                    newEntry[key] = value;
                }
            }
            list.Add(newEntry);
        }
        return list;
    }

    private object? RewriteValue(object? value, string nodePrefix, string varScope)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return RewriteExpressionString(s, nodePrefix, varScope);
            case JsonElement je:
                return RewriteJsonElement(je, nodePrefix, varScope);
            case IReadOnlyDictionary<string, object> roDict:
                return RewriteDict(roDict, nodePrefix, varScope);
            case IDictionary<string, object> dict:
                return RewriteDict(dict, nodePrefix, varScope);
            case System.Collections.IEnumerable enumerable:
                var list = new List<object?>();
                foreach (var item in enumerable)
                {
                    list.Add(RewriteValue(item, nodePrefix, varScope));
                }
                return list;
            default:
                return value;
        }
    }

    private object RewriteDict(IEnumerable<KeyValuePair<string, object>> dict, string nodePrefix, string varScope)
    {
        var snapshot = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        string? typeVal = null;
        foreach (var kvp in dict)
        {
            if (kvp.Key.Equals("__type", StringComparison.OrdinalIgnoreCase))
            {
                typeVal = kvp.Value?.ToString();
            }
            if (kvp.Value != null)
            {
                snapshot[kvp.Key] = kvp.Value;
            }
        }

        var isVariableRef = !string.IsNullOrEmpty(varScope)
            && string.Equals(typeVal, "variable_ref", StringComparison.OrdinalIgnoreCase);

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in snapshot)
        {
            if (isVariableRef && kvp.Key.Equals("variableName", StringComparison.OrdinalIgnoreCase))
            {
                result[kvp.Key] = PrefixVarName(AsStringValue(kvp.Value) ?? string.Empty, varScope);
            }
            else
            {
                result[kvp.Key] = RewriteValue(kvp.Value, nodePrefix, varScope)!;
            }
        }
        return result;
    }

    private object? RewriteJsonElement(JsonElement je, string nodePrefix, string varScope)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.String:
                return RewriteExpressionString(je.GetString() ?? string.Empty, nodePrefix, varScope);
            case JsonValueKind.Number:
                return je.TryGetInt64(out var l) ? l : je.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in je.EnumerateArray())
                {
                    list.Add(RewriteJsonElement(item, nodePrefix, varScope));
                }
                return list;
            case JsonValueKind.Object:
                string? typeVal = je.TryGetProperty("__type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;
                var isVariableRef = !string.IsNullOrEmpty(varScope)
                    && string.Equals(typeVal, "variable_ref", StringComparison.OrdinalIgnoreCase);
                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in je.EnumerateObject())
                {
                    if (isVariableRef && prop.Name.Equals("variableName", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        dict[prop.Name] = PrefixVarName(prop.Value.GetString() ?? string.Empty, varScope);
                    }
                    else
                    {
                        dict[prop.Name] = RewriteJsonElement(prop.Value, nodePrefix, varScope)!;
                    }
                }
                return dict;
            default:
                return je.ToString();
        }
    }

    // Rewrites $node.<id> references (inline path) and $variables.<name> references (instance scope)
    // inside a string/template value.
    private string RewriteExpressionString(string val, string nodePrefix, string varScope)
    {
        var result = val;
        if (!string.IsNullOrEmpty(nodePrefix))
        {
            result = RewriteToken(result, "$node.", id => $"{nodePrefix}/{id}", stopAtDot: true);
        }
        if (!string.IsNullOrEmpty(varScope))
        {
            result = RewriteToken(result, "$variables.", name => PrefixVarName(name, varScope), stopAtDot: false);
        }
        return result;
    }

    private static string RewriteToken(string val, string token, Func<string, string> transform, bool stopAtDot)
    {
        var index = 0;
        var result = val;
        while ((index = result.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            var start = index + token.Length;
            int end;
            if (stopAtDot)
            {
                end = result.IndexOf('.', start);
                if (end == -1)
                {
                    index = start;
                    continue;
                }
            }
            else
            {
                end = start;
                while (end < result.Length && IsIdentifierChar(result[end]))
                {
                    end++;
                }
                if (end == start)
                {
                    index = start;
                    continue;
                }
            }

            var name = result.Substring(start, end - start);
            var replacement = transform(name);
            result = result.Substring(0, start) + replacement + result.Substring(end);
            index = start + replacement.Length;
        }
        return result;
    }

    private static IEnumerable<object?> EnumerateEntries(object? raw)
    {
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in je.EnumerateArray())
            {
                yield return entry;
            }
        }
        else if (raw is System.Collections.IEnumerable enumerable && raw is not string)
        {
            foreach (var entry in enumerable)
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<(string key, object? value)> EnumerateFields(object? entry)
    {
        if (entry is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in je.EnumerateObject())
            {
                yield return (prop.Name, prop.Value);
            }
        }
        else if (entry is IReadOnlyDictionary<string, object> roDict)
        {
            foreach (var kvp in roDict)
            {
                yield return (kvp.Key, kvp.Value);
            }
        }
        else if (entry is IDictionary<string, object> dict)
        {
            foreach (var kvp in dict)
            {
                yield return (kvp.Key, kvp.Value);
            }
        }
    }

    private static string? AsStringValue(object? value)
    {
        if (value is string s)
        {
            return s;
        }
        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        }
        return value?.ToString();
    }
}
