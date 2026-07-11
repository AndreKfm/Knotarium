using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Api.Services;

/// <summary>
/// Synchronizes persisted PollingTrigger rows from pollingTrigger nodes in a workflow definition.
/// Cursor is preserved across benign edits and reset when the source identity changes.
/// </summary>
internal sealed class WorkflowPollingTriggerSynchronizer : IWorkflowTriggerSynchronizer
{
    private static readonly string[] SourceIdentityKeys =
        { "sourceKind", "url", "serverConfigId", "operationId", "specVersion" };

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public WorkflowPollingTriggerSynchronizer(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task SyncAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var existing = await _dbContext.PollingTriggers
            .Where(p => p.WorkflowDefinitionId == workflow.Id)
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        foreach (var node in workflow.Nodes.Where(n => n.Type.Equals("pollingTrigger", StringComparison.OrdinalIgnoreCase)))
        {
            var intervalSeconds = GetIntervalSeconds(node);
            var configJson = BuildConfigJson(node);
            var id = WorkflowPollingTriggerIdFactory.Create(workflow.Id, node.Id);

            if (existing.Remove(id, out var row))
            {
                if (SourceIdentityChanged(row.ConfigJson, configJson))
                {
                    row.Cursor = null;
                }

                row.IntervalSeconds = intervalSeconds;
                row.ConfigJson = configJson;
                row.IsActive = true;
                // NextPollAtUtc is intentionally left untouched on update: the polling worker owns
                // the cadence after the first poll, so a save must not reset the schedule.
                continue;
            }

            await _dbContext.PollingTriggers.AddAsync(new PollingTrigger
            {
                Id = id,
                WorkflowDefinitionId = workflow.Id,
                IntervalSeconds = intervalSeconds,
                NextPollAtUtc = _timeProvider.GetUtcNow(),
                ConfigJson = configJson,
                Cursor = null,
                IsActive = true
            }, cancellationToken);
        }

        foreach (var obsolete in existing.Values)
        {
            _dbContext.PollingTriggers.Remove(obsolete);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static int GetIntervalSeconds(NodeDefinition node)
    {
        if (!node.Properties.TryGetValue("intervalSeconds", out var raw) || raw is null ||
            !int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= 0)
        {
            throw new InvalidOperationException(
                $"pollingTrigger node '{node.Id.Value}' has a missing or invalid 'intervalSeconds'.");
        }

        return seconds;
    }

    private static string BuildConfigJson(NodeDefinition node)
    {
        var config = new Dictionary<string, string>();
        foreach (var key in new[]
                 {
                     "sourceKind", "changeDetection", "jsonCursorPath",
                     "url", "method", "headersJson", "apiKeySecretRef",
                     "serverConfigId", "operationId", "specVersion"
                 })
        {
            if (node.Properties.TryGetValue(key, out var value) && value is not null)
            {
                config[key] = value.ToString()!;
            }
        }

        return JsonSerializer.Serialize(config);
    }

    private static bool SourceIdentityChanged(string oldConfigJson, string newConfigJson)
    {
        var oldConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(oldConfigJson) ?? new();
        var newConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(newConfigJson) ?? new();

        foreach (var key in SourceIdentityKeys)
        {
            oldConfig.TryGetValue(key, out var oldValue);
            newConfig.TryGetValue(key, out var newValue);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
