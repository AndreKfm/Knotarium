using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts.Options;

/// <summary>
/// A single selectable option surfaced in a dynamic-options / resource-locator parameter.
/// <see cref="Value"/> is the stable key persisted by the editor and resolved at run time;
/// <see cref="Label"/> is display-only and never used for resolution.
///
/// <see cref="Kind"/> and <see cref="EnumValues"/> are optional structured metadata that let a
/// consumer build a typed sub-editor for the option (e.g. a per-parameter form): <see cref="Kind"/>
/// is a generic value-kind hint ("String", "Integer", "Number", "Boolean", "Enum", "DateTime", …)
/// and <see cref="EnumValues"/> lists the allowed values when the kind is an enumeration. Both are
/// additive and null for loaders that don't supply them, so existing consumers ignore them.
/// </summary>
public sealed record OptionItem(
    string Label,
    string Value,
    string? Description = null,
    string? Kind = null,
    IReadOnlyList<string>? EnumValues = null);

/// <summary>The result of one design-time options query.</summary>
public sealed record OptionListResult(
    IReadOnlyList<OptionItem> Options,
    bool HasMore = false,
    string? NextPage = null);

/// <summary>
/// Inputs to a loader. <see cref="ConnectionId"/> selects the stored server config (BaseUrl +
/// credential). <see cref="DependsOn"/> carries parent-parameter values and loader configuration
/// and is <b>untrusted</b> — loaders must validate it before use. <see cref="Search"/> and
/// <see cref="PageCursor"/> support server-side filtering / pagination when the loader implements it.
/// </summary>
public sealed record OptionLoadContext(
    string? ConnectionId,
    IReadOnlyDictionary<string, string> DependsOn,
    string? Search = null,
    string? PageCursor = null);

/// <summary>
/// Loads the allowed values for a dynamic-options parameter at design time. Loading is a pure
/// query (no journal entry / side effect). Implementations resolve their own server config +
/// credential server-side and never return secrets to the caller.
/// </summary>
public interface IOptionsLoader
{
    /// <summary>Stable registry key, e.g. <c>&lt;integration&gt;.&lt;resource&gt;</c>.</summary>
    string Name { get; }

    Task<OptionListResult> LoadAsync(OptionLoadContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Keyed registry of the loaders the design-time endpoint is allowed to invoke. Doubles as the
/// allowlist: <see cref="Get"/> returns <c>null</c> for any name not explicitly registered.
/// </summary>
public interface IOptionsLoaderRegistry
{
    IOptionsLoader? Get(string name);
}
