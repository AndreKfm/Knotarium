// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// Loads and compiles the execution plan for a run: the bound workflow version when one is set,
/// otherwise the live definition. A missing version/definition or a failed compilation marks the
/// run failed (with a journal entry) and yields <see langword="null"/>.
/// </summary>
internal sealed class ExecutionPlanLoader
{
    private readonly AppDbContext _dbContext;
    private readonly WorkflowCompiler _compiler;
    private readonly ExecutionJournalPublisher _journal;
    private readonly TimeProvider _timeProvider;

    public ExecutionPlanLoader(
        AppDbContext dbContext,
        WorkflowCompiler compiler,
        ExecutionJournalPublisher journal,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _compiler = compiler;
        _journal = journal;
        _timeProvider = timeProvider;
    }

    public async Task<ExecutionPlan?> LoadAsync(ExecutionInstance instance, CancellationToken cancellationToken)
    {
        var workflow = await _dbContext.WorkflowDefinitions
            .FirstOrDefaultAsync(definition => definition.Id == instance.WorkflowDefinitionId, cancellationToken);

        WorkflowDefinition? definition;
        if (instance.WorkflowVersionId.HasValue)
        {
            var workflowVersion = await _dbContext.WorkflowVersions
                .FirstOrDefaultAsync(version => version.Id == instance.WorkflowVersionId.Value, cancellationToken);

            if (workflowVersion == null)
            {
                instance.Status = ExecutionStatus.Failed;
                instance.UpdatedAt = _timeProvider.GetUtcNow();
                await _journal.PublishAsync(
                    instance,
                    JournalEventTypes.WorkflowFailed,
                    $"Workflow version '{instance.WorkflowVersionId.Value.Value}' not found.",
                    cancellationToken: cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return null;
            }

            definition = new WorkflowDefinition(
                instance.WorkflowDefinitionId,
                workflow?.Name ?? instance.WorkflowDefinitionId.Value,
                workflowVersion.Nodes,
                workflowVersion.Edges);
        }
        else
        {
            definition = workflow;
        }

        if (definition == null)
        {
            instance.Status = ExecutionStatus.Failed;
            instance.UpdatedAt = _timeProvider.GetUtcNow();
            await _journal.PublishAsync(
                instance,
                JournalEventTypes.WorkflowFailed,
                $"Workflow definition '{instance.WorkflowDefinitionId}' not found.",
                cancellationToken: cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }

        var compilationResult = await _compiler.CompileAsync(definition, cancellationToken);
        if (!compilationResult.IsSuccess || compilationResult.Plan == null)
        {
            instance.Status = ExecutionStatus.Failed;
            instance.UpdatedAt = _timeProvider.GetUtcNow();
            await _journal.PublishAsync(
                instance,
                JournalEventTypes.WorkflowFailed,
                "Workflow failed compilation before execution.",
                data: compilationResult.Diagnostics
                    .GroupBy(diagnostic => diagnostic.Code)
                    .ToDictionary(
                        group => group.Key,
                        group => (object)string.Join("; ", group.Select(diagnostic => $"{diagnostic.Severity}: {diagnostic.Message} (Node: {diagnostic.NodeId})"))),
                cancellationToken: cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }

        return compilationResult.Plan;
    }
}
