using System;
using System.Collections.Generic;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Backup;

// ─────────────────────────────────────────────────────────────────────────────
// Backup data model — the typed shape of a full-instance snapshot. Distinct from
// the .kgbundle model (curated, signed, secret-free distribution): a backup is a
// complete, secrets-INCLUDED, restore-in-place snapshot for disaster recovery and
// migration.
//
// The credential-key problem drives the secret handling here: credential and
// notification-channel ciphertext in the DB is encrypted under the host's at-rest
// key (config/env, NOT in the DB). A raw copy is undecryptable on a host with a
// different key, so the backup carries those secrets in PLAINTEXT inside the
// passphrase-encrypted archive; restore (Phase 2) re-encrypts them under the
// target host's key. This is why the archive itself must be passphrase-encrypted.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Well-known constants for the backup format.</summary>
public static class BackupFormat
{
    /// <summary>On-disk format/schema version. Restore refuses archives it cannot read (a future, higher version).</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Engine version that produced the backup. Recorded for the restore-preview and compatibility checks.</summary>
    public const string CurrentEngineVersion = "1.0.0";

    /// <summary>Suggested file extension for a backup archive.</summary>
    public const string FileExtension = ".kgbak";
}

/// <summary>
/// Self-describing header of a backup. Lives at <c>backup.json</c> inside the (encrypted) archive and is
/// the only thing <c>inspect</c> needs to read — it carries no secrets, just provenance and per-aggregate
/// counts for the restore preview.
/// </summary>
public sealed record BackupManifest(
    int FormatVersion,
    string EngineVersion,
    string CreatedAtUtc,
    string DatabaseProvider,
    bool IncludesRunHistory,
    IReadOnlyDictionary<string, int> Counts,
    string KeySource = "Passphrase");

/// <summary>A credential carried in the backup with its value DECRYPTED — re-encrypted under the target key at restore.</summary>
public sealed record CredentialBackup(
    string Id,
    string Name,
    string Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A notification channel carried in the backup with its config JSON DECRYPTED — re-encrypted at restore.</summary>
public sealed record NotificationChannelBackup(
    string Id,
    string Name,
    NotificationChannelType Type,
    string Config,
    bool IsDefaultFailureAlert,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>An OpenAPI spec carried in the backup. A flat shape (no EF back-reference) so it serializes without a cycle.</summary>
public sealed record OpenApiSpecBackup(
    string Id,
    string Title,
    string ApiVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OpenApiSpecVersionBackup> Versions);

/// <summary>A single version of an OpenAPI spec, flattened (no <c>Spec</c> navigation) for cycle-free serialization.</summary>
public sealed record OpenApiSpecVersionBackup(
    Guid RowId,
    string SpecId,
    int VersionNumber,
    string OriginalFormat,
    string ParsedSpecJson,
    DateTimeOffset ImportedAtUtc);
