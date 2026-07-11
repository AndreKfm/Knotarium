using System;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Infrastructure.Persistence;

public class PostgresExecutionJournalWriter : IExecutionJournalWriter
{
    public Task WriteAsync(ExecutionJournal entry)
    {
        // TODO(v1.5): Postgres provider implementation
        throw new NotImplementedException("Postgres execution journal writer is not yet implemented.");
    }
}
