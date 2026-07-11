using System;

namespace KnotGarden.Core.Domain;

/// <summary>
/// A template the user saved into this instance's library (as opposed to the read-only built-in gallery).
/// The packed <c>.kgtpl</c> is the source of truth — held base64-encoded, mirroring the <see cref="Credential"/>
/// storage convention. The remaining columns are a denormalized projection of the packed manifest, written
/// only at save time so the listing can be served without unpacking every archive (and never drift).
/// </summary>
public class UserTemplate
{
    /// <summary>The stable template id (derived from the source workflow). Unique — re-saving replaces.</summary>
    public string TemplateId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TemplateVersion { get; set; } = string.Empty;

    /// <summary>The full template manifest, serialized — served on list without unpacking the archive.</summary>
    public string ManifestJson { get; set; } = string.Empty;

    /// <summary>The packed <c>.kgtpl</c> archive, base64-encoded.</summary>
    public string ArchiveBase64 { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
