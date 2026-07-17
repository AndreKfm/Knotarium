// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Knotarium.Tests.Persistence;

public sealed class FileWorkflowStoreTests : IDisposable
{
    private readonly string _tempFolder;
    private readonly FileWorkflowStore _store;

    public FileWorkflowStoreTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "knotarium_tests_" + Guid.NewGuid().ToString("N"));
        _store = new FileWorkflowStore(_tempFolder, NullLogger<FileWorkflowStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempFolder))
        {
            try
            {
                Directory.Delete(_tempFolder, recursive: true);
            }
            catch
            {
                // Best effort cleanup in tests
            }
        }
    }

    [Fact]
    public void Constructor_NullStoreFolder_ThrowsArgumentException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentException>(() => new FileWorkflowStore(null!, NullLogger<FileWorkflowStore>.Instance));
    }

    [Fact]
    public async Task GetAsync_WorkflowDoesNotExist_ReturnsNull()
    {
        // Act
        var result = await _store.GetAsync(WorkflowDefinitionId.New());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_WorkflowExists_ReturnsWorkflow()
    {
        // Arrange
        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "File Test",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());

        await _store.UpsertAsync(workflow);

        // Act
        var retrieved = await _store.GetAsync(workflowId);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("File Test", retrieved.Name);
        Assert.Equal(workflowId, retrieved.Id);
    }

    [Fact]
    public async Task ListAsync_EmptyFolder_ReturnsEmptyList()
    {
        // Act
        var result = await _store.ListAsync();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListAsync_WorkflowsExist_ReturnsSortedWorkflows()
    {
        // Arrange
        var wfB = new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Bravo",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());
        var wfA = new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Alpha",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());

        await _store.UpsertAsync(wfB);
        await _store.UpsertAsync(wfA);

        // Act
        var list = await _store.ListAsync();

        // Assert
        Assert.Equal(2, list.Count);
        Assert.Collection(
            list,
            item => Assert.Equal("Alpha", item.Name),
            item => Assert.Equal("Bravo", item.Name));
    }

    [Fact]
    public async Task UpsertAsync_NewWorkflow_SavesFileToDisk()
    {
        // Arrange
        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "New Workflow",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());

        // Act
        var persisted = await _store.UpsertAsync(workflow);

        // Assert
        Assert.Equal("New Workflow", persisted.Name);
        var expectedPath = Path.Combine(_tempFolder, "workflows", $"{workflowId.Value}.json");
        Assert.True(File.Exists(expectedPath));

        var fileContent = await File.ReadAllTextAsync(expectedPath);
        Assert.Contains("New Workflow", fileContent);
    }

    [Fact]
    public async Task UpdateAsync_WorkflowDoesNotExist_ReturnsNull()
    {
        // Arrange
        var workflow = new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Non-existent",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());

        // Act
        var result = await _store.UpdateAsync(workflow);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_WorkflowExists_OverwritesDiskFile()
    {
        // Arrange
        var workflowId = WorkflowDefinitionId.New();
        var original = new WorkflowDefinition(
            workflowId,
            "Original",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());
        await _store.UpsertAsync(original);

        var updated = original with { Name = "Updated Name" };

        // Act
        var result = await _store.UpdateAsync(updated);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Updated Name", result.Name);

        var retrieved = await _store.GetAsync(workflowId);
        Assert.NotNull(retrieved);
        Assert.Equal("Updated Name", retrieved.Name);
    }

    [Fact]
    public async Task DeleteAsync_WorkflowDoesNotExist_ReturnsFalse()
    {
        // Act
        var deleted = await _store.DeleteAsync(WorkflowDefinitionId.New());

        // Assert
        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_WorkflowExists_DeletesDiskFileAndReturnsTrue()
    {
        // Arrange
        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "To Delete",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());
        await _store.UpsertAsync(workflow);

        var expectedPath = Path.Combine(_tempFolder, "workflows", $"{workflowId.Value}.json");
        Assert.True(File.Exists(expectedPath));

        // Act
        var deleted = await _store.DeleteAsync(workflowId);

        // Assert
        Assert.True(deleted);
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public async Task SaveGroupsAsync_ValidContainer_PersistsGroupsAndReturnsEtag()
    {
        // Arrange
        var groups = new[]
        {
            new GroupDefinition("grp_prod", "Production", "#FF0000")
        };
        var container = new GroupContainer(1, groups);

        // Act
        var etag1 = await _store.SaveGroupsAsync(container);
        var (retrieved, etag2) = await _store.GetGroupsWithETagAsync();

        // Assert
        Assert.NotEmpty(etag1);
        Assert.Equal(etag1, etag2);
        Assert.Single(retrieved.Groups);
        var firstGroup = retrieved.Groups[0];
        Assert.Equal("grp_prod", firstGroup.Id);
        Assert.Equal("Production", firstGroup.Name);
        Assert.Equal("#FF0000", firstGroup.Color);
    }

    [Fact]
    public async Task SaveGroupsAsync_InvalidIfMatch_ThrowsGroupPreconditionFailedException()
    {
        // Arrange
        var groups = new[]
        {
            new GroupDefinition("grp_prod", "Production", "#FF0000")
        };
        var container = new GroupContainer(1, groups);

        // Act & Assert
        await Assert.ThrowsAsync<GroupPreconditionFailedException>(() =>
            _store.SaveGroupsAsync(container, ifMatch: "invalid-etag"));
    }

    [Fact]
    public async Task SaveGroupsAsync_InvalidGroupIdPattern_ThrowsArgumentException()
    {
        // Arrange
        var groups = new[]
        {
            new GroupDefinition("invalid_id", "Production", "#FF0000")
        };
        var container = new GroupContainer(1, groups);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.SaveGroupsAsync(container));
    }

    [Fact]
    public async Task SaveGroupsAsync_InvalidColor_ThrowsArgumentException()
    {
        // Arrange
        var groups = new[]
        {
            new GroupDefinition("grp_prod", "Production", "red")
        };
        var container = new GroupContainer(1, groups);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.SaveGroupsAsync(container));
    }

    [Fact]
    public async Task SaveGroupsAsync_ExtremelyLongName_ThrowsArgumentException()
    {
        // Arrange
        var name = new string('A', 81);
        var groups = new[]
        {
            new GroupDefinition("grp_prod", name, "#00FF00")
        };
        var container = new GroupContainer(1, groups);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.SaveGroupsAsync(container));
    }

    [Fact]
    public async Task DeleteGroupAsync_NonEmptyGroup_RemovesGroupAndNullifiesWorkflowMetadataAndWrites()
    {
        // Arrange
        var groupId = "grp_marketing";
        var groups = new[]
        {
            new GroupDefinition(groupId, "Marketing", "#00FF00")
        };
        await _store.SaveGroupsAsync(new GroupContainer(1, groups));

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Marketing Emailer",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>(),
            new WorkflowMetadata(Group: groupId));
        await _store.UpsertAsync(workflow);

        // Act
        await _store.DeleteGroupAsync(groupId);

        // Assert
        var (container, _) = await _store.GetGroupsWithETagAsync();
        Assert.Empty(container.Groups);

        var retrievedWorkflow = await _store.GetAsync(workflowId);
        Assert.NotNull(retrievedWorkflow);
        Assert.Null(retrievedWorkflow.Metadata?.Group);
    }
}
