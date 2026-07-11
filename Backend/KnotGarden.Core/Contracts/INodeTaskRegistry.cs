using System.Diagnostics.CodeAnalysis;

namespace KnotGarden.Core.Contracts;

public interface INodeTaskRegistry
{
    INodeTask? GetTask(string nodeType);
}
