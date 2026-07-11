using System.Collections.Generic;
using KnotGarden.Core.Domain;

namespace KnotGarden.Core.Contracts;

/// <summary>
/// How faithfully one source construct mapped during an import — surfaced to the user before install so
/// coverage is reviewable. Mirrors the provider's own grading; the host never interprets vendor specifics.
/// </summary>
public enum WorkflowImportOutcome
{
    /// <summary>Mapped cleanly.</summary>
    Mapped = 0,

    /// <summary>Mapped approximately; review recommended.</summary>
    Partial = 1,

    /// <summary>No analogue; emitted as a stub / surfaced here, never silently dropped.</summary>
    Flagged = 2,
}

/// <summary>One line of an import coverage report: where it came from, what it was, and how it landed.</summary>
public sealed record WorkflowImportReportEntry(
    string Scope,
    string Construct,
    WorkflowImportOutcome Outcome,
    string? Reason = null);

/// <summary>The coverage report returned alongside the generated workflows.</summary>
public sealed record WorkflowImportReport(IReadOnlyList<WorkflowImportReportEntry> Entries);

/// <summary>An external system the setting references that the import can map to a host connection target.</summary>
public sealed record WorkflowImportServer(string Alias, string? Host, string? User, bool Enabled);

/// <summary>What the import did (or would do) about one referenced server: Create / Reuse / Bind / Skip.</summary>
public sealed record WorkflowImportProvisionedTarget(string ServerAlias, string Action, string? TargetId);

/// <summary>
/// The import options the host passes the provider: how to split workflows, how to provision the referenced
/// servers, and (for a mapping strategy) the server→target bindings. <see cref="Provision"/> is
/// <see langword="false"/> for preview (no side effects) and <see langword="true"/> on install.
/// </summary>
public sealed record WorkflowImportRequest(
    string Granularity,
    string TargetStrategy,
    IReadOnlyDictionary<string, string>? ServerMappings = null,
    bool Provision = false);

/// <summary>
/// What a provider hands back across the seam: the generic <see cref="WorkflowDefinition"/> documents the
/// host installs through its normal versioning path, the coverage report, the servers the setting references,
/// and what the chosen strategy did (or would do) about each. No vendor types cross here.
/// </summary>
public sealed record WorkflowImportProviderResult(
    IReadOnlyList<WorkflowDefinition> Workflows,
    WorkflowImportReport Report)
{
    public IReadOnlyList<WorkflowImportServer> DiscoveredServers { get; init; } = Array.Empty<WorkflowImportServer>();
    public IReadOnlyList<WorkflowImportProvisionedTarget> ProvisionedTargets { get; init; } = Array.Empty<WorkflowImportProvisionedTarget>();
}

/// <summary>
/// Describes an import provider to the host UI: a stable id, a display name, the file extensions it accepts,
/// and whether it honors the single-vs-multiple granularity choice. Carries no vendor-internal detail.
/// </summary>
public sealed record WorkflowImportProviderDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<string> FileExtensions,
    bool SupportsGranularity,
    string? Description = null)
{
    /// <summary>Whether this provider references external systems the user can create/map on import.</summary>
    public bool SupportsTargetStrategy { get; init; }

    /// <summary>The granularity to preselect in the UI: <c>"single"</c> or <c>"multiple"</c> (default).</summary>
    public string DefaultGranularity { get; init; } = "multiple";
}

/// <summary>
/// A binary plugin capability that turns an uploaded vendor file into generic KnotGarden workflows. The host
/// only ever passes raw bytes + a source hint + the granularity choice in, and gets generic
/// <see cref="WorkflowDefinition"/> documents + a report out — the vendor format stays entirely behind the
/// plugin boundary (the firewall in the import handoff design).
/// </summary>
public interface IWorkflowImportProvider
{
    /// <summary>How this provider presents itself to the host UI.</summary>
    WorkflowImportProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Translate an uploaded payload into workflows + a coverage report, and discover/provision the servers
    /// it references per <paramref name="request"/>. Called with <c>Provision=false</c> for preview (must be
    /// side-effect free) and <c>Provision=true</c> on install.
    /// </summary>
    WorkflowImportProviderResult Import(byte[] payload, string? sourceHint, WorkflowImportRequest request);
}
