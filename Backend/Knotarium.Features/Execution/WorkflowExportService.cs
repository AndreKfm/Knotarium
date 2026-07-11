using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Features.Portability;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Execution;

/// <summary>
/// Writes a workflow's current published version to the configured export folder as a deterministic,
/// secret-free file — the interop surface a user brings their own git (or rsync/CI/backup) to. The
/// database stays authoritative; the folder is a projection holding each workflow's current definition.
/// </summary>
public sealed class WorkflowExportService
{
    private readonly string _exportFolder;
    private readonly IPublishedWorkflowExportSource _publishedWorkflowSource;

    public WorkflowExportService(
        string exportFolder,
        IPublishedWorkflowExportSource publishedWorkflowSource)
    {
        if (string.IsNullOrWhiteSpace(exportFolder))
        {
            throw new ArgumentException("Export folder cannot be null or empty.", nameof(exportFolder));
        }

        ArgumentNullException.ThrowIfNull(publishedWorkflowSource);

        _exportFolder = exportFolder;
        _publishedWorkflowSource = publishedWorkflowSource;
    }

    /// <summary>
    /// Exports the workflow's current published state — the active version, or the latest version when
    /// none is active — to <c>{exportFolder}/workflows/{workflowId}.json</c>.
    /// </summary>
    /// <returns>The export result, or <see langword="null"/> when the workflow has no versions.</returns>
    public async Task<WorkflowExportResult?> ExportAsync(
        WorkflowDefinitionId workflowId,
        CancellationToken cancellationToken = default)
    {
        var published = await _publishedWorkflowSource.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (published is null)
        {
            return null;
        }

        var workflowsDir = Path.Combine(_exportFolder, "workflows");
        Directory.CreateDirectory(workflowsDir);
        var path = Path.Combine(workflowsDir, $"{workflowId.Value}.json");

        var json = WorkflowVersionSerializer.Serialize(published.Version, published.DisplayName);

        // Atomic write (temp + replace), mirroring FileWorkflowStore, so a reader never sees a half file.
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }

        return new WorkflowExportResult(path, published.Version.VersionNumber);
    }
}

/// <summary>The outcome of exporting a workflow version to the export folder.</summary>
/// <param name="FilePath">The absolute path the file was written to.</param>
/// <param name="VersionNumber">The exported version number.</param>
public sealed record WorkflowExportResult(string FilePath, int VersionNumber);
