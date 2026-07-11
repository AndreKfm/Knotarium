using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Knotarium.Features.Portability;

// ─────────────────────────────────────────────────────────────────────────────
// Shared zip primitive for the portable-workflow formats (.kgbundle, .kgtpl).
// One deterministic writer and ONE hardened reader, so the path-traversal guard
// and the zip-bomb limits live in a single place rather than being copied into
// each format's codec.
//
// Writing is deterministic: entries are emitted in a stable ordinal path order
// with a fixed timestamp, so the same contents always yield the same bytes (and
// therefore the same hash). Reading enforces resource limits (zip-slip ≠ zip-bomb)
// before any caller touches the contents.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Resource limits enforced on read. Zip-slip is a correctness guard; these bound cost.</summary>
public sealed record WorkflowArchiveLimits(
    long MaxArchiveBytes,
    long MaxTotalUncompressedBytes,
    long MaxEntryBytes,
    int MaxCompressionRatio,
    int MaxEntryCount)
{
    /// <summary>Conservative defaults for a single-workflow / small-bundle archive.</summary>
    public static readonly WorkflowArchiveLimits Default = new(
        MaxArchiveBytes: 5 * 1024 * 1024,
        MaxTotalUncompressedBytes: 25 * 1024 * 1024,
        MaxEntryBytes: 10 * 1024 * 1024,
        MaxCompressionRatio: 100,
        MaxEntryCount: 256);
}

/// <summary>Raised when a byte stream is not a well-formed archive or violates a resource limit.</summary>
public sealed class WorkflowArchiveException(string message) : InvalidOperationException(message);

/// <summary>Deterministic write / hardened read of a flat <c>path → UTF-8 text</c> entry map.</summary>
public static class WorkflowArchiveCodec
{
    // Zip stores DOS timestamps (min 1980); pin to the epoch floor so output bytes never depend on the clock.
    private static readonly DateTimeOffset DeterministicTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Serializes <paramref name="entries"/> to a deterministic zip. Each key is a relative archive path
    /// (forward slashes); duplicate keys and unsafe paths are rejected up front.
    /// </summary>
    public static byte[] Write(IReadOnlyDictionary<string, string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var path in entries.Keys)
        {
            ValidatePath(path);
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = DeterministicTimestamp;
                using var writer = new StreamWriter(entry.Open(), StrictUtf8);
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Parses a zip byte array into its <c>path → text</c> entries, enforcing <paramref name="limits"/> and
    /// rejecting path traversal, duplicates, and invalid UTF-8. Parse-and-validate only: no contents are
    /// interpreted here.
    /// </summary>
    /// <exception cref="WorkflowArchiveException">Malformed archive or a violated limit.</exception>
    public static IReadOnlyDictionary<string, string> Read(byte[] bytes, WorkflowArchiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        limits ??= WorkflowArchiveLimits.Default;

        if (bytes.LongLength > limits.MaxArchiveBytes)
        {
            throw new WorkflowArchiveException(
                $"The archive is {bytes.LongLength} bytes, exceeding the {limits.MaxArchiveBytes}-byte limit.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalUncompressed = 0;

        try
        {
            using var buffer = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

            if (zip.Entries.Count > limits.MaxEntryCount)
            {
                throw new WorkflowArchiveException(
                    $"The archive has {zip.Entries.Count} entries, exceeding the {limits.MaxEntryCount}-entry limit.");
            }

            foreach (var entry in zip.Entries)
            {
                // Skip directory placeholder entries (zero-length names ending in '/').
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                ValidatePath(entry.FullName);

                if (entry.Length > limits.MaxEntryBytes)
                {
                    throw new WorkflowArchiveException(
                        $"Entry '{entry.FullName}' is {entry.Length} bytes, exceeding the per-entry limit.");
                }

                // Guard against a tiny compressed entry inflating to an enormous one (zip bomb).
                if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                {
                    throw new WorkflowArchiveException(
                        $"Entry '{entry.FullName}' exceeds the {limits.MaxCompressionRatio}:1 compression-ratio limit.");
                }

                totalUncompressed += entry.Length;
                if (totalUncompressed > limits.MaxTotalUncompressedBytes)
                {
                    throw new WorkflowArchiveException("The archive's total uncompressed size exceeds the limit.");
                }

                var content = ReadEntry(entry, limits.MaxEntryBytes);
                if (!result.TryAdd(entry.FullName, content))
                {
                    throw new WorkflowArchiveException($"The archive contains a duplicate entry '{entry.FullName}'.");
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new WorkflowArchiveException($"The archive is corrupt or not a zip: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Validates a relative archive path: forward-slash separated, no traversal, no rooting, no backslashes,
    /// no <c>.</c>/<c>..</c> segments. Permits namespacing folders (e.g. <c>workflows/main.json</c>) while
    /// keeping a single zip-slip guard for every format.
    /// </summary>
    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\\')
            || Path.IsPathRooted(path)
            || path.StartsWith('/'))
        {
            throw new WorkflowArchiveException($"Invalid archive entry path '{path}'.");
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new WorkflowArchiveException($"Invalid archive entry path '{path}'.");
            }
        }
    }

    private static string ReadEntry(ZipArchiveEntry entry, long maxEntryBytes)
    {
        // Bound the read independently of the declared Length (which can lie) to defeat a forged header.
        using var stream = entry.Open();
        using var limited = new MemoryStream();
        var copyBuffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = stream.Read(copyBuffer, 0, copyBuffer.Length)) > 0)
        {
            copied += read;
            if (copied > maxEntryBytes)
            {
                throw new WorkflowArchiveException($"Entry '{entry.FullName}' exceeds the per-entry size limit while reading.");
            }

            limited.Write(copyBuffer, 0, read);
        }

        try
        {
            return StrictUtf8.GetString(limited.ToArray());
        }
        catch (DecoderFallbackException)
        {
            throw new WorkflowArchiveException($"Entry '{entry.FullName}' is not valid UTF-8 text.");
        }
    }
}
