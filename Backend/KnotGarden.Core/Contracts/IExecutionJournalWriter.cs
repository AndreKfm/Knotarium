using System.Threading.Tasks;
using KnotGarden.Core.Domain;

namespace KnotGarden.Core.Contracts;

public interface IExecutionJournalWriter
{
    Task WriteAsync(ExecutionJournal entry);
}
