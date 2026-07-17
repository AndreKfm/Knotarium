// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Execution;

/// <summary>
/// Buffers journal entries and flushes them as one multi-row transaction, bounded by whichever fires
/// first: a count bound (<see cref="ExecutionOptions.JournalBatchMaxSize"/>) or a time bound
/// (<see cref="ExecutionOptions.JournalBatchMaxDelayMilliseconds"/>). Under concurrent runs the journal
/// is the highest-volume write path and row-by-row INSERTs maximize SQLite write-lock acquisitions;
/// batching cuts them by roughly the batch size.
/// </summary>
/// <remarks>
/// <para><b>Durability contract.</b> Entries the crash-recovery protocol depends on are never left in the
/// buffer: a critical entry's <see cref="WriteAsync"/> triggers an immediate flush and completes only once
/// the entry is on disk. Critical = run suspend/terminal (<c>WorkflowSuspended/Completed/Failed</c>), the
/// external-effect protocol (<c>AttemptingExternalEffect</c> must be durable BEFORE the effect fires, and
/// any <c>AttemptId</c>-bearing completion so recovery doesn't false-positive a finished effect), and
/// <c>ManualDecisionRecorded</c>. A crash therefore loses at most the last few tens of milliseconds of
/// non-critical trace entries (node started/completed, variable updates).</para>
/// <para><b>Live visibility is unaffected:</b> SSE publishes from memory (<c>ExecutionJournalPublisher</c>
/// publishes after this returns), so only DB readers (run inspector) can lag by up to the time bound.</para>
/// <para>Entries flush in enqueue order (single reader loop), preserving the per-run journal order.</para>
/// </remarks>
public sealed class BatchingExecutionJournalWriter : IExecutionJournalWriter, IAsyncDisposable
{
    private readonly record struct PendingEntry(ExecutionJournal Entry, TaskCompletionSource? Completion);

    private readonly IExecutionJournalWriter _inner;
    private readonly ExecutionOptions _options;
    private readonly ExecutionTelemetry? _telemetry;
    private readonly ILogger<BatchingExecutionJournalWriter>? _logger;
    private readonly Channel<PendingEntry> _channel;
    private readonly Task _flushLoop;

    public BatchingExecutionJournalWriter(
        IExecutionJournalWriter inner,
        ExecutionOptions options,
        ExecutionTelemetry? telemetry = null,
        ILogger<BatchingExecutionJournalWriter>? logger = null)
    {
        _inner = inner;
        _options = options;
        _telemetry = telemetry;
        _logger = logger;
        _channel = Channel.CreateUnbounded<PendingEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _flushLoop = Task.Run(FlushLoopAsync);
    }

    public async Task WriteAsync(ExecutionJournal entry)
    {
        if (IsCritical(entry))
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_channel.Writer.TryWrite(new PendingEntry(entry, completion)))
            {
                await _inner.WriteAsync(entry); // disposed/completed → write through directly
                return;
            }

            // Awaited to disk: the flush loop flushes immediately on seeing a critical entry, and this
            // returns only once the batch containing it (and everything enqueued before it) is committed.
            await completion.Task;
            return;
        }

        if (!_channel.Writer.TryWrite(new PendingEntry(entry, null)))
        {
            await _inner.WriteAsync(entry);
        }
    }

    public Task WriteBatchAsync(IReadOnlyList<ExecutionJournal> entries)
    {
        // Already-batched input (unusual — this type is the batcher): pass through unbuffered.
        return _inner.WriteBatchAsync(entries);
    }

    private static bool IsCritical(ExecutionJournal entry)
    {
        return entry.EventType is JournalEventTypes.WorkflowSuspended
                or JournalEventTypes.WorkflowCompleted
                or JournalEventTypes.WorkflowFailed
                or JournalEventTypes.AttemptingExternalEffect
                or JournalEventTypes.ManualDecisionRecorded
            || (entry.Data?.ContainsKey("AttemptId") ?? false);
    }

    private async Task FlushLoopAsync()
    {
        var reader = _channel.Reader;
        var maxSize = _options.JournalBatchMaxSize;
        var maxDelay = TimeSpan.FromMilliseconds(_options.JournalBatchMaxDelayMilliseconds);
        var batch = new List<PendingEntry>(maxSize);

        // WaitToReadAsync returns false only when the writer is completed AND the channel is drained,
        // so disposal flushes every remaining entry before the loop exits.
        while (await reader.WaitToReadAsync())
        {
            batch.Clear();
            var hasCritical = false;

            while (batch.Count < maxSize && reader.TryRead(out var pending))
            {
                batch.Add(pending);
                hasCritical |= pending.Completion is not null;
            }

            // No critical entry and room left: wait out the time bound to accumulate more, but flush the
            // moment a critical entry arrives (its writer is awaiting durability).
            if (!hasCritical && batch.Count < maxSize)
            {
                var deadline = DateTime.UtcNow + maxDelay;
                while (batch.Count < maxSize && !hasCritical)
                {
                    if (reader.TryRead(out var pending))
                    {
                        batch.Add(pending);
                        hasCritical |= pending.Completion is not null;
                        continue;
                    }

                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    using var timeBound = new CancellationTokenSource(remaining);
                    try
                    {
                        if (!await reader.WaitToReadAsync(timeBound.Token))
                        {
                            break; // channel completed — flush what we have
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break; // time bound elapsed
                    }
                }
            }

            await FlushBatchAsync(batch);
        }
    }

    private async Task FlushBatchAsync(List<PendingEntry> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _inner.WriteBatchAsync(batch.Select(pending => pending.Entry).ToList());
            stopwatch.Stop();
            _telemetry?.RecordJournalBatchFlush(batch.Count, stopwatch.Elapsed);

            foreach (var pending in batch)
            {
                pending.Completion?.TrySetResult();
            }
        }
        catch (Exception batchException)
        {
            // One bad row (e.g. its run was retention-deleted while the entry sat in the buffer) must not
            // sink the whole batch: retry per-row so only the failing rows are affected. A critical row's
            // failure propagates to its awaiting writer, matching the unbatched failure behavior.
            _logger?.LogWarning(batchException,
                "Journal batch flush of {Count} entries failed; retrying entries individually.", batch.Count);

            foreach (var pending in batch)
            {
                try
                {
                    await _inner.WriteAsync(pending.Entry);
                    pending.Completion?.TrySetResult();
                }
                catch (Exception rowException)
                {
                    _logger?.LogError(rowException,
                        "Journal entry {EntryId} ({EventType}) for execution {ExecutionId} could not be written and was dropped.",
                        pending.Entry.Id, pending.Entry.EventType, pending.Entry.ExecutionInstanceId.Value);
                    pending.Completion?.TrySetException(rowException);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _flushLoop; // drains and flushes everything still buffered
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Journal batch flush loop faulted during shutdown drain.");
        }
    }
}
