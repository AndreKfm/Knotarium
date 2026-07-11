namespace Knotarium.Features.Compiler;

/// <summary>
/// Single source of truth for subflow variable namespacing, shared by the compiler (which rewrites
/// variable references at compile time) and the runtime script host (which scopes Inline Code's
/// GetVariable/SetVariable, since the compiler can't rewrite identifiers inside an opaque code string).
/// Both must agree exactly, or scoped reads/writes won't line up.
/// </summary>
public static class SubflowScope
{
    /// <summary>
    /// Identifier-safe scope token for a subflow-instance prefix (the inline path, e.g. "subflow-a"
    /// or "subflow-a/subflow-b"). Empty prefix (top level) => no scope. Non-alphanumeric chars are
    /// collapsed to '_' because the expression tokenizer treats '-' and '/' as delimiters.
    /// </summary>
    public static string FromPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return string.Empty;
        }
        var chars = prefix.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
            {
                chars[i] = '_';
            }
        }
        return "sf_" + new string(chars) + "__";
    }

    /// <summary>
    /// Scope for an inlined node id like "subflow-a/inline-1" — the prefix is everything before the
    /// last '/'. A top-level node id (no '/') yields no scope.
    /// </summary>
    public static string ForNodeId(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId))
        {
            return string.Empty;
        }
        var lastSlash = nodeId.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : FromPrefix(nodeId.Substring(0, lastSlash));
    }

    /// <summary>Prefix a variable name with a scope (no-op for empty scope/name).</summary>
    public static string Apply(string scope, string name)
        => string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(name) ? name : scope + name;
}
