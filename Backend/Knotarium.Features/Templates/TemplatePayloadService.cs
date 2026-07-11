using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Features.Portability;

using Knotarium.Features.Execution;

namespace Knotarium.Features.Templates;

/// <summary>
/// The graph a template carries, plus its metadata and compatibility — for <em>inserting</em> a template
/// into an already-open workflow (as opposed to installing it as a new workflow). The credential
/// references stay as <c>slot:</c> placeholders; the caller merges the nodes/edges onto the canvas and
/// the existing publish gate blocks running until the slots are bound.
/// </summary>
public sealed record TemplatePayloadResult(
    TemplateManifest Manifest,
    System.Collections.Generic.IReadOnlyList<TemplateCredentialSlot> CredentialSlots,
    TemplateCompatibility Compatibility,
    WorkflowExportContent Content);

/// <summary>Reads + verifies a <c>.kgtpl</c> and returns its node/edge graph without creating a workflow.</summary>
public sealed class TemplatePayloadService(TemplateCompatibilityChecker compatibilityChecker)
{
    public async Task<TemplatePayloadResult> GetPayloadAsync(
        byte[] bytes,
        IReadOnlyDictionary<string, string>? parameterValues = null,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(bytes);

        var archive = TemplateArchiveCodec.Read(bytes);
        var document = TemplateWorkflowReader.ReadAndVerify(archive);

        // Substitute declared parameters before returning the graph, so an inserted template already
        // carries real values (defaults prefill the form, so the common case needs no input).
        var values = TemplateParameterValidator.Validate(archive.Manifest.Parameters, parameterValues);
        document = CredentialSlotModule.SubstituteParameters(document, values);

        var compatibility = await compatibilityChecker
            .AssessAsync(document, archive.Manifest.MinEngineVersion, cancellationToken)
            .ConfigureAwait(false);

        return new TemplatePayloadResult(
            archive.Manifest,
            archive.Manifest.CredentialSlots,
            compatibility,
            document.Content);
    }
}
