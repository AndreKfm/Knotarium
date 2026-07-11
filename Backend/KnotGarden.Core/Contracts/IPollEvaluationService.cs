using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Core.Contracts;

/// <summary>Evaluates active polling triggers that are due and conditionally enqueues runs.</summary>
public interface IPollEvaluationService
{
    Task EvaluateDuePollsAsync(CancellationToken cancellationToken = default);
}
