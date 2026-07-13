using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Persistence.OpenApi;
using Knotarium.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Knotarium.Features.Backup;

/// <summary>The bytes of a produced backup plus its manifest and a suggested download file name.</summary>
public sealed record BackupResult(byte[] Bytes, BackupManifest Manifest, string FileName);

/// <summary>Outcome of a restore: where the safety backup landed, the restored manifest, and per-aggregate counts.</summary>
public sealed record RestoreReport(
    string PreRestoreBackupPath,
    BackupManifest Manifest,
    IReadOnlyDictionary<string, int> Restored);

/// <summary>Why a restore was refused before it touched any state. Maps to a precondition (412) or validation (422) response.</summary>
public enum RestoreBlockReason
{
    /// <summary>The runtime is still armed — automatic triggers could fire mid-restore. Disarm first. (412)</summary>
    RuntimeArmed,

    /// <summary>The caller did not pass <c>confirm: true</c> for this destructive, full-replace operation. (422)</summary>
    NotConfirmed,
}

/// <summary>Raised when a restore is refused by a safety rail before any state is modified.</summary>
public sealed class BackupRestoreBlockedException(RestoreBlockReason reason, string message) : InvalidOperationException(message)
{
    public RestoreBlockReason Reason { get; } = reason;
}

/// <summary>Raised when a backup's format version cannot be restored by this engine. Carries the manifest for the preview. (409)</summary>
public sealed class BackupIncompatibleException(string message, BackupManifest manifest) : InvalidOperationException(message)
{
    public BackupManifest Manifest { get; } = manifest;
}

/// <summary>
/// Produces a full-instance, passphrase-encrypted snapshot. Reads every aggregate via
/// <see cref="AppDbContext"/> and the file-store drafts/groups, decrypts secrets so they can be
/// re-encrypted under the target host's key at restore, and packs it all into one <c>.kgbak</c>.
/// </summary>
public sealed class BackupService
{
    // data/<aggregate>.json document names. Stable identifiers — the restore path keys off these.
    public const string CredentialsEntry = "credentials.json";
    public const string WorkflowDefinitionsEntry = "workflow-definitions.json";
    public const string WorkflowVersionsEntry = "workflow-versions.json";
    public const string ActiveWorkflowVersionsEntry = "active-workflow-versions.json";
    public const string WorkflowVersionActivationsEntry = "workflow-version-activations.json";
    public const string NodePackagesEntry = "node-packages.json";
    public const string SchedulesEntry = "schedules.json";
    public const string PollingTriggersEntry = "polling-triggers.json";
    public const string NotificationChannelsEntry = "notification-channels.json";
    public const string AppSettingsEntry = "app-settings.json";
    public const string ServerConfigsEntry = "server-configs.json";
    public const string OpenApiSpecsEntry = "openapi-specs.json";

    // Filename prefix for the auto pre-restore safety-net backups written to the temp directory.
    private const string PreRestorePrefix = "knotarium-pre-restore-";

    // How long a pre-restore backup is kept before the next restore prunes it.
    private static readonly TimeSpan PreRestoreRetention = TimeSpan.FromDays(7);

    private readonly AppDbContext _db;
    private readonly ICredentialCipher _cipher;
    private readonly FileWorkflowStore _fileStore;
    private readonly TimeProvider _timeProvider;
    private readonly IRuntimeArmingState _armingState;
    private readonly IConfiguration _configuration;
    private readonly CredentialKeyProvisioning? _keyProvisioning;

    public BackupService(
        AppDbContext db,
        ICredentialCipher cipher,
        FileWorkflowStore fileStore,
        TimeProvider timeProvider,
        IRuntimeArmingState armingState,
        IConfiguration configuration,
        CredentialKeyProvisioning? keyProvisioning = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _armingState = armingState ?? throw new ArgumentNullException(nameof(armingState));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _keyProvisioning = keyProvisioning;
    }

    /// <summary>
    /// Best-effort removal of pre-restore safety-net backups older than <see cref="PreRestoreRetention"/> from
    /// the temp directory. Each restore writes one and previously never deleted it, so they accumulated
    /// indefinitely; recent ones are retained so a just-completed restore can still be reversed.
    /// </summary>
    private void CleanupOldPreRestoreBackups()
    {
        try
        {
            var cutoff = _timeProvider.GetUtcNow() - PreRestoreRetention;
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), PreRestorePrefix + "*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // A single locked/partially-removed file must not abort the sweep or the restore.
                }
            }
        }
        catch
        {
            // Temp enumeration is best-effort; never let cleanup block a restore.
        }
    }

    /// <summary>
    /// Builds a <b>passphrase-encrypted</b> backup of the current instance state (portable across hosts).
    /// Run history is excluded by default; <paramref name="includeRunHistory"/> is reserved (recorded in the
    /// manifest, not yet carried).
    /// </summary>
    public Task<BackupResult> CreateAsync(
        string passphrase,
        bool includeRunHistory = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new ArgumentException("A passphrase is required to create a backup.", nameof(passphrase));
        }

        return CreateAsync(BackupSecret.Passphrase(passphrase), includeRunHistory, cancellationToken);
    }

    /// <summary>
    /// Builds a backup encrypted with <b>this host's credential key</b> — no passphrase, but only restorable
    /// on a host whose credential key is unchanged. For automatic/local snapshots; not for migration.
    /// </summary>
    public Task<BackupResult> CreateWithServerKeyAsync(
        bool includeRunHistory = false,
        CancellationToken cancellationToken = default) =>
        CreateAsync(BackupSecret.ServerKey(ResolveServerKey()), includeRunHistory, cancellationToken);

    private async Task<BackupResult> CreateAsync(
        BackupSecret secret,
        bool includeRunHistory,
        CancellationToken cancellationToken)
    {
        var keySource = secret.Source.ToString();

        // --- Database aggregates (read-only snapshot) ---
        var workflowDefinitions = await _db.WorkflowDefinitions.AsNoTracking().ToListAsync(cancellationToken);
        var workflowVersions = await _db.WorkflowVersions.AsNoTracking().ToListAsync(cancellationToken);
        var activeVersions = await _db.ActiveWorkflowVersions.AsNoTracking().ToListAsync(cancellationToken);
        var activations = await _db.WorkflowVersionActivations.AsNoTracking().ToListAsync(cancellationToken);
        var nodePackages = await _db.NodePackages.AsNoTracking().Include(p => p.Versions).ToListAsync(cancellationToken);
        var schedules = await _db.Schedules.AsNoTracking().ToListAsync(cancellationToken);
        var pollingTriggers = await _db.PollingTriggers.AsNoTracking().ToListAsync(cancellationToken);
        var appSettings = await _db.AppSettings.AsNoTracking().ToListAsync(cancellationToken);
        var serverConfigs = await _db.ServerConfigs.AsNoTracking().ToListAsync(cancellationToken);
        var openApiSpecs = await _db.OpenApiSpecs.AsNoTracking().Include(s => s.Versions).ToListAsync(cancellationToken);

        // --- Secret-bearing aggregates: decrypt so restore can re-encrypt under the target key ---
        var credentials = (await _db.Credentials.AsNoTracking().ToListAsync(cancellationToken))
            .Select(c => new CredentialBackup(
                c.Id, c.Name, _cipher.Decrypt(c.EncryptedValue), c.CreatedAt, c.UpdatedAt))
            .ToList();

        var notificationChannels = (await _db.NotificationChannels.AsNoTracking().ToListAsync(cancellationToken))
            .Select(c => new NotificationChannelBackup(
                c.Id, c.Name, c.Type, _cipher.Decrypt(c.EncryptedConfig), c.IsDefaultFailureAlert, c.CreatedAt, c.UpdatedAt))
            .ToList();

        var openApiSpecBackups = openApiSpecs
            .Select(s => new OpenApiSpecBackup(
                s.Id, s.Title, s.ApiVersion, s.CreatedAt,
                s.Versions
                    .Select(v => new OpenApiSpecVersionBackup(
                        v.RowId, v.SpecId, v.VersionNumber, v.OriginalFormat, v.ParsedSpecJson, v.ImportedAtUtc))
                    .ToList()))
            .ToList();

        // --- File-store state: editable drafts + workflow groups ---
        var drafts = await _fileStore.ListAsync(cancellationToken);
        var (groupContainer, _) = await _fileStore.GetGroupsWithETagAsync(cancellationToken);

        var data = new List<BackupArchiveEntry>
        {
            new(CredentialsEntry, BackupSerializer.Serialize(credentials)),
            new(WorkflowDefinitionsEntry, BackupSerializer.Serialize(workflowDefinitions)),
            new(WorkflowVersionsEntry, BackupSerializer.Serialize(workflowVersions)),
            new(ActiveWorkflowVersionsEntry, BackupSerializer.Serialize(activeVersions)),
            new(WorkflowVersionActivationsEntry, BackupSerializer.Serialize(activations)),
            new(NodePackagesEntry, BackupSerializer.Serialize(nodePackages)),
            new(SchedulesEntry, BackupSerializer.Serialize(schedules)),
            new(PollingTriggersEntry, BackupSerializer.Serialize(pollingTriggers)),
            new(NotificationChannelsEntry, BackupSerializer.Serialize(notificationChannels)),
            new(AppSettingsEntry, BackupSerializer.Serialize(appSettings)),
            new(ServerConfigsEntry, BackupSerializer.Serialize(serverConfigs)),
            new(OpenApiSpecsEntry, BackupSerializer.Serialize(openApiSpecBackups)),
        };

        var workflows = drafts
            .Select(d => new BackupArchiveEntry($"{d.Id.Value}.json", JsonSerializer.Serialize(d, BackupSerializer.Options)))
            .ToList();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [CredentialsEntry] = credentials.Count,
            [WorkflowDefinitionsEntry] = workflowDefinitions.Count,
            [WorkflowVersionsEntry] = workflowVersions.Count,
            [ActiveWorkflowVersionsEntry] = activeVersions.Count,
            [WorkflowVersionActivationsEntry] = activations.Count,
            [NodePackagesEntry] = nodePackages.Count,
            [SchedulesEntry] = schedules.Count,
            [PollingTriggersEntry] = pollingTriggers.Count,
            [NotificationChannelsEntry] = notificationChannels.Count,
            [AppSettingsEntry] = appSettings.Count,
            [ServerConfigsEntry] = serverConfigs.Count,
            [OpenApiSpecsEntry] = openApiSpecBackups.Count,
            ["workflows"] = workflows.Count,
            ["groups"] = groupContainer.Groups.Count,
        };

        var now = _timeProvider.GetUtcNow();
        var manifest = new BackupManifest(
            BackupFormat.CurrentFormatVersion,
            BackupFormat.CurrentEngineVersion,
            now.UtcDateTime.ToString("O"),
            DescribeProvider(_db.Database.ProviderName),
            includeRunHistory,
            counts,
            keySource);

        var archive = new BackupArchive(
            manifest,
            JsonSerializer.Serialize(groupContainer, BackupSerializer.Options),
            data,
            workflows);

        var bytes = BackupArchiveCodec.Write(archive, secret);
        var fileName = $"knotarium-backup-{now.UtcDateTime:yyyyMMdd-HHmmss}{BackupFormat.FileExtension}";
        return new BackupResult(bytes, manifest, fileName);
    }

    /// <summary>
    /// Decrypts and parses the manifest only — no writes. Powers the restore preview. The key source is
    /// auto-detected from the archive header: a passphrase-protected backup needs the passphrase; a
    /// server-key backup decrypts with this host's key (passphrase ignored). A wrong key/passphrase or
    /// corrupt archive surfaces as <see cref="BackupArchiveException"/> (400); an incompatible format as
    /// <see cref="BackupIncompatibleException"/> (409).
    /// </summary>
    public Task<BackupManifest> InspectAsync(byte[] bytes, string? passphrase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var secret = ResolveSecretForRead(bytes, passphrase);
        var archive = BackupArchiveCodec.Read(bytes, secret); // throws BackupArchiveException on bad key/corruption
        EnsureCompatible(archive.Manifest);
        return Task.FromResult(archive.Manifest);
    }

    /// <summary>
    /// Destructively replaces all managed instance state with the backup's contents, in one transaction.
    /// Refuses unless the runtime is disarmed (<see cref="RestoreBlockReason.RuntimeArmed"/>) and the caller
    /// passed <paramref name="confirm"/> (<see cref="RestoreBlockReason.NotConfirmed"/>). Before touching
    /// anything it writes an auto pre-restore backup (encrypted with the same passphrase) to a temp path so a
    /// botched restore is reversible. Credentials and notification-channel configs are re-encrypted under the
    /// CURRENT host key. Run history is left untouched (it is never carried in a backup).
    /// </summary>
    public async Task<RestoreReport> RestoreAsync(
        byte[] bytes,
        string? passphrase,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // Safety rails, checked before any decryption or writes.
        if (_armingState.IsArmed)
        {
            throw new BackupRestoreBlockedException(
                RestoreBlockReason.RuntimeArmed,
                "Restore is blocked while the runtime is armed. Disarm the runtime and retry.");
        }

        if (!confirm)
        {
            throw new BackupRestoreBlockedException(
                RestoreBlockReason.NotConfirmed,
                "Restore replaces ALL current state and requires explicit confirmation (confirm: true).");
        }

        var secret = ResolveSecretForRead(bytes, passphrase); // auto-detects passphrase vs server key
        var archive = BackupArchiveCodec.Read(bytes, secret); // 400 on bad key/corruption
        EnsureCompatible(archive.Manifest); // 409 on format mismatch

        // Prune stale pre-restore backups from previous restores so they don't accumulate in temp forever
        // (each restore used to leak one). Recent ones are kept as a short-lived safety net.
        CleanupOldPreRestoreBackups();

        // Auto pre-restore backup of the CURRENT state, in the SAME mode as the incoming archive, so the
        // operation is reversible.
        var preRestore = await CreateAsync(secret, includeRunHistory: false, cancellationToken);
        var preRestorePath = Path.Combine(
            Path.GetTempPath(),
            $"{PreRestorePrefix}{Guid.NewGuid():N}-{preRestore.FileName}");
        await File.WriteAllBytesAsync(preRestorePath, preRestore.Bytes, cancellationToken);

        // Decode the archive's aggregates up front so any malformed document fails before we delete anything.
        var workflowDefinitions = ReadData<WorkflowDefinition>(archive, WorkflowDefinitionsEntry);
        var workflowVersions = ReadData<WorkflowVersion>(archive, WorkflowVersionsEntry);
        var activeVersions = ReadData<ActiveWorkflowVersion>(archive, ActiveWorkflowVersionsEntry);
        var activations = ReadData<WorkflowVersionActivation>(archive, WorkflowVersionActivationsEntry);
        var nodePackages = ReadData<NodePackage>(archive, NodePackagesEntry);
        var schedules = ReadData<Schedule>(archive, SchedulesEntry);
        var pollingTriggers = ReadData<PollingTrigger>(archive, PollingTriggersEntry);
        var appSettings = ReadData<AppSetting>(archive, AppSettingsEntry);
        var serverConfigs = ReadData<ServerConfigEntity>(archive, ServerConfigsEntry);
        var credentials = ReadData<CredentialBackup>(archive, CredentialsEntry);
        var channels = ReadData<NotificationChannelBackup>(archive, NotificationChannelsEntry);
        var openApiSpecs = ReadData<OpenApiSpecBackup>(archive, OpenApiSpecsEntry);

        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Clear in child→parent order. (On a fresh EnsureCreated DB there are no FKs between these; on a
            // migrated SQLite DB the raw-SQL tables declare ON DELETE CASCADE, so this order is safe for both.)
            await _db.WorkflowVersionActivations.ExecuteDeleteAsync(cancellationToken);
            await _db.ActiveWorkflowVersions.ExecuteDeleteAsync(cancellationToken);
            await _db.WorkflowVersions.ExecuteDeleteAsync(cancellationToken);
            await _db.Schedules.ExecuteDeleteAsync(cancellationToken);
            await _db.PollingTriggers.ExecuteDeleteAsync(cancellationToken);
            await _db.WorkflowDefinitions.ExecuteDeleteAsync(cancellationToken);
            await _db.NodePackageVersions.ExecuteDeleteAsync(cancellationToken);
            await _db.NodePackages.ExecuteDeleteAsync(cancellationToken);
            await _db.NotificationChannels.ExecuteDeleteAsync(cancellationToken);
            await _db.Credentials.ExecuteDeleteAsync(cancellationToken);
            await _db.AppSettings.ExecuteDeleteAsync(cancellationToken);
            await _db.ServerConfigs.ExecuteDeleteAsync(cancellationToken);
            await _db.OpenApiSpecVersions.ExecuteDeleteAsync(cancellationToken);
            await _db.OpenApiSpecs.ExecuteDeleteAsync(cancellationToken);

            // Re-insert in parent→child order, saving in stages so unconfigured DB-level FKs are satisfied.
            _db.WorkflowDefinitions.AddRange(workflowDefinitions);
            await _db.SaveChangesAsync(cancellationToken);

            _db.WorkflowVersions.AddRange(workflowVersions);
            await _db.SaveChangesAsync(cancellationToken);

            _db.ActiveWorkflowVersions.AddRange(activeVersions);
            _db.WorkflowVersionActivations.AddRange(activations);
            _db.Schedules.AddRange(schedules);
            _db.PollingTriggers.AddRange(pollingTriggers);
            await _db.SaveChangesAsync(cancellationToken);

            _db.NodePackages.AddRange(nodePackages);
            await _db.SaveChangesAsync(cancellationToken);

            // Secrets: re-encrypt the plaintext from the archive under the CURRENT host key.
            _db.Credentials.AddRange(credentials.Select(c => new Credential
            {
                Id = c.Id,
                Name = c.Name,
                EncryptedValue = _cipher.Encrypt(c.Value),
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
            }));
            _db.NotificationChannels.AddRange(channels.Select(c => new NotificationChannel
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.Type,
                EncryptedConfig = _cipher.Encrypt(c.Config),
                IsDefaultFailureAlert = c.IsDefaultFailureAlert,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
            }));
            _db.AppSettings.AddRange(appSettings);
            _db.ServerConfigs.AddRange(serverConfigs);
            await _db.SaveChangesAsync(cancellationToken);

            _db.OpenApiSpecs.AddRange(openApiSpecs.Select(s => new OpenApiSpecEntity
            {
                Id = s.Id,
                Title = s.Title,
                ApiVersion = s.ApiVersion,
                CreatedAt = s.CreatedAt,
                Versions = s.Versions.Select(v => new OpenApiSpecVersionEntity
                {
                    RowId = v.RowId,
                    SpecId = v.SpecId,
                    VersionNumber = v.VersionNumber,
                    OriginalFormat = v.OriginalFormat,
                    ParsedSpecJson = v.ParsedSpecJson,
                    ImportedAtUtc = v.ImportedAtUtc,
                }).ToList(),
            }));
            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        // File-store is separate, non-transactional state (the same DB-vs-file seam BundleInstallService notes).
        // Done after the DB commit; the pre-restore backup remains the safety net if this half fails.
        await RestoreFileStoreAsync(archive, cancellationToken);

        var restored = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [CredentialsEntry] = credentials.Count,
            [WorkflowDefinitionsEntry] = workflowDefinitions.Count,
            [WorkflowVersionsEntry] = workflowVersions.Count,
            [ActiveWorkflowVersionsEntry] = activeVersions.Count,
            [WorkflowVersionActivationsEntry] = activations.Count,
            [NodePackagesEntry] = nodePackages.Count,
            [SchedulesEntry] = schedules.Count,
            [PollingTriggersEntry] = pollingTriggers.Count,
            [NotificationChannelsEntry] = channels.Count,
            [AppSettingsEntry] = appSettings.Count,
            [ServerConfigsEntry] = serverConfigs.Count,
            [OpenApiSpecsEntry] = openApiSpecs.Count,
            ["workflows"] = archive.Workflows.Count,
        };

        return new RestoreReport(preRestorePath, archive.Manifest, restored);
    }

    private async Task RestoreFileStoreAsync(BackupArchive archive, CancellationToken cancellationToken)
    {
        // Replace drafts wholesale: drop every current draft, then write the archive's.
        foreach (var existing in await _fileStore.ListAsync(cancellationToken))
        {
            await _fileStore.DeleteAsync(existing.Id, cancellationToken);
        }

        foreach (var entry in archive.Workflows)
        {
            var draft = JsonSerializer.Deserialize<WorkflowDefinition>(entry.Content, BackupSerializer.Options);
            if (draft is not null)
            {
                await _fileStore.UpsertAsync(draft, cancellationToken);
            }
        }

        if (archive.Groups is not null)
        {
            var groups = JsonSerializer.Deserialize<GroupContainer>(archive.Groups, BackupSerializer.Options);
            if (groups is not null)
            {
                // ifMatch: null skips the optimistic-concurrency check — restore is an authoritative overwrite.
                await _fileStore.SaveGroupsAsync(groups, ifMatch: null, cancellationToken);
            }
        }
    }

    private static IReadOnlyList<T> ReadData<T>(BackupArchive archive, string entryName)
    {
        var entry = archive.Data.FirstOrDefault(e => e.Name == entryName);
        // A missing aggregate document is treated as empty (forward/backward tolerance across format additions).
        return entry is null ? Array.Empty<T>() : BackupSerializer.Deserialize<T>(entry.Content);
    }

    // Auto-detects the key source from the archive's cleartext header, then builds the matching secret:
    // a server-key backup uses this host's key (passphrase irrelevant); a passphrase backup needs the
    // passphrase. A malformed/unrecognized file fails in PeekKeySource as a BackupArchiveException (400).
    private BackupSecret ResolveSecretForRead(byte[] bytes, string? passphrase)
    {
        var source = BackupArchiveCodec.PeekKeySource(bytes);
        if (source == BackupKeySource.ServerKey)
        {
            return BackupSecret.ServerKey(ResolveServerKey());
        }

        if (string.IsNullOrEmpty(passphrase))
        {
            throw new BackupArchiveException("This backup is passphrase-protected — enter the passphrase used when it was created.");
        }

        return BackupSecret.Passphrase(passphrase);
    }

    // The host's credential key, surfaced as a BackupArchiveException (→400) when absent so the server-key
    // path fails with a clear, non-500 message instead of an unhandled config error.
    private byte[] ResolveServerKey()
    {
        try
        {
            return CredentialEncryptionKey.Resolve(_configuration, _keyProvisioning);
        }
        catch (InvalidOperationException ex)
        {
            throw new BackupArchiveException(
                "This server has no credential encryption key configured, so a server-key backup can't be created or restored. " + ex.Message);
        }
    }

    private static void EnsureCompatible(BackupManifest manifest)
    {
        if (manifest.FormatVersion > BackupFormat.CurrentFormatVersion)
        {
            throw new BackupIncompatibleException(
                $"This backup was written in format v{manifest.FormatVersion}, but this engine only supports up to v{BackupFormat.CurrentFormatVersion}. Upgrade Knotarium to restore it.",
                manifest);
        }

        if (manifest.FormatVersion < 1)
        {
            throw new BackupIncompatibleException(
                $"This backup declares an invalid format version (v{manifest.FormatVersion}).",
                manifest);
        }
    }

    private static string DescribeProvider(string? providerName)
    {
        if (string.IsNullOrEmpty(providerName))
        {
            return "Unknown";
        }

        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return "SQLite";
        }

        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            || providerName.Contains("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            return "Postgres";
        }

        return providerName;
    }
}
