using System.Collections.Generic;

namespace Knotarium.Core.Domain;

/// <summary>
/// A centralized configuration container containing version metadata and groups.
/// </summary>
public record GroupContainer(int Version, IReadOnlyList<GroupDefinition> Groups);
