// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Xunit;

namespace Knotarium.Tests.Execution;

public class BatchingExecutionJournalWriterTests
{
    private sealed class RecordingJournalWriter : IExecutionJournalWriter
    {
        private readonly object _gate = new();
        private readonly List<ExecutionJournal> _delivered = new();
        private readonly List<int> _batchSizes = new();
        private int _singleWrites;
        private int _batchFailuresRemaining;

        public RecordingJournalWriter(int failFirstBatches = 0)
        {
            _batchFailuresRemaining = failFirstBatches;
        }

        public IReadOnlyList<ExecutionJournal> Delivered
        {
            get { lock (_gate) { return _delivered.ToList(); } }
        }

        public IReadOnlyList<int> BatchSizes
        {
            get { lock (_gate) { return _batchSizes.ToList(); } }
        }

        public int SingleWrites
        {
            get { lock (_gate) { return _singleWrites; } }
        }

        public Task WriteAsync(ExecutionJournal entry)
        {
            lock (_gate)
            {
                _singleWrites++;
                _delivered.Add(entry);
            }
            return Task.CompletedTask;
        }

        public Task WriteBatchAsync(IReadOnlyList<ExecutionJournal> entries)
        {
            lock (_gate)
            {
                if (_batchFailuresRemaining > 0)
                {
                    _batchFailuresRemaining--;
                    throw new InvalidOperationException("Simulated batch write failure.");
                }

                _batchSizes.Add(entries.Count);
                _delivered.AddRange(entries);
            }
            return Task.CompletedTask;
        }
    }

    private static ExecutionJournal Entry(string eventType, Dictionary<string, object>? data = null) => new()
    {
        Id = Guid.NewGuid(),
        ExecutionInstanceId = ExecutionInstanceId.New(),
        NodeId = null,
        Timestamp = DateTimeOffset.UtcNow,
        EventType = eventType,
        Message = eventType,
        Data = data ?? new Dictionary<string, object>(),
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for: {what}");
    }

    [Fact]
    public async Task CriticalEntry_IsDurableWhenWriteAsyncReturns_AndPrecedingEntriesFlushWithIt()
    {
        var inner = new RecordingJournalWriter();
        await using var writer = new BatchingExecutionJournalWriter(inner, new ExecutionOptions());

        var trace = Entry(JournalEventTypes.NodeExecutionStarted);
        await writer.WriteAsync(trace); // buffered, returns immediately

        var terminal = Entry(JournalEventTypes.WorkflowCompleted);
        await writer.WriteAsync(terminal);

        // The awaited critical write guarantees the terminal entry — and everything enqueued before
        // it — is already on disk here, without waiting for any time bound.
        var delivered = inner.Delivered;
        Assert.Equal(new[] { trace.Id, terminal.Id }, delivered.Select(e => e.Id).ToArray());
    }

    [Theory]
    [InlineData(JournalEventTypes.WorkflowSuspended)]
    [InlineData(JournalEventTypes.WorkflowFailed)]
    [InlineData(JournalEventTypes.AttemptingExternalEffect)]
    [InlineData(JournalEventTypes.ManualDecisionRecorded)]
    public async Task DurabilityCriticalEventTypes_AreAwaitedToDisk(string eventType)
    {
        var inner = new RecordingJournalWriter();
        await using var writer = new BatchingExecutionJournalWriter(inner, new ExecutionOptions());

        var entry = Entry(eventType);
        await writer.WriteAsync(entry);

        Assert.Contains(inner.Delivered, e => e.Id == entry.Id);
    }

    [Fact]
    public async Task AttemptIdBearingCompletion_IsTreatedAsCritical()
    {
        var inner = new RecordingJournalWriter();
        await using var writer = new BatchingExecutionJournalWriter(inner, new ExecutionOptions());

        // The external-effect completion marker: crash recovery matches it by AttemptId, so it must
        // not sit in the buffer (a lost completion would false-positive a manual decision).
        var completion = Entry(JournalEventTypes.NodeExecutionCompleted, new Dictionary<string, object> { ["AttemptId"] = Guid.NewGuid().ToString() });
        await writer.WriteAsync(completion);

        Assert.Contains(inner.Delivered, e => e.Id == completion.Id);
    }

    [Fact]
    public async Task NonCriticalEntries_FlushWithinTheTimeBound_InEnqueueOrder()
    {
        var inner = new RecordingJournalWriter();
        await using var writer = new BatchingExecutionJournalWriter(
            inner, new ExecutionOptions { JournalBatchMaxDelayMilliseconds = 25 });

        var entries = Enumerable.Range(0, 5).Select(_ => Entry(JournalEventTypes.NodeExecutionStarted)).ToList();
        foreach (var entry in entries)
        {
            await writer.WriteAsync(entry);
        }

        await WaitUntilAsync(() => inner.Delivered.Count == entries.Count, "buffered entries to flush on the time bound");
        Assert.Equal(entries.Select(e => e.Id), inner.Delivered.Select(e => e.Id));
    }

    [Fact]
    public async Task CountBound_CapsEveryFlushedBatch()
    {
        var inner = new RecordingJournalWriter();
        await using var writer = new BatchingExecutionJournalWriter(
            inner, new ExecutionOptions { JournalBatchMaxSize = 4, JournalBatchMaxDelayMilliseconds = 200 });

        var entries = Enumerable.Range(0, 10).Select(_ => Entry(JournalEventTypes.VariableUpdated)).ToList();
        foreach (var entry in entries)
        {
            await writer.WriteAsync(entry);
        }

        await WaitUntilAsync(() => inner.Delivered.Count == entries.Count, "all entries to flush");
        Assert.All(inner.BatchSizes, size => Assert.True(size <= 4, $"batch of {size} exceeded the count bound of 4"));
        Assert.Equal(entries.Select(e => e.Id), inner.Delivered.Select(e => e.Id));
    }

    [Fact]
    public async Task BatchFailure_FallsBackToPerRowWrites_AndCriticalWriterStillSucceeds()
    {
        var inner = new RecordingJournalWriter(failFirstBatches: 1);
        // Long time bound: nothing flushes until the critical entry arrives, so the failing batch
        // deterministically contains all three entries.
        await using var writer = new BatchingExecutionJournalWriter(
            inner, new ExecutionOptions { JournalBatchMaxDelayMilliseconds = 1000 });

        var traceA = Entry(JournalEventTypes.NodeExecutionStarted);
        var traceB = Entry(JournalEventTypes.VariableUpdated);
        await writer.WriteAsync(traceA);
        await writer.WriteAsync(traceB);

        // Awaited critical write: the batch attempt fails once, the per-row fallback lands all three,
        // and the awaiting caller must NOT observe the swallowed batch failure.
        var terminal = Entry(JournalEventTypes.WorkflowFailed);
        await writer.WriteAsync(terminal);

        Assert.Equal(3, inner.SingleWrites);
        Assert.Equal(new[] { traceA.Id, traceB.Id, terminal.Id }, inner.Delivered.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task Dispose_DrainsEverythingStillBuffered()
    {
        var inner = new RecordingJournalWriter();
        var writer = new BatchingExecutionJournalWriter(
            inner, new ExecutionOptions { JournalBatchMaxDelayMilliseconds = 1000 });

        var entries = Enumerable.Range(0, 5).Select(_ => Entry(JournalEventTypes.NodeExecutionStarted)).ToList();
        foreach (var entry in entries)
        {
            await writer.WriteAsync(entry);
        }

        await writer.DisposeAsync(); // must flush, not drop, the buffer

        Assert.Equal(entries.Select(e => e.Id), inner.Delivered.Select(e => e.Id));
    }

    [Fact]
    public async Task WriteAfterDispose_WritesThroughDirectly()
    {
        var inner = new RecordingJournalWriter();
        var writer = new BatchingExecutionJournalWriter(inner, new ExecutionOptions());
        await writer.DisposeAsync();

        var entry = Entry(JournalEventTypes.NodeExecutionStarted);
        await writer.WriteAsync(entry);

        Assert.Contains(inner.Delivered, e => e.Id == entry.Id);
        Assert.Equal(1, inner.SingleWrites);
    }
}
