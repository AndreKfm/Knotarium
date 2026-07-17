// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Enforces the instance-global <see cref="FileAccessPolicy"/> for the built-in file nodes.
/// <para>
/// The path-safety contract (the security-critical part): a requested path is made absolute, its symlinks
/// and junctions are resolved to their real target (walking up to the nearest existing ancestor so a
/// not-yet-created write target is still checked against a real parent), and the result must sit inside an
/// admin-granted directory subtree with the right mode. Textual <c>..</c>, an absolute path pointing
/// elsewhere, a different drive, and a symlink escaping an allowed root are all rejected. Writes additionally
/// must leave the configured free-space reserve on the target drive.
/// </para>
/// </summary>
public sealed class FileAccessGuard : IFileAccessPolicy
{
    private readonly IFileAccessPolicyProvider _provider;

    public FileAccessGuard(IFileAccessPolicyProvider provider)
    {
        _provider = provider;
    }

    public async Task<FileAccessResult> CheckReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var policy = await _provider.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        return Evaluate(policy, path, FileAccessMode.Read, bytesToWrite: 0, append: false);
    }

    public async Task<FileAccessResult> CheckWriteAsync(string path, long bytesToWrite, bool append, CancellationToken cancellationToken = default)
    {
        var policy = await _provider.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        return Evaluate(policy, path, FileAccessMode.Write, bytesToWrite, append);
    }

    /// <summary>
    /// Pure policy evaluation — no async, no ambient state — so it is exhaustively unit-testable. Exposed
    /// internally for tests; production callers go through the async methods which fetch the live policy.
    /// </summary>
    internal static FileAccessResult Evaluate(FileAccessPolicy policy, string path, FileAccessMode requested, long bytesToWrite, bool append)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FileAccessResult.Deny("File access denied: an empty path was requested.");
        }

        // Total access = the old unrestricted behaviour: resolve (relative allowed, against CWD) and permit,
        // but still honour the free-space reserve on writes so a runaway workflow can't fill the disk.
        if (policy.TotalAccess)
        {
            string canonicalTotal;
            try
            {
                canonicalTotal = ResolveRealPath(Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                return FileAccessResult.Deny($"File access denied: path could not be resolved ({ex.Message}).");
            }

            if (requested == FileAccessMode.Write)
            {
                var (spaceOk, spaceReason) = CheckFreeSpace(policy, canonicalTotal, bytesToWrite, append);
                if (!spaceOk)
                {
                    return FileAccessResult.Deny(spaceReason!);
                }
            }
            return FileAccessResult.Allow(canonicalTotal);
        }

        // Enforced mode. A relative path cannot be safely bounded against absolute grants — reject it
        // outright rather than resolving it against an unpredictable working directory.
        if (!Path.IsPathFullyQualified(path))
        {
            return FileAccessResult.Deny($"File access denied: only absolute paths are allowed, got '{path}'.");
        }

        // Resolve up front so every deny reason can name the exact path that was attempted — the UI parses
        // it (the single-quoted segment) to offer a one-click "grant this path" action.
        string canonical;
        try
        {
            canonical = ResolveRealPath(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            return FileAccessResult.Deny($"File access denied: path '{path}' could not be resolved ({ex.Message}).");
        }

        if (policy.Rules.Count == 0)
        {
            return FileAccessResult.Deny(
                $"File access denied: the path '{canonical}' is not permitted (nothing is granted yet). An administrator must grant it under Settings → File Access.");
        }

        var matchedRoot = false;
        var matchedWithMode = false;
        foreach (var rule in policy.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Path))
            {
                continue;
            }

            string ruleRoot;
            try
            {
                ruleRoot = ResolveRealPath(Path.GetFullPath(rule.Path));
            }
            catch
            {
                continue; // a malformed configured root can't grant anything
            }

            if (!IsWithin(ruleRoot, canonical))
            {
                continue;
            }

            matchedRoot = true;
            if ((rule.Mode & requested) == requested)
            {
                matchedWithMode = true;
                break;
            }
        }

        if (!matchedWithMode)
        {
            if (matchedRoot)
            {
                var need = requested == FileAccessMode.Write ? "writing" : "reading";
                return FileAccessResult.Deny($"File access denied: the path '{canonical}' is within a grant that does not permit {need}.");
            }
            return FileAccessResult.Deny($"File access denied: the path '{canonical}' is outside every permitted directory.");
        }

        if (requested == FileAccessMode.Write)
        {
            var (spaceOk, spaceReason) = CheckFreeSpace(policy, canonical, bytesToWrite, append);
            if (!spaceOk)
            {
                return FileAccessResult.Deny(spaceReason!);
            }
        }

        return FileAccessResult.Allow(canonical);
    }

    /// <summary>
    /// Resolve a full path to its real on-disk location: follow symlinks/junctions on the deepest existing
    /// ancestor, then re-append the not-yet-existing tail. Defeats a symlinked directory that points outside
    /// an allowed root (the classic escape for a write to a new file under a hostile parent).
    /// </summary>
    private static string ResolveRealPath(string fullPath)
    {
        var current = fullPath;
        var tail = new Stack<string>();

        while (!File.Exists(current) && !Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                // Nothing along the path exists (e.g. a brand-new tree). The textual full path is already
                // '..'-collapsed, which is the best we can do without a real ancestor to resolve.
                return fullPath;
            }
            tail.Push(Path.GetFileName(current));
            current = parent;
        }

        string real;
        try
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            real = target?.FullName ?? current;
        }
        catch
        {
            real = current;
        }

        real = Path.GetFullPath(real);
        while (tail.Count > 0)
        {
            real = Path.Combine(real, tail.Pop());
        }
        return Path.GetFullPath(real);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> itself or lives beneath it.
    /// Uses a relative-path boundary test so a sibling like <c>C:\data-evil</c> never matches <c>C:\data</c>,
    /// and a different drive is rejected outright.</summary>
    private static bool IsWithin(string root, string candidate)
    {
        var rel = Path.GetRelativePath(root, candidate);
        if (rel == ".")
        {
            return true;
        }
        if (Path.IsPathRooted(rel))
        {
            return false; // different volume — GetRelativePath returns an absolute path
        }
        if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                          || rel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false; // escapes upward out of the root
        }
        return true;
    }

    private static (bool ok, string? reason) CheckFreeSpace(FileAccessPolicy policy, string canonicalPath, long bytesToWrite, bool append)
    {
        if (policy.MinFreeBytes is null && policy.MinFreePercent is null)
        {
            return (true, null);
        }

        DriveInfo drive;
        long available;
        long total;
        try
        {
            var root = Path.GetPathRoot(canonicalPath);
            if (string.IsNullOrEmpty(root))
            {
                return (false, "File write denied: could not determine the target drive for the free-space check.");
            }
            drive = new DriveInfo(root);
            available = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch (Exception ex)
        {
            return (false, $"File write denied: could not read free space on the target drive ({ex.Message}).");
        }

        // On overwrite the existing file's bytes are reclaimed, so only the net growth needs headroom.
        long existingSize = 0;
        if (!append)
        {
            try
            {
                if (File.Exists(canonicalPath))
                {
                    existingSize = new FileInfo(canonicalPath).Length;
                }
            }
            catch
            {
                existingSize = 0; // treat as a fresh write if the size can't be read
            }
        }
        var needed = append ? bytesToWrite : Math.Max(0, bytesToWrite - existingSize);

        // "Whichever triggers first" = the stricter (larger) of the absolute and percentage reserves.
        long reserve = policy.MinFreeBytes ?? 0;
        if (policy.MinFreePercent is { } pct && pct > 0)
        {
            reserve = Math.Max(reserve, (long)(total * (pct / 100.0)));
        }

        if (available - needed < reserve)
        {
            return (false,
                $"File write denied: it would leave {FormatBytes(available - needed)} free on {drive.Name}, " +
                $"below the required reserve of {FormatBytes(reserve)}.");
        }
        return (true, null);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
