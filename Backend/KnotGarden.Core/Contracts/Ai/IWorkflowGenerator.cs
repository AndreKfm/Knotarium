using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;

namespace KnotGarden.Core.Contracts.Ai;

/// <summary>
/// One model call that turns an intent (plus the node catalog and, on a repair pass, the prior
/// attempt's errors) into a candidate <see cref="WorkflowDefinition"/>. The catalog is supplied by the
/// caller rather than fetched here so the generator stays agnostic about catalog sourcing (built-ins vs.
/// DB vs. a future retrieval tier) and trivially testable.
/// </summary>
public sealed record WorkflowGenerationRequest(
    string Intent,
    IReadOnlyList<NodePackageManifest> Catalog,
    IReadOnlyList<string>? PriorErrors = null,
    // When set, the model MODIFIES this existing workflow per the intent instead of building from scratch.
    WorkflowDefinition? CurrentWorkflow = null);

/// <summary>
/// The outcome of a single generation call. <see cref="Workflow"/> is null when the model's output could
/// not be parsed into a workflow; <see cref="ParseError"/> then describes why. A parse failure is
/// <em>not</em> an exception — it is a repairable signal the orchestrator threads back into the next
/// attempt, exactly like a compiler error. <see cref="RawText"/> is the model's verbatim text (for
/// diagnostics / the repair prompt).
/// </summary>
public sealed record WorkflowGenerationAttempt(
    WorkflowDefinition? Workflow,
    string RawText,
    string? ParseError)
{
    public bool Parsed => Workflow is not null && ParseError is null;
}

public interface IWorkflowGenerator
{
    Task<WorkflowGenerationAttempt> GenerateAsync(WorkflowGenerationRequest request, CancellationToken cancellationToken = default);
}
