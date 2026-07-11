using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// Custom exception thrown when there is an opportunistic concurrency violation on groups.
/// </summary>
public class GroupPreconditionFailedException : Exception
{
    public GroupPreconditionFailedException(string message) : base(message) { }
}

/// <summary>
/// Persists and retrieves workflow draft definitions on the local file system
/// in the user's AppData directory for Git versioning.
/// </summary>
public sealed class FileWorkflowStore : IWorkflowStore
{
    private readonly string _storeFolder;
    private readonly ILogger<FileWorkflowStore> _logger;
    private readonly SemaphoreSlim _workspaceLock = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new(PersistenceJsonOptions.Default)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="FileWorkflowStore"/> class.
    /// Uses the default path %APPDATA%/Knotarium.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public FileWorkflowStore(ILogger<FileWorkflowStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storeFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Knotarium");
        
        if (!Directory.Exists(_storeFolder))
        {
            Directory.CreateDirectory(_storeFolder);
        }

        var workflowsDir = Path.Combine(_storeFolder, "workflows");
        if (!Directory.Exists(workflowsDir))
        {
            Directory.CreateDirectory(workflowsDir);
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileWorkflowStore"/> class with a custom store folder.
    /// </summary>
    /// <param name="storeFolder">The folder where workflows are stored.</param>
    /// <param name="logger">The logger instance.</param>
    public FileWorkflowStore(string storeFolder, ILogger<FileWorkflowStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storeFolder = storeFolder ?? throw new ArgumentException("Store folder cannot be null or empty", nameof(storeFolder));
        
        if (!Directory.Exists(_storeFolder))
        {
            Directory.CreateDirectory(_storeFolder);
        }

        var workflowsDir = Path.Combine(_storeFolder, "workflows");
        if (!Directory.Exists(workflowsDir))
        {
            Directory.CreateDirectory(workflowsDir);
        }
    }

    private string GetFilePath(WorkflowDefinitionId id)
    {
        var workflowsDir = Path.Combine(_storeFolder, "workflows");
        if (!Directory.Exists(workflowsDir))
        {
            Directory.CreateDirectory(workflowsDir);
        }
        return Path.Combine(workflowsDir, $"{id.Value}.json");
    }

    /// <inheritdoc />
    public async Task<WorkflowDefinition?> GetAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default)
    {
        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetUnlockedAsync(workflowId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    private async Task<WorkflowDefinition?> GetUnlockedAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken)
    {
        var path = GetFilePath(workflowId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WorkflowDefinition>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read or deserialize workflow definition from file: {Path}", path);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ListUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    private async Task<IReadOnlyList<WorkflowDefinition>> ListUnlockedAsync(CancellationToken cancellationToken)
    {
        var list = new List<WorkflowDefinition>();
        var workflowsDir = Path.Combine(_storeFolder, "workflows");
        if (!Directory.Exists(workflowsDir))
        {
            return list;
        }

        var files = Directory.GetFiles(workflowsDir, "*.json");
        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(json, SerializerOptions);
                if (workflow != null)
                {
                    list.Add(workflow);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize workflow from file: {Path}", file);
            }
        }

        // Sort by Name, then ID to match DatabaseWorkflowStore consistency
        list.Sort((x, y) =>
        {
            var nameCompare = string.Compare(x.Name, y.Name, StringComparison.Ordinal);
            if (nameCompare != 0) return nameCompare;
            return string.Compare(x.Id.Value, y.Id.Value, StringComparison.Ordinal);
        });

        return list;
    }

    /// <inheritdoc />
    public async Task<WorkflowDefinition> UpsertAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UpsertUnlockedAsync(workflow, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    private async Task<WorkflowDefinition> UpsertUnlockedAsync(WorkflowDefinition workflow, CancellationToken cancellationToken)
    {
        var path = GetFilePath(workflow.Id);
        var json = JsonSerializer.Serialize(workflow, SerializerOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        return workflow;
    }

    /// <inheritdoc />
    public async Task<WorkflowDefinition?> UpdateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UpdateUnlockedAsync(workflow, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    private async Task<WorkflowDefinition?> UpdateUnlockedAsync(WorkflowDefinition workflow, CancellationToken cancellationToken)
    {
        var path = GetFilePath(workflow.Id);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = JsonSerializer.Serialize(workflow, SerializerOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        return workflow;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default)
    {
        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DeleteUnlockedAsync(workflowId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    private Task<bool> DeleteUnlockedAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken)
    {
        var path = GetFilePath(workflowId);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        try
        {
            File.Delete(path);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete workflow file: {Path}", path);
            return Task.FromResult(false);
        }
    }

    private string ComputeETag(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        return $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }

    private void ValidateGroups(GroupContainer container)
    {
        if (container == null)
        {
            throw new ArgumentNullException(nameof(container));
        }

        var groupIds = new HashSet<string>();
        foreach (var group in container.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id))
            {
                throw new ArgumentException("Group ID is required.");
            }

            var idRegex = new System.Text.RegularExpressions.Regex("^grp_[a-zA-Z0-9_-]+$");
            if (!idRegex.IsMatch(group.Id))
            {
                throw new ArgumentException($"Group ID '{group.Id}' must match grp_[a-zA-Z0-9_-]+ format.");
            }

            if (!groupIds.Add(group.Id))
            {
                throw new ArgumentException($"Group ID '{group.Id}' must be unique.");
            }

            if (string.IsNullOrWhiteSpace(group.Name))
            {
                throw new ArgumentException("Group Name is required.");
            }

            var trimmedName = (group.Name ?? "").Trim();
            if (trimmedName.Length > 80)
            {
                throw new ArgumentException("Group Name cannot exceed 80 characters.");
            }

            if (string.IsNullOrWhiteSpace(group.Color))
            {
                throw new ArgumentException("Group Color is required.");
            }

            var colorRegex = new System.Text.RegularExpressions.Regex("^#[0-9a-fA-F]{6}$");
            if (!colorRegex.IsMatch(group.Color))
            {
                throw new ArgumentException("Group Color must be a valid #RRGGBB hex string.");
            }
        }
    }

    private async Task<GroupContainer> ReadGroupsUnlockedAsync(CancellationToken ct)
    {
        var path = Path.Combine(_storeFolder, "groups.json");
        if (!File.Exists(path))
        {
            return new GroupContainer(1, Array.Empty<GroupDefinition>());
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<GroupContainer>(json, SerializerOptions) ?? new GroupContainer(1, Array.Empty<GroupDefinition>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read or deserialize groups from file: {Path}", path);
            return new GroupContainer(1, Array.Empty<GroupDefinition>());
        }
    }

    private async Task WriteGroupsUnlockedAsync(GroupContainer container, CancellationToken ct)
    {
        var path = Path.Combine(_storeFolder, "groups.json");
        var tempPath = path + ".tmp";
        
        var json = JsonSerializer.Serialize(container, SerializerOptions);
        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    private async Task<IReadOnlyList<WorkflowDefinition>> ReadAllDraftsUnlockedAsync(CancellationToken ct)
    {
        return await ListUnlockedAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteDraftUnlockedAsync(WorkflowDefinition draft, CancellationToken ct)
    {
        await UpsertUnlockedAsync(draft, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the list of groups and computes the latest ETag.
    /// </summary>
    public async Task<(GroupContainer Container, string ETag)> GetGroupsWithETagAsync(CancellationToken ct = default)
    {
        await _workspaceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_storeFolder, "groups.json");
            if (!File.Exists(path))
            {
                var container = new GroupContainer(1, Array.Empty<GroupDefinition>());
                var json = JsonSerializer.Serialize(container, SerializerOptions);
                var etag = ComputeETag(json);
                return (container, etag);
            }

            var actualJson = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var deserialized = JsonSerializer.Deserialize<GroupContainer>(actualJson, SerializerOptions) 
                               ?? new GroupContainer(1, Array.Empty<GroupDefinition>());
            return (deserialized, ComputeETag(actualJson));
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Saves groups under optimistic concurrency check ifIfMatch provided.
    /// </summary>
    public async Task<string> SaveGroupsAsync(GroupContainer container, string? ifMatch = null, CancellationToken ct = default)
    {
        var processedGroups = container.Groups.Select(g => g with { Name = (g.Name ?? "").Trim() }).ToList();
        var sanitizedContainer = container with { Groups = processedGroups };

        ValidateGroups(sanitizedContainer);

        await _workspaceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_storeFolder, "groups.json");
            string currentEtag;
            if (!File.Exists(path))
            {
                var emptyContainer = new GroupContainer(1, Array.Empty<GroupDefinition>());
                var emptyJson = JsonSerializer.Serialize(emptyContainer, SerializerOptions);
                currentEtag = ComputeETag(emptyJson);
            }
            else
            {
                var actualJson = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                currentEtag = ComputeETag(actualJson);
            }

            if (!string.IsNullOrEmpty(ifMatch) && ifMatch != currentEtag)
            {
                throw new GroupPreconditionFailedException("Optimistic concurrency violation. The groups file has been modified.");
            }

            var newJson = JsonSerializer.Serialize(sanitizedContainer, SerializerOptions);
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, newJson, ct).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }

            return ComputeETag(newJson);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Delete-with-Reassign pattern to remove a group from groups.json in a non-destructive way.
    /// </summary>
    public async Task DeleteGroupAsync(string groupId, CancellationToken ct = default)
    {
        await _workspaceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var container = await ReadGroupsUnlockedAsync(ct);
            if (container.Groups.All(g => g.Id != groupId))
            {
                return; // Idempotent no-op for unknown but valid IDs
            }

            var updatedGroups = container.Groups.Where(g => g.Id != groupId).ToList();
            await WriteGroupsUnlockedAsync(container with { Groups = updatedGroups }, ct);

            var drafts = await ReadAllDraftsUnlockedAsync(ct);
            foreach (var draft in drafts)
            {
                if (draft.Metadata?.Group != groupId)
                {
                    continue;
                }

                var updated = draft with
                {
                    Metadata = new WorkflowMetadata(Group: null)
                };

                await WriteDraftUnlockedAsync(updated, ct);
            }
        }
        finally
        {
            _workspaceLock.Release();
        }
    }
}
