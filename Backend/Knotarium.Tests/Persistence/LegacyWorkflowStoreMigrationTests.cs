// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Knotarium.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Knotarium.Tests.Persistence;

public sealed class LegacyWorkflowStoreMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacy;
    private readonly string _target;

    public LegacyWorkflowStoreMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "knotarium_migration_tests_" + Guid.NewGuid().ToString("N"));
        _legacy = Path.Combine(_root, "legacy");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best effort cleanup in tests
            }
        }
    }

    [Fact]
    public void Migrate_LegacyMissing_IsNoOp()
    {
        LegacyWorkflowStoreMigration.Migrate(_target, _legacy, NullLogger.Instance);

        Assert.False(Directory.Exists(_target));
    }

    [Fact]
    public void Migrate_MovesWorkflowsAndGroupsIntoTarget()
    {
        Directory.CreateDirectory(Path.Combine(_legacy, "workflows"));
        File.WriteAllText(Path.Combine(_legacy, "workflows", "a.json"), "{\"id\":\"a\"}");
        File.WriteAllText(Path.Combine(_legacy, "workflows", "b.json"), "{\"id\":\"b\"}");
        File.WriteAllText(Path.Combine(_legacy, "groups.json"), "[]");

        LegacyWorkflowStoreMigration.Migrate(_target, _legacy, NullLogger.Instance);

        Assert.Equal("{\"id\":\"a\"}", File.ReadAllText(Path.Combine(_target, "workflows", "a.json")));
        Assert.Equal("{\"id\":\"b\"}", File.ReadAllText(Path.Combine(_target, "workflows", "b.json")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(_target, "groups.json")));
        // Moved, not copied — the emptied legacy workflows dir is removed so nothing ghosts around.
        Assert.False(File.Exists(Path.Combine(_legacy, "groups.json")));
        Assert.False(Directory.Exists(Path.Combine(_legacy, "workflows")));
    }

    [Fact]
    public void Migrate_ExistingTargetFiles_AreNeverOverwritten()
    {
        Directory.CreateDirectory(Path.Combine(_legacy, "workflows"));
        File.WriteAllText(Path.Combine(_legacy, "workflows", "a.json"), "legacy");
        File.WriteAllText(Path.Combine(_legacy, "groups.json"), "legacy-groups");
        Directory.CreateDirectory(Path.Combine(_target, "workflows"));
        File.WriteAllText(Path.Combine(_target, "workflows", "a.json"), "current");
        File.WriteAllText(Path.Combine(_target, "groups.json"), "current-groups");

        LegacyWorkflowStoreMigration.Migrate(_target, _legacy, NullLogger.Instance);

        Assert.Equal("current", File.ReadAllText(Path.Combine(_target, "workflows", "a.json")));
        Assert.Equal("current-groups", File.ReadAllText(Path.Combine(_target, "groups.json")));
        // The skipped legacy files stay where they are (non-destructive on conflict).
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(_legacy, "workflows", "a.json")));
        Assert.Equal("legacy-groups", File.ReadAllText(Path.Combine(_legacy, "groups.json")));
    }

    [Fact]
    public void Migrate_TargetEqualsLegacy_IsNoOp()
    {
        Directory.CreateDirectory(Path.Combine(_legacy, "workflows"));
        File.WriteAllText(Path.Combine(_legacy, "workflows", "a.json"), "{}");

        LegacyWorkflowStoreMigration.Migrate(_legacy, _legacy, NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(_legacy, "workflows", "a.json")));
    }

    [Fact]
    public void Migrate_IsIdempotent_SecondRunChangesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_legacy, "workflows"));
        File.WriteAllText(Path.Combine(_legacy, "workflows", "a.json"), "{}");

        LegacyWorkflowStoreMigration.Migrate(_target, _legacy, NullLogger.Instance);
        LegacyWorkflowStoreMigration.Migrate(_target, _legacy, NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(_target, "workflows", "a.json")));
    }
}
