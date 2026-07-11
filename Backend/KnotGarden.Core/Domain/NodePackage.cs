using System.Collections.Generic;

namespace KnotGarden.Core.Domain;

public class NodePackage
{
    public NodePackageId Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<NodePackageVersion> Versions { get; set; } = new();
}
