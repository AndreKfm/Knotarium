using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Knotarium.NodeRuntime;

public class CollectibleAssemblyLoadContext : AssemblyLoadContext
{
    public CollectibleAssemblyLoadContext(string name) : base(name, isCollectible: true)
    {
    }

    public Assembly LoadFromBytes(byte[] assemblyBytes)
    {
        using var ms = new MemoryStream(assemblyBytes);
        return LoadFromStream(ms);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Return null to allow the default host context to resolve shared dependencies
        return null;
    }
}
