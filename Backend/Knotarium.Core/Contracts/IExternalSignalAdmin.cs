using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Optional administration capability an <see cref="IExternalSignalProvider"/> MAY also implement so its
/// configured targets can be created and edited from the host UI — turning "edit a config file and
/// restart" into "configure it in the app". The host stays vendor-neutral: it never knows what a target
/// is, only the generic shape below, and it renders the provider's own branding from
/// <see cref="ProviderDescriptor"/>. The provider owns persistence and applies edits live (no restart);
/// secrets are write-only across this seam (accepted on save, never returned).
///
/// A provider manages a single logical "system" (one routing/addressing domain) containing one or more
/// "targets" (the individual boxes). Multiple independent systems are out of scope for this seam.
/// </summary>
public interface IExternalSignalAdmin
{
    /// <summary>Static, vendor-supplied UI descriptor (branding + nouns + capability flags).</summary>
    ProviderDescriptor Describe();

    /// <summary>The current system and its targets (with live status; never any secret material).</summary>
    Task<ExternalSystemInfo> GetSystemAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Clear the live <see cref="SystemDiagnostics"/> feed (recent activity + counters). Observed-only
    /// data, so this just resets the in-memory readout; it changes no configuration. Returns the refreshed
    /// system. Providers with no diagnostics can no-op and return <see cref="GetSystemAsync"/>.
    /// </summary>
    Task<ExternalSystemInfo> ClearDiagnosticsAsync(CancellationToken cancellationToken);

    /// <summary>Rename the system (display only — the id is stable).</summary>
    Task<ExternalSystemInfo> RenameSystemAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Set one of the system-level boolean options the provider declares in
    /// <see cref="ExternalSystemInfo.Options"/>. The change is applied live (persisted by the provider,
    /// no restart). Throws <see cref="System.InvalidOperationException"/> for an unknown key. Providers
    /// that declare no options never receive this call from the UI, but must still reject unknown keys.
    /// </summary>
    Task<ExternalSystemInfo> SetOptionAsync(string key, bool value, CancellationToken cancellationToken);

    /// <summary>
    /// Create (<see cref="ExternalTargetEdit.Id"/> null/empty) or update a target. Connection edits are
    /// applied live: the provider drops and lazily rebuilds the affected connection. A null
    /// <see cref="ExternalTargetEdit.Password"/> leaves any stored secret untouched.
    /// </summary>
    Task<ExternalTargetInfo> UpsertTargetAsync(ExternalTargetEdit edit, CancellationToken cancellationToken);

    /// <summary>Remove a target and its stored secret; tears down its live connection.</summary>
    Task DeleteTargetAsync(string targetId, CancellationToken cancellationToken);

    /// <summary>
    /// Pull the target's catalog (channels/events/actions) live from the box and persist it into config,
    /// so the editor pickers reflect the real device. Returns the refreshed target.
    /// </summary>
    Task<ExternalTargetInfo> SyncTargetAsync(string targetId, CancellationToken cancellationToken);

    /// <summary>
    /// Probe connectivity for a candidate target (typically before first save) without persisting it.
    /// Uses the supplied <see cref="ExternalTargetEdit.Password"/>, or the stored secret when editing an
    /// existing target and no password is supplied.
    /// </summary>
    Task<TargetStatus> TestConnectionAsync(ExternalTargetEdit candidate, CancellationToken cancellationToken);
}

/// <summary>
/// Vendor-supplied descriptor that lets the generic host UI render provider-appropriate labels without
/// hard-coding any vendor name. All nouns are display-only.
/// </summary>
public sealed record ProviderDescriptor(
    string ProviderId,                 // stable slug, e.g. "device"
    string DisplayName,                // UI page/section title, e.g. "Device Workflow"
    string SystemNoun,                 // e.g. "system" / "site"
    string TargetNoun,                 // e.g. "Device server" / "device"
    string ChannelNoun,                // e.g. "camera"
    bool SupportsSync,
    bool SupportsTestConnection,
    bool RequiresCredentials);

/// <summary>
/// The administered system: an id, a display name, and its targets. <see cref="Options"/> are the
/// provider-declared system-level toggles (flipped via <see cref="IExternalSignalAdmin.SetOptionAsync"/>);
/// <see cref="Diagnostics"/> is a live, never-persisted readout of provider activity. Both default to
/// null so a provider that surfaces neither keeps constructing the record positionally.
/// </summary>
public sealed record ExternalSystemInfo(
    string Id,
    string Name,
    IReadOnlyList<ExternalTargetInfo> Targets,
    IReadOnlyList<SystemOption>? Options = null,
    SystemDiagnostics? Diagnostics = null);

/// <summary>
/// A provider-declared system-level boolean toggle rendered generically by the host UI (label +
/// optional help text, current value). The <see cref="Key"/> is the stable slug passed back to
/// <see cref="IExternalSignalAdmin.SetOptionAsync"/>. Vendor-neutral: the host never interprets the key.
/// </summary>
public sealed record SystemOption(
    string Key,
    string Label,
    bool Value,
    string? Description = null);

/// <summary>
/// Live, observed-only provider diagnostics surfaced in the admin UI: a set of headline metrics and a
/// bounded feed of recent activity. Never persisted — resets when the host restarts.
/// </summary>
public sealed record SystemDiagnostics(
    IReadOnlyList<SystemMetric> Metrics,
    IReadOnlyList<SystemActivityEntry> RecentActivity);

/// <summary>One headline metric (e.g. a running counter) as label + preformatted display value.</summary>
public sealed record SystemMetric(
    string Key,
    string Label,
    string Value);

/// <summary>One entry in the recent-activity feed. <see cref="Kind"/> is a stable slug for styling.</summary>
public sealed record SystemActivityEntry(
    DateTimeOffset Timestamp,
    string Kind,
    string Summary,
    string? Detail = null);

/// <summary>
/// One configured target as the UI sees it. Carries the discovered catalog and live status, but never a
/// secret — <see cref="HasCredential"/> only reports whether one is stored.
/// </summary>
public sealed record ExternalTargetInfo(
    string Id,
    string Name,
    string Host,
    int Port,
    string? User,
    bool HasCredential,
    IReadOnlyList<CatalogChannel> Channels,
    IReadOnlyList<CatalogEntry> Events,
    IReadOnlyList<CatalogEntry> Actions,
    TargetStatus Status,
    // Provider-specific per-target behaviour flag: drop this device's own reflected outbound signals
    // (self-echo). Defaults on. Providers that have no such notion can ignore it.
    bool SuppressSelfEcho = true);

/// <summary>
/// An inbound create/update for a target. <see cref="Id"/> null/empty creates; otherwise updates. A null
/// <see cref="Password"/> means "leave the stored secret unchanged"; set <see cref="ClearPassword"/> to
/// remove it. Secrets travel one way only — they are never echoed back in an <see cref="ExternalTargetInfo"/>.
/// </summary>
public sealed record ExternalTargetEdit(
    string? Id,
    string Name,
    string Host,
    int Port,
    string? User = null,
    string? Password = null,
    bool ClearPassword = false,
    // null = leave unchanged (existing value, or provider default for a new target).
    bool? SuppressSelfEcho = null);
