using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;
using KnotGarden.Infrastructure.Persistence;
using KnotGarden.Features.Portability;
using Microsoft.EntityFrameworkCore;

namespace KnotGarden.Features.Execution;

/// <summary>
/// Creates immutable workflow versions and publishes workflow definitions into the runtime database.
/// </summary>
public sealed class WorkflowPublisher
{
    private readonly AppDbContext _dbContext;
    private readonly WorkflowCompiler _compiler;
    private readonly IReadOnlyList<IWorkflowTriggerSynchronizer> _triggerSynchronizers;
    private readonly ActiveWorkflowVersionService _activeWorkflowVersionService;
    private readonly WorkflowActivationService _activationService;
    private readonly IWorkflowStore _workflowStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowPublisher"/> class.
    /// </summary>
    /// <param name="dbContext">The application database context.</param>
    /// <param name="compiler">The workflow compiler.</param>
    /// <param name="triggerSynchronizers">The registered trigger synchronizers (schedules, polling triggers).</param>
    /// <param name="activeWorkflowVersionService">The active workflow version service.</param>
    /// <param name="activationService">The version-scoped activation service.</param>
    /// <param name="workflowStore">The workflow store containing draft definitions.</param>
    public WorkflowPublisher(
        AppDbContext dbContext,
        WorkflowCompiler compiler,
        IEnumerable<IWorkflowTriggerSynchronizer> triggerSynchronizers,
        ActiveWorkflowVersionService activeWorkflowVersionService,
        WorkflowActivationService activationService,
        IWorkflowStore workflowStore)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(triggerSynchronizers);
        ArgumentNullException.ThrowIfNull(activeWorkflowVersionService);
        ArgumentNullException.ThrowIfNull(activationService);
        ArgumentNullException.ThrowIfNull(workflowStore);

        _dbContext = dbContext;
        _compiler = compiler;
        _triggerSynchronizers = triggerSynchronizers.ToArray();
        _activeWorkflowVersionService = activeWorkflowVersionService;
        _activationService = activationService;
        _workflowStore = workflowStore;
    }

    /// <summary>
    /// Creates a new immutable version from supplied workflow nodes and edges.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="nodes">The node definitions to snapshot.</param>
    /// <param name="edges">The edge definitions to snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created workflow version when the workflow exists; otherwise, <see langword="null"/>.</returns>
    public async Task<WorkflowVersion?> CreateVersionAsync(
        WorkflowDefinitionId workflowId,
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<EdgeDefinition> edges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var draft = await _workflowStore.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return null;
        }

        // Ensure database workflow header exists
        var dbWorkflow = await _dbContext.WorkflowDefinitions
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken)
            .ConfigureAwait(false);

        if (dbWorkflow is null)
        {
            _dbContext.WorkflowDefinitions.Add(draft with { Nodes = nodes, Edges = edges });
        }

        var version = await CreateVersionCoreAsync(workflowId, nodes, edges, cancellationToken).ConfigureAwait(false);
        await SaveChangesWithVersionNumberRetryAsync(cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <summary>
    /// Publishes workflow content by validating it, updating the workflow definition, synchronizing schedules, and creating a new version.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="nodes">The node definitions to publish.</param>
    /// <param name="edges">The edge definitions to publish.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The publish result when the workflow exists; otherwise, <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when scheduler synchronization fails validation.</exception>
    public async Task<WorkflowPublishResult?> PublishAsync(
        WorkflowDefinitionId workflowId,
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<EdgeDefinition> edges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var draft = await _workflowStore.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return null;
        }

        var dbWorkflow = await _dbContext.WorkflowDefinitions
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken)
            .ConfigureAwait(false);

        var updatedWorkflow = draft with { Nodes = nodes, Edges = edges };
        var compilation = await _compiler.CompileAsync(updatedWorkflow, cancellationToken).ConfigureAwait(false);
        if (!compilation.IsSuccess)
        {
            return new WorkflowPublishResult(updatedWorkflow, Version: null, compilation.Diagnostics);
        }

        if (dbWorkflow is null)
        {
            _dbContext.WorkflowDefinitions.Add(updatedWorkflow);
        }
        else
        {
            _dbContext.Entry(dbWorkflow).CurrentValues.SetValues(updatedWorkflow);
        }

        foreach (var synchronizer in _triggerSynchronizers)
        {
            await synchronizer.SyncAsync(updatedWorkflow, cancellationToken).ConfigureAwait(false);
        }

        // Dedup: if the latest version already holds identical content, reuse it
        // instead of spawning a near-duplicate version on every save.
        var latestVersion = await _dbContext.WorkflowVersions
            .Where(item => item.WorkflowDefinitionId == workflowId)
            .OrderByDescending(item => item.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var version = latestVersion is not null && HasSameContent(latestVersion, nodes, edges)
            ? latestVersion
            : await CreateVersionCoreAsync(workflowId, nodes, edges, cancellationToken).ConfigureAwait(false);

        await SaveChangesWithVersionNumberRetryAsync(cancellationToken).ConfigureAwait(false);

        // Auto-activate the published version (new or reused)
        await _activeWorkflowVersionService.ActivateAsync(
            workflowId,
            version.Id,
            activationReason: "Auto-activated on publish",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new WorkflowPublishResult(updatedWorkflow, version, compilation.Diagnostics);
    }

    /// <summary>
    /// Determines whether a stored version already holds the same node/edge content,
    /// using the persistence JSON shape so the comparison matches what gets saved.
    /// </summary>
    private static bool HasSameContent(
        WorkflowVersion version,
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<EdgeDefinition> edges)
    {
        return SerializeContent(version.Nodes, version.Edges) == SerializeContent(nodes, edges);
    }

    private static string SerializeContent(
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<EdgeDefinition> edges)
    {
        var nodesJson = JsonSerializer.Serialize(nodes, PersistenceJsonOptions.Default);
        var edgesJson = JsonSerializer.Serialize(edges, PersistenceJsonOptions.Default);
        return string.Concat(nodesJson, "", edgesJson);
    }

    private async Task<WorkflowVersion> CreateVersionCoreAsync(
        WorkflowDefinitionId workflowId,
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<EdgeDefinition> edges,
        CancellationToken cancellationToken,
        WorkflowVersionOrigin origin = WorkflowVersionOrigin.Published,
        WorkflowVersionId? sourceVersionId = null,
        string? createdBy = null,
        string? label = null,
        string? creationReason = null)
    {
        var lastVersion = await _dbContext.WorkflowVersions
            .Where(version => version.WorkflowDefinitionId == workflowId)
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var nextVersionNumber = (lastVersion?.VersionNumber ?? 0) + 1;
        var version = new WorkflowVersion(
            WorkflowVersionId.New(),
            workflowId,
            nextVersionNumber,
            nodes,
            edges,
            DateTimeOffset.UtcNow,
            origin,
            sourceVersionId,
            createdBy,
            label,
            creationReason);

        _dbContext.WorkflowVersions.Add(version);
        return version;
    }

    /// <summary>
    /// Restores an earlier version by copying its payload forward into a new immutable
    /// <c>Restored</c> version (never reactivating in place), optionally activating it. The whole
    /// operation is transactional: when <paramref name="activate"/> is <see langword="true"/> a failed
    /// activation rolls back the forward copy too, so a restore never half-applies.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="sourceVersionId">The version to copy forward.</param>
    /// <param name="activate">Whether to activate the restored version (defaults to <see langword="false"/>).</param>
    /// <param name="createdBy">The actor performing the restore.</param>
    /// <param name="label">An optional label for the restored version.</param>
    /// <param name="reason">An optional human-readable reason.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The restore result, or <see langword="null"/> when the source version does not belong to the workflow.
    /// </returns>
    public async Task<WorkflowRestoreResult?> RestoreAsync(
        WorkflowDefinitionId workflowId,
        WorkflowVersionId sourceVersionId,
        bool activate,
        string? createdBy = null,
        string? label = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate the source version belongs to the workflow (route + tenant boundary).
        var source = await _dbContext.WorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                version => version.Id == sourceVersionId && version.WorkflowDefinitionId == workflowId,
                cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return null;
        }

        // 2. Compatibility validation — compile the restored graph against the current environment.
        var header = await _dbContext.WorkflowDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken)
            .ConfigureAwait(false);

        var definition = new WorkflowDefinition(
            workflowId,
            header?.Name ?? workflowId.Value,
            source.Nodes,
            source.Edges);
        var compilation = await _compiler.CompileAsync(definition, cancellationToken).ConfigureAwait(false);

        // Activation requires a clean compile; an inactive forward copy may carry warnings the user
        // fixes before activating (the whole point of activate=false).
        if (activate && !compilation.IsSuccess)
        {
            return new WorkflowRestoreResult(Version: null, Activated: false, ActivatedAtUtc: null, compilation.Diagnostics);
        }

        var restoreLabel = label ?? $"Restored from v{source.VersionNumber}";

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // 3-4. Fork-forward: new immutable version with Restored provenance.
        var restored = await CreateVersionCoreAsync(
            workflowId,
            source.Nodes,
            source.Edges,
            cancellationToken,
            WorkflowVersionOrigin.Restored,
            sourceVersionId,
            createdBy,
            restoreLabel,
            reason).ConfigureAwait(false);

        await SaveChangesWithVersionNumberRetryAsync(cancellationToken).ConfigureAwait(false);

        ActiveWorkflowVersion? activated = null;
        if (activate)
        {
            // Enlists in this transaction (it owns none of its own), so trigger re-binding +
            // activation + the forward copy all commit or roll back together.
            activated = await _activationService.ActivateAsync(
                workflowId,
                restored.Id,
                activatedBy: createdBy,
                activationReason: restoreLabel,
                restoredFromVersionId: sourceVersionId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new WorkflowRestoreResult(restored, activated is not null, activated?.ActivatedAtUtc, compilation.Diagnostics);
    }

    /// <summary>
    /// Persists pending changes, recomputing the version number for any newly added
    /// <see cref="WorkflowVersion"/> when the unique <c>(WorkflowDefinitionId, VersionNumber)</c>
    /// index rejects a number a concurrent publish already claimed.
    /// </summary>
    /// <summary>
    /// Imports an exported document as a new immutable <c>Imported</c> version (inactive), creating the
    /// parent workflow when it does not yet exist. Never mutates in place; runs the same compatibility
    /// validation as restore, surfaced as warnings — the imported version stays inactive until activated.
    /// </summary>
    /// <param name="document">The parsed export document.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created <c>Imported</c> version and its compatibility diagnostics.</returns>
    public async Task<WorkflowImportResult> ImportAsync(
        WorkflowExportDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var workflowId = new WorkflowDefinitionId(document.Manifest.WorkflowId);
        var nodes = document.Content.Nodes;
        var edges = document.Content.Edges;

        var draft = await _workflowStore.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var name = draft?.Name
            ?? (string.IsNullOrWhiteSpace(document.Manifest.WorkflowName) ? workflowId.Value : document.Manifest.WorkflowName);

        // Ensure a parent workflow exists (draft + db header) to attach the imported version to.
        if (draft is null)
        {
            await _workflowStore.UpsertAsync(new WorkflowDefinition(workflowId, name, nodes, edges), cancellationToken).ConfigureAwait(false);
        }

        var header = await _dbContext.WorkflowDefinitions
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken)
            .ConfigureAwait(false);
        if (header is null)
        {
            _dbContext.WorkflowDefinitions.Add(new WorkflowDefinition(workflowId, name, nodes, edges));
        }
        else if (header.IsArchived)
        {
            // Re-importing onto a previously deleted (archived) header must bring the workflow back —
            // otherwise the imported version lands on a header the dashboard hides, and the import looks
            // like it vanished. Un-archive so the restored draft is visible again.
            _dbContext.Entry(header).Property(item => item.IsArchived).CurrentValue = false;
        }

        // Compatibility validation (same as restore); the imported version stays inactive regardless.
        var definition = new WorkflowDefinition(workflowId, name, nodes, edges);
        var compilation = await _compiler.CompileAsync(definition, cancellationToken).ConfigureAwait(false);

        var version = await CreateVersionCoreAsync(
            workflowId,
            nodes,
            edges,
            cancellationToken,
            WorkflowVersionOrigin.Imported,
            sourceVersionId: null,
            createdBy: null,
            label: document.Manifest.Label,
            creationReason: "Imported").ConfigureAwait(false);

        await SaveChangesWithVersionNumberRetryAsync(cancellationToken).ConfigureAwait(false);

        return new WorkflowImportResult(version, compilation.Diagnostics);
    }

    private async Task SaveChangesWithVersionNumberRetryAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException exception) when (attempt < maxAttempts && IsDuplicateVersionNumber(exception))
            {
                foreach (var entry in _dbContext.ChangeTracker.Entries<WorkflowVersion>()
                    .Where(item => item.State == EntityState.Added)
                    .ToList())
                {
                    var pendingWorkflowId = entry.Entity.WorkflowDefinitionId;
                    var maxNumber = await _dbContext.WorkflowVersions
                        .Where(item => item.WorkflowDefinitionId == pendingWorkflowId)
                        .Select(item => (int?)item.VersionNumber)
                        .MaxAsync(cancellationToken)
                        .ConfigureAwait(false) ?? 0;

                    entry.Property(nameof(WorkflowVersion.VersionNumber)).CurrentValue = maxNumber + 1;
                }
            }
        }
    }

    private static bool IsDuplicateVersionNumber(DbUpdateException exception)
    {
        return exception.InnerException?.Message
            .Contains("WorkflowVersions.VersionNumber", StringComparison.OrdinalIgnoreCase) == true;
    }
}

/// <summary>
/// Represents the outcome of publishing workflow content.
/// </summary>
/// <param name="Workflow">The workflow definition after applying the published content.</param>
/// <param name="Version">The created workflow version when publishing succeeds.</param>
/// <param name="Diagnostics">The compilation diagnostics produced during publishing.</param>
public sealed record WorkflowPublishResult(
    WorkflowDefinition Workflow,
    WorkflowVersion? Version,
    IReadOnlyList<CompilationDiagnostic> Diagnostics);

/// <summary>
/// Represents the outcome of restoring (fork-forward copying) an earlier workflow version.
/// </summary>
/// <param name="Version">The new <c>Restored</c> version, or <see langword="null"/> when activation was requested but compatibility validation failed.</param>
/// <param name="Activated">Whether the restored version was activated.</param>
/// <param name="ActivatedAtUtc">The activation timestamp when activated.</param>
/// <param name="Diagnostics">Compatibility diagnostics; surfaced as warnings when not activating.</param>
public sealed record WorkflowRestoreResult(
    WorkflowVersion? Version,
    bool Activated,
    DateTimeOffset? ActivatedAtUtc,
    IReadOnlyList<CompilationDiagnostic> Diagnostics);

/// <summary>
/// Represents the outcome of importing an exported document as a new <c>Imported</c> version.
/// </summary>
/// <param name="Version">The created (inactive) imported version.</param>
/// <param name="Diagnostics">Compatibility diagnostics surfaced as warnings.</param>
public sealed record WorkflowImportResult(
    WorkflowVersion Version,
    IReadOnlyList<CompilationDiagnostic> Diagnostics);