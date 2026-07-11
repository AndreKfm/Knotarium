using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KnotGarden.Features.Backup;

// ─────────────────────────────────────────────────────────────────────────────
// .kgbak archive codec — turns an in-memory backup (manifest + opaque data and
// workflow documents) into a single passphrase-encrypted file and back.
//
// Two layers:
//   1. An inner zip with a stable layout (mirrors the .kgbundle codec's shape):
//        backup.json          — BackupManifest (provenance + per-aggregate counts)
//        groups.json          — file-store workflow groups (optional)
//        data/<aggregate>     — one JSON document per DB aggregate
//        workflows/<id>.json  — one file-store draft per workflow
//   2. An encrypted envelope wrapping that zip: AES-256-GCM, with the key derived
//      from one of two sources (a cleartext header byte records which):
//        • Passphrase (default) — PBKDF2-HMAC-SHA256 over a user passphrase. Portable:
//          restorable on any host by anyone who knows the passphrase. Required for
//          migration / off-host disaster recovery.
//        • ServerKey — HKDF-SHA256 over THIS host's credential encryption key (config/
//          env, never in the archive). No passphrase to remember, but the backup only
//          decrypts on a host whose credential key is unchanged. For automatic/local
//          snapshots; useless for migration (by design).
//      A random salt+nonce go in the cleartext header, which is also fed as AES-GCM
//      associated data so tampering with the KDF params fails authentication.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>How a <c>.kgbak</c> envelope's symmetric key is derived. Recorded in the cleartext header.</summary>
public enum BackupKeySource : byte
{
    /// <summary>PBKDF2 over a user passphrase. Portable across hosts.</summary>
    Passphrase = 1,

    /// <summary>HKDF over this host's credential encryption key. Restorable only on the creating host.</summary>
    ServerKey = 2,
}

/// <summary>
/// The secret used to write/read a backup envelope — either a user passphrase or this host's raw
/// credential key. Created via the factory methods; the codec picks the KDF from <see cref="Source"/>.
/// </summary>
public sealed class BackupSecret
{
    internal BackupKeySource Source { get; }
    internal byte[] Material { get; }

    private BackupSecret(BackupKeySource source, byte[] material)
    {
        Source = source;
        Material = material;
    }

    /// <summary>A portable, passphrase-derived secret (PBKDF2).</summary>
    public static BackupSecret Passphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new ArgumentException("A non-empty passphrase is required.", nameof(passphrase));
        }

        return new BackupSecret(BackupKeySource.Passphrase, Encoding.UTF8.GetBytes(passphrase));
    }

    /// <summary>A host-bound secret derived (HKDF) from the 32-byte credential encryption key.</summary>
    public static BackupSecret ServerKey(byte[] credentialKey)
    {
        ArgumentNullException.ThrowIfNull(credentialKey);
        if (credentialKey.Length != 32)
        {
            throw new ArgumentException("The server key must be exactly 32 bytes.", nameof(credentialKey));
        }

        return new BackupSecret(BackupKeySource.ServerKey, credentialKey);
    }
}

/// <summary>
/// The full in-memory contents of a <c>.kgbak</c>: the manifest plus the opaque per-aggregate data
/// documents, file-store drafts, and (optional) workflow-groups document. Round-trips through the codec.
/// </summary>
public sealed record BackupArchive(
    BackupManifest Manifest,
    string? Groups,
    IReadOnlyList<BackupArchiveEntry> Data,
    IReadOnlyList<BackupArchiveEntry> Workflows);

/// <summary>A single named document under <c>data/</c> or <c>workflows/</c>, carried verbatim by the codec.</summary>
/// <param name="Name">The leaf file name (no folder prefix), e.g. <c>credentials.json</c>. Must be a simple relative name.</param>
/// <param name="Content">The file's UTF-8 text content.</param>
public sealed record BackupArchiveEntry(string Name, string Content);

/// <summary>Raised when a byte stream is not a well-formed <c>.kgbak</c> archive or the passphrase is wrong.</summary>
public sealed class BackupArchiveException(string message) : InvalidOperationException(message);

/// <summary>Reads and writes the passphrase-encrypted <c>.kgbak</c> archive.</summary>
public static class BackupArchiveCodec
{
    public const string ManifestEntryName = "backup.json";
    public const string GroupsEntryName = "groups.json";
    public const string DataPrefix = "data/";
    public const string WorkflowsPrefix = "workflows/";

    // Envelope header layout (all cleartext, all authenticated as AES-GCM associated data):
    //   magic(4) | envelopeVersion(1) | keySource(1) | iterations(int32 BE, 4) | salt(16)
    // iterations is the PBKDF2 count for Passphrase; 0 for ServerKey (HKDF doesn't iterate).
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("KGBK");
    private const byte EnvelopeVersion = 1;

    // OWASP-recommended floor for PBKDF2-HMAC-SHA256 (2023). High enough to make a stolen-archive
    // passphrase guess expensive; the count is stored in the header so it can be raised later.
    private const int Pbkdf2Iterations = 600_000;

    // HKDF "info" context string — domain-separates the envelope key from any other use of the host key.
    private static readonly byte[] ServerKeyInfo = Encoding.ASCII.GetBytes("kgbak-server-key-v1");

    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const int HeaderLength = 4 + 1 + 1 + 4 + SaltLength;

    // Zip stores DOS timestamps (min 1980); pin to the floor so the inner zip bytes never depend on the clock.
    private static readonly DateTimeOffset DeterministicTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Serializes <paramref name="archive"/> to a complete, passphrase-encrypted <c>.kgbak</c> byte array.</summary>
    public static byte[] Write(BackupArchive archive, string passphrase) =>
        Write(archive, BackupSecret.Passphrase(passphrase));

    /// <summary>Serializes <paramref name="archive"/> to a complete, encrypted <c>.kgbak</c> using the given secret.</summary>
    public static byte[] Write(BackupArchive archive, BackupSecret secret)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(archive.Manifest);
        ArgumentNullException.ThrowIfNull(secret);

        var innerZip = WriteInnerZip(archive);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var iterations = secret.Source == BackupKeySource.Passphrase ? Pbkdf2Iterations : 0;
        var header = BuildHeader(secret.Source, iterations, salt);
        var key = DeriveKey(secret, salt, iterations);

        var cipherText = new byte[innerZip.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(key, TagLength))
        {
            aes.Encrypt(nonce, innerZip, cipherText, tag, header);
        }

        // output = header | nonce | tag | ciphertext
        var output = new byte[header.Length + NonceLength + TagLength + cipherText.Length];
        var offset = 0;
        Buffer.BlockCopy(header, 0, output, offset, header.Length); offset += header.Length;
        Buffer.BlockCopy(nonce, 0, output, offset, NonceLength); offset += NonceLength;
        Buffer.BlockCopy(tag, 0, output, offset, TagLength); offset += TagLength;
        Buffer.BlockCopy(cipherText, 0, output, offset, cipherText.Length);

        return output;
    }

    /// <summary>Reads which key source a <c>.kgbak</c> uses, from its cleartext header — no decryption.</summary>
    /// <remarks>Lets the caller pick the right secret (passphrase vs this host's key) before reading.</remarks>
    public static BackupKeySource PeekKeySource(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < HeaderLength + NonceLength + TagLength || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new BackupArchiveException("The file is not a recognized .kgbak backup archive.");
        }

        if (bytes[4] != EnvelopeVersion)
        {
            throw new BackupArchiveException($"Unsupported backup envelope version {bytes[4]}.");
        }

        return ParseKeySource(bytes[5]);
    }

    /// <summary>Decrypts and parses a <c>.kgbak</c> byte array using a passphrase.</summary>
    public static BackupArchive Read(byte[] bytes, string passphrase) =>
        Read(bytes, BackupSecret.Passphrase(passphrase));

    /// <summary>Decrypts and parses a <c>.kgbak</c> byte array using the given secret.</summary>
    /// <exception cref="BackupArchiveException">Malformed, wrong key/passphrase, key-source mismatch, or a required entry is missing.</exception>
    public static BackupArchive Read(byte[] bytes, BackupSecret secret)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(secret);

        if (bytes.Length < HeaderLength + NonceLength + TagLength)
        {
            throw new BackupArchiveException("The backup archive is truncated or not a .kgbak file.");
        }

        if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new BackupArchiveException("The file is not a recognized .kgbak backup archive.");
        }

        var envelopeVersion = bytes[4];
        if (envelopeVersion != EnvelopeVersion)
        {
            throw new BackupArchiveException($"Unsupported backup envelope version {envelopeVersion}.");
        }

        var source = ParseKeySource(bytes[5]);
        if (source != secret.Source)
        {
            throw new BackupArchiveException(source == BackupKeySource.ServerKey
                ? "This backup is bound to its source server's key (created with the 'this server' option) — it can only be restored on that host, and a passphrase won't open it."
                : "This backup is passphrase-protected — provide its passphrase to open it.");
        }

        var iterations = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(6, 4));
        if (source == BackupKeySource.Passphrase && iterations <= 0)
        {
            throw new BackupArchiveException("The backup archive declares an invalid PBKDF2 iteration count.");
        }

        var header = bytes.AsSpan(0, HeaderLength).ToArray();
        var salt = bytes.AsSpan(4 + 1 + 1 + 4, SaltLength).ToArray();
        var nonce = bytes.AsSpan(HeaderLength, NonceLength).ToArray();
        var tag = bytes.AsSpan(HeaderLength + NonceLength, TagLength).ToArray();
        var cipherText = bytes.AsSpan(HeaderLength + NonceLength + TagLength).ToArray();

        var key = DeriveKey(secret, salt, iterations);
        var plainText = new byte[cipherText.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, cipherText, tag, plainText, header);
        }
        catch (AuthenticationTagMismatchException)
        {
            // Indistinguishable by design: wrong key/passphrase and a corrupted/tampered archive both land here.
            throw new BackupArchiveException(source == BackupKeySource.ServerKey
                ? "Couldn't decrypt with this server's key — a server-key backup only restores on the host that created it (its credential key must be unchanged)."
                : "Incorrect passphrase, or the backup archive is corrupt.");
        }

        return ReadInnerZip(plainText);
    }

    private static BackupKeySource ParseKeySource(byte value) => value switch
    {
        (byte)BackupKeySource.Passphrase => BackupKeySource.Passphrase,
        (byte)BackupKeySource.ServerKey => BackupKeySource.ServerKey,
        _ => throw new BackupArchiveException($"Unsupported backup key source id {value}."),
    };

    private static byte[] BuildHeader(BackupKeySource source, int iterations, byte[] salt)
    {
        var header = new byte[HeaderLength];
        var offset = 0;
        Buffer.BlockCopy(Magic, 0, header, offset, Magic.Length); offset += Magic.Length;
        header[offset++] = EnvelopeVersion;
        header[offset++] = (byte)source;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(offset, 4), iterations); offset += 4;
        Buffer.BlockCopy(salt, 0, header, offset, salt.Length);
        return header;
    }

    private static byte[] DeriveKey(BackupSecret secret, byte[] salt, int iterations) => secret.Source switch
    {
        BackupKeySource.Passphrase => Rfc2898DeriveBytes.Pbkdf2(secret.Material, salt, iterations, HashAlgorithmName.SHA256, KeyLength),
        BackupKeySource.ServerKey => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret.Material, KeyLength, salt, ServerKeyInfo),
        _ => throw new BackupArchiveException("Unsupported backup key source."),
    };

    private static byte[] WriteInnerZip(BackupArchive archive)
    {
        var entries = new List<(string Path, string Content)>
        {
            (ManifestEntryName, BackupSerializer.SerializeManifest(archive.Manifest)),
        };

        if (archive.Groups is not null)
        {
            entries.Add((GroupsEntryName, archive.Groups));
        }

        AppendNamespaced(entries, DataPrefix, archive.Data ?? [], "data");
        AppendNamespaced(entries, WorkflowsPrefix, archive.Workflows ?? [], "workflow");

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Canonical, stable write order so the inner zip bytes are deterministic for a given content.
            foreach (var (path, content) in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = DeterministicTimestamp;
                using var writer = new StreamWriter(entry.Open(), Utf8NoBom);
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static BackupArchive ReadInnerZip(byte[] zipBytes)
    {
        string? manifestJson = null;
        string? groupsJson = null;
        var data = new List<BackupArchiveEntry>();
        var workflows = new List<BackupArchiveEntry>();

        try
        {
            using var buffer = new MemoryStream(zipBytes, writable: false);
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue; // directory placeholder
                }

                var content = ReadEntry(entry);
                switch (entry.FullName)
                {
                    case ManifestEntryName:
                        manifestJson = content;
                        break;
                    case GroupsEntryName:
                        groupsJson = content;
                        break;
                    default:
                        Classify(entry.FullName, content, data, workflows);
                        break;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new BackupArchiveException($"The backup archive payload is corrupt or not a zip: {ex.Message}");
        }

        if (manifestJson is null)
        {
            throw new BackupArchiveException($"The backup archive is missing '{ManifestEntryName}'.");
        }

        return new BackupArchive(
            BackupSerializer.DeserializeManifest(manifestJson),
            groupsJson,
            data,
            workflows);
    }

    private static void Classify(
        string fullName,
        string content,
        List<BackupArchiveEntry> data,
        List<BackupArchiveEntry> workflows)
    {
        if (TryStrip(fullName, DataPrefix, out var dataName))
        {
            data.Add(new BackupArchiveEntry(dataName, content));
            return;
        }

        if (TryStrip(fullName, WorkflowsPrefix, out var workflowName))
        {
            workflows.Add(new BackupArchiveEntry(workflowName, content));
            return;
        }

        // Fail loud: an unrecognised entry means a shape we don't round-trip, so reject rather than lose data.
        throw new BackupArchiveException($"The backup archive contains an unexpected entry '{fullName}'.");
    }

    private static void AppendNamespaced(
        List<(string Path, string Content)> entries,
        string prefix,
        IReadOnlyList<BackupArchiveEntry> files,
        string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ValidateLeafName(file.Name, kind);
            if (!seen.Add(file.Name))
            {
                throw new BackupArchiveException($"Duplicate {kind} entry '{file.Name}' in the backup archive.");
            }

            entries.Add((prefix + file.Name, file.Content));
        }
    }

    private static void ValidateLeafName(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('/')
            || name.Contains('\\')
            || name == "."
            || name == ".."
            || Path.IsPathRooted(name))
        {
            throw new BackupArchiveException($"Invalid {kind} entry name '{name}' in the backup archive.");
        }
    }

    private static bool TryStrip(string fullName, string prefix, out string leaf)
    {
        if (fullName.StartsWith(prefix, StringComparison.Ordinal))
        {
            var candidate = fullName[prefix.Length..];
            if (candidate.Length > 0 && !candidate.Contains('/'))
            {
                leaf = candidate;
                return true;
            }
        }

        leaf = string.Empty;
        return false;
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Utf8NoBom);
        return reader.ReadToEnd();
    }
}
