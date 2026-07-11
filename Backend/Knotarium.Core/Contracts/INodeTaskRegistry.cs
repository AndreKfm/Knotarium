using System.Diagnostics.CodeAnalysis;

namespace Knotarium.Core.Contracts;

public interface INodeTaskRegistry
{
    INodeTask? GetTask(string nodeType);
}
