namespace Knotarium.Core.Domain;

/// <summary>
/// Shared defaults for the built-in <c>inlineCode</c> node. Lives in Core so the manifest
/// catalog (Compiler slice) and the executor (Nodes slice) agree on the enforced timeout
/// without a cross-slice dependency between them.
/// </summary>
public static class InlineCodeNodeDefaults
{
    /// <summary>Default wall-clock timeout for an inlineCode script, in seconds.</summary>
    public const int TimeoutSeconds = 30;
}
