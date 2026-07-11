using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

public interface IExecutionJournalWriter
{
    Task WriteAsync(ExecutionJournal entry);
}
