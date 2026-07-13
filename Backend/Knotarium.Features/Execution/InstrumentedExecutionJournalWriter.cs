using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Execution;

public sealed class InstrumentedExecutionJournalWriter : IExecutionJournalWriter
{
    private readonly IExecutionJournalWriter _inner;
    private readonly ExecutionTelemetry _telemetry;

    public InstrumentedExecutionJournalWriter(IExecutionJournalWriter inner, ExecutionTelemetry telemetry)
    {
        _inner = inner;
        _telemetry = telemetry;
    }

    public async Task WriteAsync(ExecutionJournal entry)
    {
        var sw = Stopwatch.StartNew();
        await _inner.WriteAsync(entry);
        sw.Stop();

        _telemetry.RecordJournalWriteLatency(sw.Elapsed);
    }

    // Must forward to the inner writer's bulk path — the interface's default implementation would fall
    // back to row-by-row WriteAsync and silently undo the batching layered above this decorator.
    public async Task WriteBatchAsync(System.Collections.Generic.IReadOnlyList<ExecutionJournal> entries)
    {
        var sw = Stopwatch.StartNew();
        await _inner.WriteBatchAsync(entries);
        sw.Stop();

        _telemetry.RecordJournalWrites(entries.Count, sw.Elapsed);
    }
}