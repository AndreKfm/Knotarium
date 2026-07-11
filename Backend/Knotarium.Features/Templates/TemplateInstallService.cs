using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Features.Portability;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

using Knotarium.Features.Execution;

namespace Knotarium.Features.Templates;

/// <summary>Raised when supplied credential bindings are invalid (unknown slot, missing credential, type mismatch).</summary>
public sealed class TemplateBindingException(IReadOnlyList<string> errors)
    : InvalidOperationException("The supplied credential bindings are invalid.")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Import seam — the one piece of install that reaches into the workflow versioning stack. It exists as an
/// interface so <see cref="TemplateInstallService"/> can be unit-tested with a fake instead of standing up
/// <c>WorkflowPublisher</c>'s full dependency graph. The real implementation creates an inactive
/// <c>Imported</c> version.
/// </summary>
public interface ITemplateWorkflowImporter
{
    Task<int> ImportAsync(WorkflowExportDocument document, CancellationToken cancellationToken = default);
}

/// <summary>Adapts <see cref="WorkflowPublisher"/> to <see cref="ITemplateWorkflowImporter"/>.</summary>
public sealed class WorkflowPublisherTemplateImporter(WorkflowPublisher publisher) : ITemplateWorkflowImporter
{
    public async Task<int> ImportAsync(WorkflowExportDocument document, CancellationToken cancellationToken = default)
    {
        var result = await publisher.ImportAsync(document, cancellationToken).ConfigureAwait(false);
        return result.Version.VersionNumber;
    }
}

/// <summary>
/// Installs a <c>.kgtpl</c> as a brand-new draft workflow: verify → rebind bound slots → import as an
/// inactive <c>Imported</c> version, all in one DB transaction (no partial persistence on failure). A
/// fresh workflow id is minted so installing never clobbers an existing workflow and re-installing yields
/// a distinct copy. Unbound slots are allowed — the result reports them and the publish gate blocks
/// running until they are bound.
/// </summary>
public sealed class TemplateInstallService(
    AppDbContext dbContext,
    ITemplateWorkflowImporter workflowImporter,
    TemplateCompatibilityChecker compatibilityChecker,
    IWorkflowStore workflowStore)
{
    public async Task<TemplateInstallResult> InstallAsync(
        byte[] bytes,
        IReadOnlyDictionary<string, string>? credentialBindings,
        string? workflowName = null,
        IReadOnlyDictionary<string, string>? parameterValues = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var archive = TemplateArchiveCodec.Read(bytes);
        var document = TemplateWorkflowReader.ReadAndVerify(archive);
        var bindings = credentialBindings ?? new Dictionary<string, string>(StringComparer.Ordinal);

        await ValidateBindingsAsync(archive.Manifest, bindings, cancellationToken).ConfigureAwait(false);

        // Order matters: validate+coerce parameters → substitute {{param:…}} tokens → rebind slot:<key>.
        // Parameter values are sanitized (no slot:/{{param: tokens), so substitution can't inject a live
        // slot token into the rebind pass that follows.
        var values = TemplateParameterValidator.Validate(archive.Manifest.Parameters, parameterValues);
        document = CredentialSlotModule.SubstituteParameters(document, values);

        // Postcondition: with the value map total over every declared parameter, any token still present means
        // the graph references an UNDECLARED parameter — an authoring bug, not a "configure later" state like an
        // unbound slot. Fail rather than persist a workflow carrying a literal {{param:…}} token.
        var residual = CredentialSlotModule.FindUnsubstitutedParameters(document);
        if (residual.Count > 0)
        {
            throw new TemplateParameterException(
                residual.Select(key => $"The workflow references an undeclared parameter '{key}'.").ToList());
        }

        // Rewrite bound slot:<key> placeholders to real credential ids; unbound slots stay as placeholders.
        var rebind = CredentialSlotModule.RebindSlotsToIds(document, bindings);
        var openSlots = CredentialSlotModule.FindUnboundSlots(rebind.Document);

        // Effective name: caller's override, else the template name — then collision-suffixed so a
        // second import doesn't produce an indistinguishable duplicate in the list.
        var desiredName = string.IsNullOrWhiteSpace(workflowName) ? archive.Manifest.Name : workflowName.Trim();
        var existing = await workflowStore.ListAsync(cancellationToken).ConfigureAwait(false);
        var finalName = WorkflowNaming.EnsureUnique(desiredName, existing.Select(w => w.Name));

        // Mint a fresh workflow id so each install is a distinct draft.
        var newWorkflowId = Guid.NewGuid().ToString("N");
        var importDocument = new WorkflowExportDocument(
            rebind.Document.Manifest with { WorkflowId = newWorkflowId, WorkflowName = finalName },
            rebind.Document.Content);

        var compatibility = await compatibilityChecker
            .AssessAsync(importDocument, archive.Manifest.MinEngineVersion, cancellationToken)
            .ConfigureAwait(false);

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var versionNumber = await workflowImporter.ImportAsync(importDocument, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new TemplateInstallResult(
            WorkflowId: newWorkflowId,
            VersionNumber: versionNumber,
            WorkflowName: finalName,
            ReboundSlots: rebind.ReboundSlots,
            OpenSlots: openSlots,
            BindingErrors: [],
            ConfigurationRequired: openSlots.Count > 0,
            Runnable: compatibility.Supported && openSlots.Count == 0,
            Diagnostics: compatibility.Warnings);
    }

    private async Task ValidateBindingsAsync(
        TemplateManifest manifest,
        IReadOnlyDictionary<string, string> bindings,
        CancellationToken cancellationToken)
    {
        if (bindings.Count == 0)
        {
            return;
        }

        var declaredSlots = manifest.CredentialSlots.Select(slot => slot.Slot).ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();

        var boundIds = bindings.Values.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        var existingIds = await dbContext.Credentials
            .AsNoTracking()
            .Where(credential => boundIds.Contains(credential.Id))
            .Select(credential => credential.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existing = existingIds.ToHashSet(StringComparer.Ordinal);

        foreach (var (slotKey, credentialId) in bindings)
        {
            if (!declaredSlots.Contains(slotKey))
            {
                errors.Add($"Unknown credential slot '{slotKey}'.");
                continue;
            }

            if (!existing.Contains(credentialId))
            {
                errors.Add($"Slot '{slotKey}' is bound to a non-existent credential '{credentialId}'.");
            }

            // Note: RequiredCredentialType is advisory in schema v1. Credentials carry no type column today,
            // so there is nothing to match against — wire this check up once credential typing lands.
        }

        if (errors.Count > 0)
        {
            throw new TemplateBindingException(errors);
        }
    }
}
