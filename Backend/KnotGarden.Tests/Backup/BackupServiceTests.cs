using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KnotGarden.Api.Services;
using KnotGarden.Features.Backup;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnotGarden.Tests.Backup;

public sealed class BackupServiceTests : IDisposable
{
    private const string Passphrase = "a-very-long-test-passphrase";

    // Two distinct 32-byte credential keys, base64 — model two different hosts for the server-key path.
    private static readonly string ServerKeyA = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string ServerKeyB = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());

    private static IConfiguration ConfigWith(string? credentialKeyBase64)
    {
        var dict = new System.Collections.Generic.Dictionary<string, string?>();
        if (credentialKeyBase64 is not null)
        {
            dict["Security:Credentials:EncryptionKeyBase64"] = credentialKeyBase64;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _tempFolder;
    private readonly FileWorkflowStore _fileStore;
    private readonly ReversibleFakeCipher _cipher = new();

    public BackupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        _tempFolder = Path.Combine(Path.GetTempPath(), "knotgarden_backup_tests_" + Guid.NewGuid().ToString("N"));
        _fileStore = new FileWorkflowStore(_tempFolder, NullLogger<FileWorkflowStore>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_tempFolder))
        {
            try { Directory.Delete(_tempFolder, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // A reversible stand-in for the AES cipher. The token models a host's at-rest key: ciphertext is
    // "{token}::{plaintext}", so a backup made under one token re-encrypted under another is observable.
    private sealed class ReversibleFakeCipher : ICredentialCipher
    {
        private readonly string _token;
        public ReversibleFakeCipher(string token = "A") => _token = token;
        public string Token => _token;
        public string Encrypt(string plainText) => $"{_token}::{plainText}";
        public string Decrypt(string cipherText)
        {
            var idx = cipherText.IndexOf("::", StringComparison.Ordinal);
            return idx >= 0 ? cipherText[(idx + 2)..] : cipherText;
        }
    }

    private AppDbContext NewContext() => new(_options);

    private async Task SeedAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();

        db.WorkflowDefinitions.Add(new WorkflowDefinition(
            new WorkflowDefinitionId("wf1"), "My Workflow",
            Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>()));

        db.WorkflowVersions.Add(new WorkflowVersion(
            WorkflowVersionId.New(), new WorkflowDefinitionId("wf1"), 1,
            Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>(), DateTimeOffset.UnixEpoch));

        db.Schedules.Add(new Schedule
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = new WorkflowDefinitionId("wf1"),
            CronExpression = "* * * * *",
            TimeZoneId = "UTC",
            NextFireAtUtc = DateTimeOffset.UnixEpoch,
            IsActive = true,
        });

        db.Credentials.Add(new Credential
        {
            Id = "cred1",
            Name = "API Key",
            EncryptedValue = _cipher.Encrypt("super-secret"),
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        db.NotificationChannels.Add(new NotificationChannel
        {
            Id = "ch1",
            Name = "Ops",
            Type = NotificationChannelType.Webhook,
            EncryptedConfig = _cipher.Encrypt("{\"url\":\"https://hook.example\"}"),
            IsDefaultFailureAlert = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.DefaultErrorWorkflowId, Value = "wf1" });

        await db.SaveChangesAsync();

        // A file-store draft (separate state from the DB header).
        await _fileStore.UpsertAsync(new WorkflowDefinition(
            new WorkflowDefinitionId("draft1"), "Draft Workflow",
            Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>()));
    }

    private BackupService NewService(ICredentialCipher? cipher = null, bool armed = false, string? serverKey = null) =>
        new(NewContext(), cipher ?? _cipher, _fileStore, TimeProvider.System, new RuntimeArmingState(armed),
            ConfigWith(serverKey ?? ServerKeyA));

    private async Task<BackupService> CreateServiceAsync()
    {
        await SeedAsync();
        return NewService();
    }

    [Fact]
    public async Task CreateAsync_ProducesManifest_WithMatchingCounts()
    {
        var service = await CreateServiceAsync();

        var result = await service.CreateAsync(Passphrase);

        Assert.Equal(BackupFormat.CurrentFormatVersion, result.Manifest.FormatVersion);
        Assert.Equal("SQLite", result.Manifest.DatabaseProvider);
        Assert.False(result.Manifest.IncludesRunHistory);
        Assert.Equal(1, result.Manifest.Counts[BackupService.CredentialsEntry]);
        Assert.Equal(1, result.Manifest.Counts[BackupService.WorkflowDefinitionsEntry]);
        Assert.Equal(1, result.Manifest.Counts[BackupService.WorkflowVersionsEntry]);
        Assert.Equal(1, result.Manifest.Counts[BackupService.SchedulesEntry]);
        Assert.Equal(1, result.Manifest.Counts[BackupService.NotificationChannelsEntry]);
        Assert.Equal(1, result.Manifest.Counts[BackupService.AppSettingsEntry]);
        Assert.Equal(1, result.Manifest.Counts["workflows"]);
        Assert.EndsWith(".kgbak", result.FileName);
    }

    [Fact]
    public async Task CreateAsync_RoundTrips_AndDecryptsSecretsIntoArchive()
    {
        var service = await CreateServiceAsync();

        var result = await service.CreateAsync(Passphrase);
        var archive = BackupArchiveCodec.Read(result.Bytes, Passphrase);

        // Credentials are carried DECRYPTED so a different host can re-encrypt them at restore.
        var credsEntry = archive.Data.Single(e => e.Name == BackupService.CredentialsEntry);
        var creds = BackupSerializer.Deserialize<CredentialBackup>(credsEntry.Content);
        var cred = Assert.Single(creds);
        Assert.Equal("cred1", cred.Id);
        Assert.Equal("super-secret", cred.Value);

        // Notification-channel config is decrypted too.
        var channelsEntry = archive.Data.Single(e => e.Name == BackupService.NotificationChannelsEntry);
        var channels = BackupSerializer.Deserialize<NotificationChannelBackup>(channelsEntry.Content);
        var channel = Assert.Single(channels);
        Assert.Equal("{\"url\":\"https://hook.example\"}", channel.Config);

        // The file-store draft is carried under workflows/.
        Assert.Contains(archive.Workflows, w => w.Name == "draft1.json");

        // Groups document is present (empty container by default).
        Assert.NotNull(archive.Groups);
    }

    [Fact]
    public async Task CreateAsync_ThenReadWithWrongPassphrase_FailsCleanly()
    {
        var service = await CreateServiceAsync();

        var result = await service.CreateAsync(Passphrase);

        Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Read(result.Bytes, "not-the-passphrase"));
    }

    [Fact]
    public async Task CreateAsync_EmptyPassphrase_Throws()
    {
        var service = await CreateServiceAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(""));
    }

    // ── Phase 2: inspect & restore ──────────────────────────────────────────

    [Fact]
    public async Task InspectAsync_ReturnsManifest_WithoutWriting()
    {
        var service = await CreateServiceAsync();
        var backup = await service.CreateAsync(Passphrase);

        var manifest = await NewService().InspectAsync(backup.Bytes, Passphrase);

        Assert.Equal(BackupFormat.CurrentFormatVersion, manifest.FormatVersion);
        Assert.Equal(1, manifest.Counts[BackupService.CredentialsEntry]);

        // No writes: the credential is still present and unchanged.
        await using var db = NewContext();
        Assert.Equal(1, await db.Credentials.CountAsync());
    }

    [Fact]
    public async Task InspectAsync_WrongPassphrase_Throws()
    {
        var service = await CreateServiceAsync();
        var backup = await service.CreateAsync(Passphrase);

        await Assert.ThrowsAsync<BackupArchiveException>(() => NewService().InspectAsync(backup.Bytes, "wrong"));
    }

    [Fact]
    public async Task InspectAsync_IncompatibleFormat_Throws()
    {
        var bytes = BuildArchiveWithFormatVersion(999);

        await Assert.ThrowsAsync<BackupIncompatibleException>(() => NewService().InspectAsync(bytes, Passphrase));
    }

    [Fact]
    public async Task RestoreAsync_ReplacesState_AndReEncryptsUnderCurrentKey()
    {
        // Backup the baseline under host key "A".
        await SeedAsync();
        var backup = await NewService(new ReversibleFakeCipher("A")).CreateAsync(Passphrase);

        // Diverge the live state: add a workflow definition that is NOT in the backup.
        await using (var db = NewContext())
        {
            db.WorkflowDefinitions.Add(new WorkflowDefinition(
                new WorkflowDefinitionId("junk1"), "Junk",
                Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>()));
            await db.SaveChangesAsync();
        }

        // Restore onto a host with a DIFFERENT key "B".
        var cipherB = new ReversibleFakeCipher("B");
        var report = await NewService(cipherB).RestoreAsync(backup.Bytes, Passphrase, confirm: true);

        Assert.Equal(1, report.Restored[BackupService.WorkflowDefinitionsEntry]);
        Assert.True(File.Exists(report.PreRestoreBackupPath));

        await using var verify = NewContext();
        // The out-of-band "junk1" is gone; the backup's "wf1" remains — state was replaced wholesale.
        Assert.False(await verify.WorkflowDefinitions.AnyAsync(w => w.Id == new WorkflowDefinitionId("junk1")));
        Assert.True(await verify.WorkflowDefinitions.AnyAsync(w => w.Id == new WorkflowDefinitionId("wf1")));

        // The credential was re-encrypted under key "B" and still decrypts to the original plaintext.
        var cred = await verify.Credentials.SingleAsync();
        Assert.StartsWith("B::", cred.EncryptedValue);
        Assert.Equal("super-secret", cipherB.Decrypt(cred.EncryptedValue));
    }

    [Fact]
    public async Task RestoreAsync_RuntimeArmed_Throws412Reason()
    {
        await SeedAsync();
        var backup = await NewService().CreateAsync(Passphrase);

        var ex = await Assert.ThrowsAsync<BackupRestoreBlockedException>(
            () => NewService(armed: true).RestoreAsync(backup.Bytes, Passphrase, confirm: true));
        Assert.Equal(RestoreBlockReason.RuntimeArmed, ex.Reason);
    }

    [Fact]
    public async Task RestoreAsync_NotConfirmed_ThrowsValidationReason()
    {
        await SeedAsync();
        var backup = await NewService().CreateAsync(Passphrase);

        var ex = await Assert.ThrowsAsync<BackupRestoreBlockedException>(
            () => NewService().RestoreAsync(backup.Bytes, Passphrase, confirm: false));
        Assert.Equal(RestoreBlockReason.NotConfirmed, ex.Reason);
    }

    [Fact]
    public async Task RestoreAsync_IncompatibleFormat_Throws()
    {
        await SeedAsync();
        var bytes = BuildArchiveWithFormatVersion(999);

        await Assert.ThrowsAsync<BackupIncompatibleException>(
            () => NewService().RestoreAsync(bytes, Passphrase, confirm: true));
    }

    [Fact]
    public async Task RestoreAsync_MidFailure_RollsBackTransaction()
    {
        await SeedAsync();

        // A malformed-but-decryptable archive: two workflow versions sharing (definition, versionNumber),
        // which violates the unique index on insert and aborts the transaction mid-restore.
        var bytes = BuildBrokenArchive();

        await Assert.ThrowsAnyAsync<Exception>(
            () => NewService().RestoreAsync(bytes, Passphrase, confirm: true));

        // The transaction rolled back: the original baseline is fully intact.
        await using var verify = NewContext();
        Assert.True(await verify.WorkflowDefinitions.AnyAsync(w => w.Id == new WorkflowDefinitionId("wf1")));
        Assert.Equal(1, await verify.Credentials.CountAsync());
        Assert.Equal(1, await verify.WorkflowVersions.CountAsync());
    }

    // ── Server-key (passwordless, host-bound) mode ──────────────────────────

    [Fact]
    public async Task CreateWithServerKeyAsync_RoundTrips_WithoutAnyPassphrase()
    {
        await SeedAsync();

        var backup = await NewService(serverKey: ServerKeyA).CreateWithServerKeyAsync();

        Assert.Equal("ServerKey", backup.Manifest.KeySource);
        Assert.Equal(BackupKeySource.ServerKey, BackupArchiveCodec.PeekKeySource(backup.Bytes));

        // Inspect needs no passphrase — the host's key opens it.
        var manifest = await NewService(serverKey: ServerKeyA).InspectAsync(backup.Bytes, passphrase: null);
        Assert.Equal(1, manifest.Counts[BackupService.CredentialsEntry]);
    }

    [Fact]
    public async Task ServerKeyBackup_OnAHostWithADifferentKey_FailsToDecrypt()
    {
        await SeedAsync();
        var backup = await NewService(serverKey: ServerKeyA).CreateWithServerKeyAsync();

        // A different host (key B) cannot open a server-key backup made under key A.
        await Assert.ThrowsAsync<BackupArchiveException>(
            () => NewService(serverKey: ServerKeyB).InspectAsync(backup.Bytes, passphrase: null));
    }

    [Fact]
    public async Task PassphraseBackup_InspectedWithoutPassphrase_IsRejected()
    {
        await SeedAsync();
        var backup = await NewService().CreateAsync(Passphrase);

        // Auto-detect sees a passphrase archive; with no passphrase supplied it must ask for one.
        await Assert.ThrowsAsync<BackupArchiveException>(
            () => NewService().InspectAsync(backup.Bytes, passphrase: null));
    }

    [Fact]
    public async Task CreateWithServerKeyAsync_NoCredentialKeyConfigured_Throws()
    {
        await SeedAsync();
        var service = new BackupService(
            NewContext(), _cipher, _fileStore, TimeProvider.System, new RuntimeArmingState(false), ConfigWith(null));

        await Assert.ThrowsAsync<BackupArchiveException>(() => service.CreateWithServerKeyAsync());
    }

    [Fact]
    public async Task RestoreAsync_ServerKeyBackup_RoundTrips_WithoutPassphrase()
    {
        await SeedAsync();
        var backup = await NewService(serverKey: ServerKeyA).CreateWithServerKeyAsync();

        await using (var db = NewContext())
        {
            db.WorkflowDefinitions.Add(new WorkflowDefinition(
                new WorkflowDefinitionId("junk1"), "Junk", Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>()));
            await db.SaveChangesAsync();
        }

        var report = await NewService(serverKey: ServerKeyA).RestoreAsync(backup.Bytes, passphrase: null, confirm: true);

        Assert.True(File.Exists(report.PreRestoreBackupPath));
        await using var verify = NewContext();
        Assert.False(await verify.WorkflowDefinitions.AnyAsync(w => w.Id == new WorkflowDefinitionId("junk1")));
        Assert.True(await verify.WorkflowDefinitions.AnyAsync(w => w.Id == new WorkflowDefinitionId("wf1")));
    }

    private static byte[] BuildArchiveWithFormatVersion(int formatVersion)
    {
        var manifest = new BackupManifest(
            formatVersion, "1.0.0", "1970-01-01T00:00:00.0000000Z", "SQLite", false,
            new System.Collections.Generic.Dictionary<string, int>());
        var archive = new BackupArchive(manifest, Groups: null,
            Data: Array.Empty<BackupArchiveEntry>(), Workflows: Array.Empty<BackupArchiveEntry>());
        return BackupArchiveCodec.Write(archive, Passphrase);
    }

    private static byte[] BuildBrokenArchive()
    {
        var defId = new WorkflowDefinitionId("wfX");
        var defs = new[]
        {
            new WorkflowDefinition(defId, "X", Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>()),
        };
        // Same (WorkflowDefinitionId, VersionNumber) twice → unique-index violation on the second insert.
        var versions = new[]
        {
            new WorkflowVersion(WorkflowVersionId.New(), defId, 1, Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>(), DateTimeOffset.UnixEpoch),
            new WorkflowVersion(WorkflowVersionId.New(), defId, 1, Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>(), DateTimeOffset.UnixEpoch),
        };

        var manifest = new BackupManifest(
            BackupFormat.CurrentFormatVersion, "1.0.0", "1970-01-01T00:00:00.0000000Z", "SQLite", false,
            new System.Collections.Generic.Dictionary<string, int>());
        var data = new[]
        {
            new BackupArchiveEntry(BackupService.WorkflowDefinitionsEntry, BackupSerializer.Serialize(defs)),
            new BackupArchiveEntry(BackupService.WorkflowVersionsEntry, BackupSerializer.Serialize(versions)),
        };
        var archive = new BackupArchive(manifest, Groups: null, Data: data, Workflows: Array.Empty<BackupArchiveEntry>());
        return BackupArchiveCodec.Write(archive, Passphrase);
    }
}
