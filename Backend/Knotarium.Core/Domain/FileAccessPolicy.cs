using System;
using System.Collections.Generic;

namespace Knotarium.Core.Domain;

/// <summary>
/// The kind of file access a node requests, and (as flags) what a single allow-rule grants.
/// A rule granting <see cref="ReadWrite"/> satisfies both a <see cref="Read"/> and a <see cref="Write"/>
/// request; a request is always exactly one of Read or Write.
/// </summary>
[Flags]
public enum FileAccessMode
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
}

/// <summary>
/// One allowed directory subtree. <see cref="Path"/> is an absolute directory; access is granted to that
/// directory and everything beneath it (recursive) for the operations in <see cref="Mode"/>.
/// </summary>
public sealed record FileAccessRule(string Path, FileAccessMode Mode);

/// <summary>
/// The instance-global file-access policy consulted by the built-in file nodes before any IO.
/// <para>
/// Secure by default: a fresh instance has <see cref="TotalAccess"/> off and no <see cref="Rules"/>, so
/// every file operation is denied until an admin grants a path (or turns on total access, which must be
/// confirmed explicitly in the UI). <see cref="MinFreeBytes"/> / <see cref="MinFreePercent"/> guard writes
/// so a workflow can never fill the target drive past the reserve; whichever threshold is stricter wins.
/// </para>
/// </summary>
public sealed record FileAccessPolicy(
    bool TotalAccess,
    IReadOnlyList<FileAccessRule> Rules,
    long? MinFreeBytes,
    double? MinFreePercent)
{
    /// <summary>The secure default: nothing permitted, no free-space reserve.</summary>
    public static FileAccessPolicy Denied { get; } =
        new(TotalAccess: false, Rules: Array.Empty<FileAccessRule>(), MinFreeBytes: null, MinFreePercent: null);
}

/// <summary>Outcome of a policy check. <see cref="CanonicalPath"/> is the resolved, boundary-checked
/// absolute path a caller should actually use for IO (never the raw, unvalidated input).</summary>
public sealed record FileAccessResult(bool Allowed, string? CanonicalPath, string? DenyReason)
{
    public static FileAccessResult Allow(string canonicalPath) => new(true, canonicalPath, null);
    public static FileAccessResult Deny(string reason) => new(false, null, reason);
}
