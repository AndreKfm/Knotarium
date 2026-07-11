using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Knotarium.Features.Backup;
using Xunit;

namespace Knotarium.Tests.Backup;

public class BackupArchiveCodecTests
{
    private const string Passphrase = "correct horse battery staple";

    private static BackupManifest SampleManifest() => new(
        FormatVersion: BackupFormat.CurrentFormatVersion,
        EngineVersion: BackupFormat.CurrentEngineVersion,
        CreatedAtUtc: "1970-01-01T00:00:00.0000000Z",
        DatabaseProvider: "SQLite",
        IncludesRunHistory: false,
        Counts: new Dictionary<string, int> { ["credentials.json"] = 2, ["workflows"] = 1 });

    private static BackupArchive SampleArchive() => new(
        SampleManifest(),
        Groups: "{\"version\":1,\"groups\":[]}",
        Data: new[]
        {
            new BackupArchiveEntry("credentials.json", "[{\"id\":\"c1\"}]"),
            new BackupArchiveEntry("schedules.json", "[]"),
        },
        Workflows: new[] { new BackupArchiveEntry("wf-1.json", "{\"id\":\"wf-1\"}") });

    [Fact]
    public void WriteThenRead_RoundTripsEveryField()
    {
        var original = SampleArchive();

        var restored = BackupArchiveCodec.Read(BackupArchiveCodec.Write(original, Passphrase), Passphrase);

        // Manifest equality is checked through its serialized form: record .Equals compares the dictionary
        // reference, so a fresh round-tripped instance never compares equal despite identical contents.
        Assert.Equal(
            BackupSerializer.SerializeManifest(original.Manifest),
            BackupSerializer.SerializeManifest(restored.Manifest));
        Assert.Equal(original.Groups, restored.Groups);
        Assert.Equal(original.Data, restored.Data);
        Assert.Equal(original.Workflows, restored.Workflows);
    }

    [Fact]
    public void Write_ProducesEncryptedBytes_NotPlaintextZip()
    {
        var bytes = BackupArchiveCodec.Write(SampleArchive(), Passphrase);

        // Encrypted envelope, not a raw zip: it starts with the "KGBK" magic, never the "PK" zip signature.
        Assert.Equal((byte)'K', bytes[0]);
        Assert.Equal((byte)'G', bytes[1]);
        Assert.NotEqual((byte)'P', bytes[0]);
    }

    [Fact]
    public void Read_WrongPassphrase_ThrowsCleanly()
    {
        var bytes = BackupArchiveCodec.Write(SampleArchive(), Passphrase);

        var ex = Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Read(bytes, "wrong passphrase"));
        Assert.Contains("passphrase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_TamperedCiphertext_Throws()
    {
        var bytes = BackupArchiveCodec.Write(SampleArchive(), Passphrase);
        bytes[^1] ^= 0xFF; // flip a byte in the ciphertext tail

        Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Read(bytes, Passphrase));
    }

    [Fact]
    public void Read_TamperedHeader_Throws()
    {
        var bytes = BackupArchiveCodec.Write(SampleArchive(), Passphrase);
        // The iteration count lives in the authenticated header (bytes 6..9); perturbing it must fail auth.
        bytes[6] ^= 0x01;

        Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Read(bytes, Passphrase));
    }

    [Fact]
    public void Read_NotAKgbak_Throws()
    {
        Assert.Throws<BackupArchiveException>(
            () => BackupArchiveCodec.Read(Encoding.UTF8.GetBytes("this is not a backup archive at all xxxxxxxxxxx"), Passphrase));
    }

    [Fact]
    public void Read_Truncated_Throws()
    {
        Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Read(new byte[] { 1, 2, 3 }, Passphrase));
    }

    [Fact]
    public void Write_DuplicateDataName_Throws()
    {
        var archive = SampleArchive() with
        {
            Data = new[]
            {
                new BackupArchiveEntry("dup.json", "1"),
                new BackupArchiveEntry("dup.json", "2"),
            }
        };

        Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Write(archive, Passphrase));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("nested/child.json")]
    [InlineData("")]
    public void Write_InvalidLeafName_Throws(string name)
    {
        var archive = SampleArchive() with { Workflows = new[] { new BackupArchiveEntry(name, "x") } };

        Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Write(archive, Passphrase));
    }

    [Fact]
    public void Write_EmptyPassphrase_Throws()
    {
        Assert.Throws<ArgumentException>(() => BackupArchiveCodec.Write(SampleArchive(), ""));
    }

    [Fact]
    public void RoundTrip_WithoutGroups_PreservesNullGroups()
    {
        var archive = SampleArchive() with { Groups = null };

        var restored = BackupArchiveCodec.Read(BackupArchiveCodec.Write(archive, Passphrase), Passphrase);

        Assert.Null(restored.Groups);
    }

    // ── Server-key envelope ──────────────────────────────────────────────────

    private static readonly byte[] ServerKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherServerKey = Enumerable.Range(50, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void ServerKey_WriteThenRead_RoundTrips()
    {
        var original = SampleArchive();
        var secret = BackupSecret.ServerKey(ServerKey);

        var restored = BackupArchiveCodec.Read(BackupArchiveCodec.Write(original, secret), BackupSecret.ServerKey(ServerKey));

        Assert.Equal(original.Data, restored.Data);
        Assert.Equal(original.Workflows, restored.Workflows);
    }

    [Fact]
    public void PeekKeySource_ReportsTheEnvelopeMode()
    {
        Assert.Equal(BackupKeySource.Passphrase, BackupArchiveCodec.PeekKeySource(BackupArchiveCodec.Write(SampleArchive(), Passphrase)));
        Assert.Equal(BackupKeySource.ServerKey, BackupArchiveCodec.PeekKeySource(BackupArchiveCodec.Write(SampleArchive(), BackupSecret.ServerKey(ServerKey))));
    }

    [Fact]
    public void ServerKey_ReadWithDifferentKey_Throws()
    {
        var bytes = BackupArchiveCodec.Write(SampleArchive(), BackupSecret.ServerKey(ServerKey));

        Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Read(bytes, BackupSecret.ServerKey(OtherServerKey)));
    }

    [Fact]
    public void KeySourceMismatch_IsRejectedClearly()
    {
        var serverKeyBytes = BackupArchiveCodec.Write(SampleArchive(), BackupSecret.ServerKey(ServerKey));
        var passphraseBytes = BackupArchiveCodec.Write(SampleArchive(), Passphrase);

        // Passphrase secret against a server-key archive…
        var ex1 = Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Read(serverKeyBytes, Passphrase));
        Assert.Contains("server", ex1.Message, StringComparison.OrdinalIgnoreCase);

        // …and a server key against a passphrase archive.
        var ex2 = Assert.Throws<BackupArchiveException>(() => BackupArchiveCodec.Read(passphraseBytes, BackupSecret.ServerKey(ServerKey)));
        Assert.Contains("passphrase", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }
}
