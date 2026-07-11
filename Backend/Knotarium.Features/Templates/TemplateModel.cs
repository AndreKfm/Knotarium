using System.Collections.Generic;

using Knotarium.Features.Execution;
using Knotarium.Features.Security;

namespace Knotarium.Features.Templates;

// ─────────────────────────────────────────────────────────────────────────────
// .kgtpl format — a single shareable workflow plus rich metadata. A template is
// the secret-free, credential-portabilized projection of one workflow's published
// state. Simpler than a bundle: no packages, no lock, no signatures.
//
// Inside the zip (closed entry-set for schemaVersion 1):
//   template.json  — TemplateManifest (metadata + declared credential slots)
//   workflow.json  — a WorkflowExportDocument with credential refs replaced by slot:<key>
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Constants pinning the on-disk template format and its content limits.</summary>
public static class TemplateFormat
{
    /// <summary>The current <c>.kgtpl</c> format version. Unknown values are rejected on read.</summary>
    public const int SchemaVersion = 1;

    public const string ManifestEntryName = "template.json";
    public const string WorkflowEntryName = "workflow.json";
    public const string Extension = ".kgtpl";

    /// <summary>The template-specific MIME type, with <c>application/zip</c> as the fallback.</summary>
    public const string ContentType = "application/vnd.knotarium.template+zip";

    // Content-shape limits (the archive byte/ratio limits live in WorkflowArchiveLimits).
    public const int MaxJsonDepth = 64;
    public const int MaxNodeCount = 2000;
    public const int MaxPropertyCountPerNode = 200;

    /// <summary>The engine version templates are checked against (advisory <c>minEngineVersion</c> baseline).</summary>
    public const string EngineVersion = "1.0.0";

    /// <summary>Derives the stable template id for a source workflow (stable across re-exports).</summary>
    public static string TemplateIdFor(string sourceWorkflowId) => "tpl_" + sourceWorkflowId;
}

/// <summary>
/// A symbolic credential slot. Intent only — never a secret value. A template's workflow references
/// credentials as <c>slot:&lt;Slot&gt;</c>; the installer rebinds these to real credential ids.
/// </summary>
public sealed record TemplateCredentialSlot(
    string Slot,
    string DisplayName,
    string? Description,
    string? RequiredCredentialType);

/// <summary>The declared type of a <see cref="TemplateParameter"/>. Drives coercion + the install-form input.</summary>
public static class TemplateParameterTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Enum = "enum";

    public static bool IsKnown(string? type)
        => type is String or Number or Boolean or Enum;
}

/// <summary>
/// A non-secret value the template author left blank for the installer to supply (a channel, base URL,
/// interval…). The workflow references it with a <c>{{param:&lt;Key&gt;}}</c> token; substitution at
/// install/insert replaces the token with the supplied (or default) value. Distinct from a credential
/// slot, which carries a secret reference.
/// </summary>
public sealed record TemplateParameter(
    string Key,
    string Label,
    string? Description,
    string Type,
    IReadOnlyList<string>? Options,
    string? Default,
    bool Required);

/// <summary><c>template.json</c> — authoring metadata plus the declared credential slots.</summary>
public sealed record TemplateManifest(
    string TemplateId,
    string TemplateVersion,
    int SchemaVersion,
    string Name,
    string Author,
    string Description,
    IReadOnlyList<string> Tags,
    string Category,
    string? MinEngineVersion,
    string CreatedAtUtc,
    string SourceWorkflowName,
    string WorkflowChecksum,
    IReadOnlyList<TemplateCredentialSlot> CredentialSlots)
{
    /// <summary>
    /// Declared install-time parameters. Additive in schemaVersion 1: an init-only property (not a
    /// positional ctor arg) so every existing constructor call and every pre-parameters <c>.kgtpl</c>
    /// keeps working — absent in JSON ⇒ an empty list, never null.
    /// </summary>
    public IReadOnlyList<TemplateParameter> Parameters { get; init; } = [];
}

/// <summary>The full in-memory contents of a <c>.kgtpl</c>.</summary>
public sealed record TemplateArchive(TemplateManifest Manifest, string WorkflowDocumentJson);

/// <summary>Compatibility assessment of a template against the current engine.</summary>
public sealed record TemplateCompatibility(bool Supported, IReadOnlyList<string> Warnings);

/// <summary>The request body for exporting a workflow as a template.</summary>
public sealed record TemplateExportRequest(
    string WorkflowId,
    string? Name = null,
    string? Author = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    string? Category = null,
    string? TemplateVersion = null,
    IReadOnlyList<TemplateParameter>? Parameters = null);

/// <summary>What credential references were lifted into slots during export, for user review.</summary>
public sealed record TemplatePortabilizationReport(
    IReadOnlyList<string> RewrittenPaths,
    IReadOnlyList<TemplateCredentialSlot> Slots);

/// <summary>The outcome of an export: the bytes to download plus what was portabilized.</summary>
public sealed record TemplateExportResult(
    byte[] Bytes,
    TemplateManifest Manifest,
    TemplatePortabilizationReport Report);

/// <summary>The result of inspecting a template without importing it.</summary>
public sealed record TemplateInspectResult(
    TemplateManifest Manifest,
    IReadOnlyList<TemplateCredentialSlot> CredentialSlots,
    TemplateCompatibility Compatibility,
    IReadOnlyList<PrivilegedNodeInfo> PrivilegedNodes);

/// <summary>The result of installing a template as a new draft workflow.</summary>
public sealed record TemplateInstallResult(
    string WorkflowId,
    int VersionNumber,
    string WorkflowName,
    IReadOnlyList<string> ReboundSlots,
    IReadOnlyList<string> OpenSlots,
    IReadOnlyList<string> BindingErrors,
    bool ConfigurationRequired,
    bool Runnable,
    IReadOnlyList<string> Diagnostics);

/// <summary>A built-in gallery entry: a template id paired with its parsed manifest.</summary>
public sealed record GalleryTemplate(string TemplateId, TemplateManifest Manifest);
