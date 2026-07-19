// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

/// <summary>
/// Exhaustive coverage of the file-access policy decision logic: deny-by-default, path grants + modes,
/// path-attack containment (traversal, sibling-prefix, cross-volume, symlink escape), total access, and the
/// write free-space reserve.
/// </summary>
public class FileAccessGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kg-fa-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "kg-fa-out-" + Guid.NewGuid().ToString("N"));

    public FileAccessGuardTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_outside, recursive: true); } catch { /* best-effort */ }
    }

    private sealed class StubProvider : IFileAccessPolicyProvider
    {
        private readonly FileAccessPolicy _policy;
        public StubProvider(FileAccessPolicy policy) => _policy = policy;
        public Task<FileAccessPolicy> GetPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_policy);
    }

    private static FileAccessGuard Guard(FileAccessPolicy policy) => new(new StubProvider(policy));

    private static FileAccessPolicy Grant(string path, FileAccessMode mode, long? minBytes = null, double? minPct = null)
        => new(TotalAccess: false, Rules: new[] { new FileAccessRule(path, mode) }, MinFreeBytes: minBytes, MinFreePercent: minPct);

    [Fact]
    public async Task Denies_everything_by_default()
    {
        var guard = Guard(FileAccessPolicy.Denied);

        Assert.False((await guard.CheckReadAsync(Path.Combine(_root, "a.txt"))).Allowed);
        Assert.False((await guard.CheckWriteAsync(Path.Combine(_root, "a.txt"), 10, append: false)).Allowed);
    }

    [Fact]
    public async Task Read_grant_allows_read_but_not_write()
    {
        var guard = Guard(Grant(_root, FileAccessMode.Read));

        var read = await guard.CheckReadAsync(Path.Combine(_root, "a.txt"));
        Assert.True(read.Allowed);
        Assert.Equal(Path.Combine(_root, "a.txt"), read.CanonicalPath);

        var write = await guard.CheckWriteAsync(Path.Combine(_root, "a.txt"), 10, append: false);
        Assert.False(write.Allowed);
    }

    [Fact]
    public async Task Write_grant_allows_write_into_not_yet_existing_subtree()
    {
        var guard = Guard(Grant(_root, FileAccessMode.ReadWrite));

        // Parent folder does not exist yet — the nearest-existing-ancestor resolution must still bound it.
        var write = await guard.CheckWriteAsync(Path.Combine(_root, "sub", "deep", "f.txt"), 10, append: false);
        Assert.True(write.Allowed);
    }

    [Fact]
    public async Task Rejects_parent_traversal_out_of_grant()
    {
        var guard = Guard(Grant(_root, FileAccessMode.ReadWrite));

        var escape = Path.Combine(_root, "..", "escape.txt"); // resolves above the granted root
        Assert.False((await guard.CheckReadAsync(escape)).Allowed);
        Assert.False((await guard.CheckWriteAsync(escape, 10, append: false)).Allowed);
    }

    [Fact]
    public async Task Rejects_absolute_path_outside_grant()
    {
        var guard = Guard(Grant(_root, FileAccessMode.ReadWrite));
        Assert.False((await guard.CheckReadAsync(Path.Combine(_outside, "x.txt"))).Allowed);
    }

    [Fact]
    public async Task Rejects_sibling_directory_with_shared_prefix()
    {
        // A grant of ".../root" must not leak into ".../root-evil".
        var evil = _root + "-evil";
        Directory.CreateDirectory(evil);
        try
        {
            var guard = Guard(Grant(_root, FileAccessMode.ReadWrite));
            Assert.False((await guard.CheckReadAsync(Path.Combine(evil, "x.txt"))).Allowed);
        }
        finally
        {
            try { Directory.Delete(evil, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Rejects_relative_path_under_enforced_policy()
    {
        var guard = Guard(Grant(_root, FileAccessMode.ReadWrite));
        var result = await guard.CheckReadAsync(Path.Combine("relative", "x.txt"));
        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task Total_access_allows_arbitrary_absolute_path()
    {
        var guard = Guard(new FileAccessPolicy(TotalAccess: true, Rules: Array.Empty<FileAccessRule>(), MinFreeBytes: null, MinFreePercent: null));

        Assert.True((await guard.CheckReadAsync(Path.Combine(_outside, "x.txt"))).Allowed);
        Assert.True((await guard.CheckWriteAsync(Path.Combine(_outside, "x.txt"), 10, append: false)).Allowed);
    }

    [Fact]
    public async Task Write_blocked_when_it_would_breach_absolute_free_space_reserve()
    {
        // An impossibly large absolute reserve — no drive can satisfy it, so any write is blocked.
        var guard = Guard(Grant(_root, FileAccessMode.Write, minBytes: long.MaxValue));
        var result = await guard.CheckWriteAsync(Path.Combine(_root, "big.bin"), 1, append: false);
        Assert.False(result.Allowed);
        Assert.Contains("reserve", result.DenyReason);
    }

    [Fact]
    public async Task Write_blocked_when_it_would_breach_percentage_free_space_reserve()
    {
        // Requiring 100% of the drive free can never hold — verifies the percentage branch also triggers.
        var guard = Guard(Grant(_root, FileAccessMode.Write, minPct: 100.0));
        var result = await guard.CheckWriteAsync(Path.Combine(_root, "big.bin"), 1, append: false);
        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task Write_allowed_when_reserve_is_satisfiable()
    {
        var guard = Guard(Grant(_root, FileAccessMode.Write, minBytes: 1, minPct: 0));
        var result = await guard.CheckWriteAsync(Path.Combine(_root, "small.bin"), 1, append: false);
        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task Rejects_symlinked_directory_escaping_the_grant()
    {
        // A symlink INSIDE the granted root pointing OUTSIDE must not widen access. Requires symlink
        // privileges; if the OS/user can't create one, treat as inconclusive rather than fail.
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch
        {
            return; // no symlink privilege on this host — skip
        }

        var guard = Guard(Grant(_root, FileAccessMode.ReadWrite));
        var viaLink = Path.Combine(link, "x.txt"); // real target is _outside/x.txt, outside the grant
        Assert.False((await guard.CheckReadAsync(viaLink)).Allowed);
        Assert.False((await guard.CheckWriteAsync(viaLink, 10, append: false)).Allowed);
    }

    [Fact]
    public async Task Rejects_symlink_parent_when_a_real_file_exists_below_it()
    {
        // Regression: a reparse point in the MIDDLE of the path (not the deepest existing ancestor) must still
        // be resolved. Here 'link' -> _outside, and a real file exists at _outside/secret.txt, so the walk-up
        // stops at the existing leaf and would never inspect the parent junction under a leaf-only resolver.
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch
        {
            return; // no symlink privilege on this host — skip
        }

        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "top secret"); // reachable as link/secret.txt

        var guard = Guard(Grant(_root, FileAccessMode.ReadWrite));
        var viaLink = Path.Combine(link, "secret.txt"); // real target is _outside/secret.txt, outside the grant
        Assert.False((await guard.CheckReadAsync(viaLink)).Allowed);
        Assert.False((await guard.CheckWriteAsync(viaLink, 10, append: false)).Allowed);
    }

    [Fact]
    public async Task Rejects_negative_write_size()
    {
        var guard = Guard(Grant(_root, FileAccessMode.Write, minBytes: 1));
        var result = await guard.CheckWriteAsync(Path.Combine(_root, "f.bin"), bytesToWrite: -1, append: false);
        Assert.False(result.Allowed);
    }
}
