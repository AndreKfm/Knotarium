using System;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Features.Security;

namespace KnotGarden.Features.Templates;

/// <summary>
/// Reads and validates an untrusted <c>.kgtpl</c> and reports its manifest, credential slots, engine
/// compatibility, and any privileged nodes — <strong>without importing</strong>. This is what the upload UI
/// calls before committing, so the user sees which slots need binding, whether the template will run, and
/// whether it carries filesystem/code/database access. Parse-and-validate only.
/// </summary>
public sealed class TemplateInspectService(
    TemplateCompatibilityChecker compatibilityChecker,
    INodePackageManifestProvider manifestProvider)
{
    public async Task<TemplateInspectResult> InspectAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var archive = TemplateArchiveCodec.Read(bytes);
        var document = TemplateWorkflowReader.ReadAndVerify(archive);
        var compatibility = await compatibilityChecker
            .AssessAsync(document, archive.Manifest.MinEngineVersion, cancellationToken)
            .ConfigureAwait(false);
        var privilegedNodes = await PrivilegedNodeScanner
            .ScanAsync(manifestProvider, document.Content.Nodes, cancellationToken)
            .ConfigureAwait(false);

        return new TemplateInspectResult(archive.Manifest, archive.Manifest.CredentialSlots, compatibility, privilegedNodes);
    }
}
