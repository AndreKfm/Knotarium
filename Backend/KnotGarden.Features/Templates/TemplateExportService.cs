using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Features.Bundles;
using KnotGarden.Features.Portability;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

using KnotGarden.Features.Execution;

namespace KnotGarden.Features.Templates;

/// <summary>Raised when an export request is malformed (e.g. an invalid template version).</summary>
public sealed class TemplateExportException(string message) : InvalidOperationException(message);

/// <summary>
/// Exports a workflow's current published state as a portable <c>.kgtpl</c>: fetch the published version
/// via the shared source, portabilize its credential references into slots, recompute the checksum, and
/// pack the archive. Pure-core dependencies are injected so the orchestration is unit-testable.
/// </summary>
public sealed class TemplateExportService(
    IPublishedWorkflowExportSource publishedWorkflowSource,
    AppDbContext dbContext,
    TimeProvider timeProvider)
{
    public async Task<TemplateExportResult?> ExportAsync(
        TemplateExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WorkflowId))
        {
            throw new TemplateExportException("A workflowId is required.");
        }

        var templateVersion = string.IsNullOrWhiteSpace(request.TemplateVersion) ? "1.0.0" : request.TemplateVersion.Trim();
        if (!SemanticVersion.TryParse(templateVersion, out _))
        {
            throw new TemplateExportException($"templateVersion '{templateVersion}' is not a valid semantic version.");
        }

        var workflowId = new WorkflowDefinitionId(request.WorkflowId);
        var published = await publishedWorkflowSource.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (published is null)
        {
            return null;
        }

        var document = WorkflowVersionSerializer.Deserialize(
            WorkflowVersionSerializer.Serialize(published.Version, published.DisplayName));

        // Portabilize: replace host credential ids with slot:<key> placeholders.
        var credentials = await dbContext.Credentials
            .AsNoTracking()
            .Select(credential => new { credential.Id, credential.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var idToName = credentials.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);

        var extracted = CredentialSlotModule.ExtractIdsToSlots(document, idToName);
        var slots = extracted.Slots
            .Select(slot => new TemplateCredentialSlot(slot.Slot, slot.DisplayName, Description: null, RequiredCredentialType: null))
            .ToList();

        // Author-declared parameters are validated here (incl. optional-needs-default) so a shipped
        // template can never declare a parameter the installer can't satisfy.
        var parameters = TemplateParameterValidator.ValidateDeclarations(request.Parameters);

        var portableJson = WorkflowVersionSerializer.Serialize(extracted.Document);

        var manifest = new TemplateManifest(
            TemplateId: TemplateFormat.TemplateIdFor(request.WorkflowId),
            TemplateVersion: templateVersion,
            SchemaVersion: TemplateFormat.SchemaVersion,
            Name: Coalesce(request.Name, published.DisplayName),
            Author: request.Author ?? string.Empty,
            Description: request.Description ?? string.Empty,
            Tags: request.Tags ?? [],
            Category: request.Category ?? "uncategorized",
            MinEngineVersion: null,
            CreatedAtUtc: timeProvider.GetUtcNow().UtcDateTime.ToString("O"),
            SourceWorkflowName: published.DisplayName,
            WorkflowChecksum: extracted.Document.Manifest.Checksum,
            CredentialSlots: slots)
        {
            Parameters = parameters,
        };

        var bytes = TemplateArchiveCodec.Write(new TemplateArchive(manifest, portableJson));
        var report = new TemplatePortabilizationReport(extracted.RewrittenPaths, slots);
        return new TemplateExportResult(bytes, manifest, report);
    }

    private static string Coalesce(string? preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
}
