using System;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Infrastructure.Persistence;

public class PostgresExecutionJournalWriter : IExecutionJournalWriter
{
    public Task WriteAsync(ExecutionJournal entry)
    {
        // TODO(v1.5): Postgres provider implementation
        throw new NotImplementedException("Postgres execution journal writer is not yet implemented.");
    }
}
