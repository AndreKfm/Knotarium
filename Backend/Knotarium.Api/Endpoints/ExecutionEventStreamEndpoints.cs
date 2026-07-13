using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api;

/// <summary>
/// Server-sent-events stream of an execution's journal entries. Catch-up (optionally resuming from a
/// Last-Event-ID) is read from the persisted journal; the live tail is pushed by the in-process
/// <see cref="SseEventPublisher"/> — the same publisher every journal write already feeds via
/// WorkflowExecutor.PublishJournalEntryAsync — instead of polling the database on a timer.
///
/// Ordering matters: subscribe to the publisher <em>before</em> the catch-up read, so an entry
/// produced during catch-up is buffered on the channel rather than lost in the gap. The live drain
/// then de-duplicates against whatever catch-up already sent (by timestamp, then id).
/// </summary>
public static class ExecutionEventStreamEndpoints
{
    public static void MapExecutionEventStreamEndpoints(this WebApplication app)
    {
        app.MapGet("/api/executions/{id}/events", async (HttpContext context, Guid id, IServiceScopeFactory scopeFactory, SseEventPublisher publisher) =>
        {
            var execId = new ExecutionInstanceId(id);

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var instanceExists = await db.ExecutionInstances.AnyAsync(e => e.Id == execId);
                if (!instanceExists)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new { message = "Execution instance not found" });
                    return;
                }
            }

            // Reserve a bounded subscriber slot before writing any SSE headers, so we can cleanly answer 503
            // when the global live-subscriber cap is reached instead of accepting an unbounded connection.
            var channel = publisher.TrySubscribe(execId);
            if (channel is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { message = "Live event subscriber limit reached; retry shortly." });
                return;
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var cancellationToken = context.RequestAborted;
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var lastTimestamp = DateTimeOffset.MinValue;
            var seenEntryIdsAtLastTimestamp = new List<Guid>();

            async Task WriteEntryAsync(ExecutionJournal entry)
            {
                var data = JsonSerializer.Serialize(entry, jsonOptions);
                await context.Response.WriteAsync($"id: {entry.Id}\n", cancellationToken);
                await context.Response.WriteAsync($"event: {entry.EventType}\n", cancellationToken);
                await context.Response.WriteAsync($"data: {data}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);

                if (entry.Timestamp > lastTimestamp)
                {
                    lastTimestamp = entry.Timestamp;
                    seenEntryIdsAtLastTimestamp.Clear();
                }
                seenEntryIdsAtLastTimestamp.Add(entry.Id);
            }

            bool AlreadySent(ExecutionJournal entry) =>
                entry.Timestamp < lastTimestamp
                || (entry.Timestamp == lastTimestamp && seenEntryIdsAtLastTimestamp.Contains(entry.Id));

            // Channel is already subscribed above (before headers) so live entries produced during the
            // catch-up DB read are buffered, not dropped.
            try
            {
                // If the client is resuming, anchor the cursor at the referenced entry so catch-up only
                // replays what came after it.
                if (context.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventIdStr) &&
                    Guid.TryParse(lastEventIdStr.ToString(), out var lastEventId))
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var lastEntry = await db.JournalEntries
                        .FirstOrDefaultAsync(j => j.ExecutionInstanceId == execId && j.Id == lastEventId, cancellationToken);
                    if (lastEntry != null)
                    {
                        lastTimestamp = lastEntry.Timestamp;
                        seenEntryIdsAtLastTimestamp.Add(lastEntry.Id);
                    }
                }

                // Catch-up: replay every persisted entry at/after the cursor (without a Last-Event-ID this is
                // the full history). Then the live tail takes over from the publisher.
                using (var scope = scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var historicalEntries = await db.JournalEntries
                        .Where(j => j.ExecutionInstanceId == execId &&
                            (j.Timestamp > lastTimestamp ||
                             (j.Timestamp == lastTimestamp && !seenEntryIdsAtLastTimestamp.Contains(j.Id))))
                        .OrderBy(j => j.Timestamp)
                        .ThenBy(j => j.Id)
                        .ToListAsync(cancellationToken);

                    foreach (var entry in historicalEntries)
                    {
                        await WriteEntryAsync(entry);
                    }
                }

                // Live tail: push entries as the executor publishes them, skipping anything catch-up already sent.
                await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    if (AlreadySent(entry))
                    {
                        continue;
                    }
                    await WriteEntryAsync(entry);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal client disconnect
            }
            finally
            {
                publisher.Unsubscribe(execId, channel);
            }
        });
    }
}
