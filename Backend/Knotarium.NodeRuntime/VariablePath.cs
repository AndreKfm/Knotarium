using System.Collections.Generic;

namespace Knotarium.NodeRuntime;

/// <summary>
/// A single navigation step into a variable value: either a named member
/// (object property / string-key) or a non-negative array index.
/// </summary>
public abstract record PathSegment
{
    public sealed record Member(string Name) : PathSegment;

    public sealed record Index(int Value) : PathSegment;
}

/// <summary>
/// A parsed variable reference: a head variable name plus an ordered list of
/// navigation segments. Shared by reads (expressions) and writes (Set Variable)
/// so the path syntax never diverges between them.
///
/// Grammar:
///   head        := first run of chars up to the first '.' or '['
///   .name       := member access
///   ["name"]    := member access (allows dots/spaces in the key)
///   ['name']    := member access
///   [0]         := non-negative array index
/// </summary>
public sealed record VariablePath(string Head, IReadOnlyList<PathSegment> Segments)
{
    /// <summary>
    /// Parse <paramref name="input"/> into a head + segments. Returns false on any
    /// malformed input (empty, empty segment, unterminated bracket, non-integer
    /// unquoted index). Never throws.
    /// </summary>
    public static bool TryParse(string? input, out VariablePath? path)
    {
        path = null;
        if (string.IsNullOrEmpty(input))
            return false;

        int i = 0;
        int len = input.Length;

        // Head: everything up to the first '.' or '['. Must be non-empty.
        int headStart = i;
        while (i < len && input[i] != '.' && input[i] != '[')
            i++;
        if (i == headStart)
            return false;
        string head = input.Substring(headStart, i - headStart);

        var segments = new List<PathSegment>();
        while (i < len)
        {
            char c = input[i];
            if (c == '.')
            {
                i++; // consume '.'
                int start = i;
                while (i < len && input[i] != '.' && input[i] != '[')
                    i++;
                if (i == start) // empty member, e.g. "a..b" or trailing '.'
                    return false;
                segments.Add(new PathSegment.Member(input.Substring(start, i - start)));
            }
            else if (c == '[')
            {
                i++; // consume '['
                if (i >= len)
                    return false; // unterminated
                char quote = input[i];
                if (quote == '"' || quote == '\'')
                {
                    i++; // consume opening quote
                    int start = i;
                    while (i < len && input[i] != quote)
                        i++;
                    if (i >= len) // unterminated quote
                        return false;
                    string key = input.Substring(start, i - start);
                    i++; // consume closing quote
                    if (i >= len || input[i] != ']')
                        return false;
                    i++; // consume ']'
                    segments.Add(new PathSegment.Member(key));
                }
                else
                {
                    int start = i;
                    while (i < len && input[i] != ']')
                        i++;
                    if (i >= len) // unterminated bracket
                        return false;
                    string body = input.Substring(start, i - start);
                    i++; // consume ']'
                    if (!int.TryParse(body, out int index) || index < 0)
                        return false;
                    segments.Add(new PathSegment.Index(index));
                }
            }
            else
            {
                return false;
            }
        }

        path = new VariablePath(head, segments);
        return true;
    }
}
