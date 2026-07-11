using System;
using System.Diagnostics;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Execution;

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
}