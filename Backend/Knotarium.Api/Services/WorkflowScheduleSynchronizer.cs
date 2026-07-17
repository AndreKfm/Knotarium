// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Schedules;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Api.Services;

/// <summary>
/// Synchronizes persisted schedule records from scheduler trigger nodes in a workflow definition.
/// </summary>
internal sealed class WorkflowScheduleSynchronizer : IWorkflowTriggerSynchronizer
{
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowScheduleSynchronizer"/> class.
    /// </summary>
    /// <param name="dbContext">The application database context.</param>
    /// <param name="timeProvider">The time provider used to compute future fire times.</param>
    public WorkflowScheduleSynchronizer(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Reconciles persisted schedules for the supplied workflow with its scheduler nodes.
    /// </summary>
    /// <param name="workflow">The workflow definition to inspect.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when schedule persistence is synchronized.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="workflow"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a scheduler node has invalid cron or time zone settings.</exception>
    public async Task SyncAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var existingSchedules = await _dbContext.Schedules
            .Where(schedule => schedule.WorkflowDefinitionId == workflow.Id)
            .ToDictionaryAsync(schedule => schedule.Id, cancellationToken);

        foreach (var schedulerNode in workflow.Nodes.Where(node => node.Type.Equals("scheduler", StringComparison.OrdinalIgnoreCase)))
        {
            var cronExpressionValue = GetRequiredProperty(schedulerNode, "cronExpression");
            var timeZoneId = GetRequiredProperty(schedulerNode, "timezoneId");
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var cronExpression = CronExpressionParser.Parse(cronExpressionValue);
            var nextFireAtUtc = cronExpression.GetNextOccurrence(_timeProvider.GetUtcNow(), timeZone)
                ?? throw new InvalidOperationException(
                    $"Scheduler node '{schedulerNode.Id.Value}' with cron '{cronExpressionValue}' in time zone '{timeZoneId}' has no future occurrence.");

            var scheduleId = WorkflowScheduleIdFactory.Create(workflow.Id, schedulerNode.Id);
            if (existingSchedules.Remove(scheduleId, out var existingSchedule))
            {
                existingSchedule.WorkflowDefinitionId = workflow.Id;
                existingSchedule.CronExpression = cronExpressionValue;
                existingSchedule.TimeZoneId = timeZoneId;
                existingSchedule.NextFireAtUtc = nextFireAtUtc;
                existingSchedule.IsActive = true;
                continue;
            }

            await _dbContext.Schedules.AddAsync(
                new Schedule
                {
                    Id = scheduleId,
                    WorkflowDefinitionId = workflow.Id,
                    CronExpression = cronExpressionValue,
                    TimeZoneId = timeZoneId,
                    NextFireAtUtc = nextFireAtUtc,
                    IsActive = true
                },
                cancellationToken);
        }

        foreach (var obsoleteSchedule in existingSchedules.Values)
        {
            _dbContext.Schedules.Remove(obsoleteSchedule);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string GetRequiredProperty(NodeDefinition schedulerNode, string propertyName)
    {
        if (!schedulerNode.Properties.TryGetValue(propertyName, out var rawValue) ||
            rawValue is null ||
            string.IsNullOrWhiteSpace(rawValue.ToString()))
        {
            throw new InvalidOperationException(
                $"Scheduler node '{schedulerNode.Id.Value}' is missing required property '{propertyName}'.");
        }

        return rawValue.ToString()!;
    }
}