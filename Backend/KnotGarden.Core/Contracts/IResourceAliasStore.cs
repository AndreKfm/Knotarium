using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Core.Contracts;

/// <summary>
/// Optional resource-aliasing capability an <see cref="IExternalSignalProvider"/> MAY also implement so a
/// graph can re-point one logical resource number onto another at runtime — an indirection the provider
/// maintains as durable, inspectable host state rather than a fire-and-forget side effect. The host stays
/// vendor-neutral: a "resource type" is just an opaque provider-defined dimension name (the provider gives
/// the numbers and the type their meaning), and an alias is a per-target mapping
/// <c>(resourceType, oldNumber) → newNumber</c>.
///
/// Aliases are durable per target (they survive host restart, unlike run-scoped variables), single-hop
/// (no transitive chaining: a reference to <c>old</c> resolves to its direct <c>newNumber</c> only), and
/// override-on-write (a later alias for the same <c>(resourceType, oldNumber)</c> replaces the earlier
/// one). Writing an identity alias (<c>newNumber == oldNumber</c>) clears the mapping.
///
/// This seam captures and exposes the state only. Whether resolution is actually woven into outbound /
/// inbound addressing is a separate concern owned by the provider; until it is, the alias is queryable but
/// does not yet alter dispatch.
/// </summary>
public interface IResourceAliasStore
{
    /// <summary>
    /// Record (or replace) the alias <c>(resourceType, oldNumber) → newNumber</c> for a target, durably.
    /// An identity alias (<paramref name="newNumber"/> equals <paramref name="oldNumber"/>) clears any
    /// existing mapping for that key.
    /// </summary>
    ValueTask SetAliasAsync(string targetId, string resourceType, long oldNumber, long newNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a resource number through the alias table for a target. Single-hop: returns the directly
    /// mapped <c>newNumber</c> when an alias exists for <c>(resourceType, number)</c>, otherwise returns
    /// <paramref name="number"/> unchanged.
    /// </summary>
    long ResolveAlias(string targetId, string resourceType, long number);

    /// <summary>
    /// All currently recorded aliases, optionally scoped to one target (null = every target). Inspection
    /// surface for admin/query — the redirect is observable state, not a black box.
    /// </summary>
    IReadOnlyList<ResourceAlias> GetAliases(string? targetId = null);
}

/// <summary>One recorded resource alias: on <see cref="TargetId"/>, <see cref="OldNumber"/> of
/// <see cref="ResourceType"/> is re-pointed onto <see cref="NewNumber"/>.</summary>
public sealed record ResourceAlias(string TargetId, string ResourceType, long OldNumber, long NewNumber);
