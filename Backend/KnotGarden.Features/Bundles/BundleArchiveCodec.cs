using System;
using System.Collections.Generic;
using System.IO;
using KnotGarden.Features.Portability;

using KnotGarden.Features.Execution;

namespace KnotGarden.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// .kgbundle archive codec — the IO edge that turns the in-memory bundle (typed
// manifest + lock, plus opaque package/workflow files) into a single zip and
// back. This is the first step that crosses the dependency-free boundary, but it
// stays self-contained: System.IO.Compression only, no DB/registry/disk paths.
//
// Layout inside the zip:
//   bundle.json            — BundleManifest         (authoring intent)
//   bundle.lock            — BundleLock             (verified/trusted record)
//   packages/<name>        — one file per package   (opaque to the codec)
//   workflows/<name>       — one WorkflowExportDocument per bundled workflow
//
// Deliberately decoupled: package payloads and workflow documents are carried as
// named text blobs. Workflow-document serialization already lives in
// WorkflowVersionSerializer; the codec only owns the archive *shape*, so it never
// re-implements (or drifts from) those formats.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A single file under <c>packages/</c> or <c>workflows/</c>, carried verbatim by the codec.</summary>
/// <param name="Name">The leaf file name (no folder prefix), e.g. <c>main.json</c>. Must be a simple relative name.</param>
/// <param name="Content">The file's UTF-8 text content.</param>
public sealed record BundleArchiveEntry(string Name, string Content);

/// <summary>
/// The full in-memory contents of a <c>.kgbundle</c>: the typed manifest and lock, plus the opaque
/// package and workflow files. Round-trips losslessly through <see cref="BundleArchiveCodec"/>.
/// </summary>
public sealed record BundleArchive(
    BundleManifest Manifest,
    BundleLock Lock,
    IReadOnlyList<BundleArchiveEntry> Packages,
    IReadOnlyList<BundleArchiveEntry> Workflows);

/// <summary>Raised when a byte stream is not a well-formed <c>.kgbundle</c> archive.</summary>
public sealed class BundleArchiveException(string message) : InvalidOperationException(message);

/// <summary>
/// Reads and writes the <c>.kgbundle</c> zip. Writing is <strong>deterministic</strong>: entries are
/// emitted in a stable order with a fixed timestamp, so the same archive contents always yield the same
/// bytes (and therefore the same hash) — the diff/provenance story depends on it.
/// </summary>
public static class BundleArchiveCodec
{
    public const string ManifestEntryName = "bundle.json";
    public const string LockEntryName = "bundle.lock";
    public const string PackagesPrefix = "packages/";
    public const string WorkflowsPrefix = "workflows/";

    // Bundles can legitimately carry several packages + workflows, so allow more entries than the
    // single-workflow default while keeping the size/ratio guards.
    private static readonly WorkflowArchiveLimits BundleLimits = WorkflowArchiveLimits.Default with { MaxEntryCount = 1024 };

    /// <summary>Serializes <paramref name="archive"/> to a complete <c>.kgbundle</c> byte array.</summary>
    public static byte[] Write(BundleArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(archive.Manifest);
        ArgumentNullException.ThrowIfNull(archive.Lock);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManifestEntryName] = BundleSerializer.SerializeManifest(archive.Manifest),
            [LockEntryName] = BundleSerializer.SerializeLock(archive.Lock),
        };

        AppendNamespaced(entries, PackagesPrefix, archive.Packages ?? [], "package");
        AppendNamespaced(entries, WorkflowsPrefix, archive.Workflows ?? [], "workflow");

        return WorkflowArchiveCodec.Write(entries);
    }

    /// <summary>Parses a <c>.kgbundle</c> byte array back into a <see cref="BundleArchive"/>.</summary>
    /// <exception cref="BundleArchiveException">The bytes are not a valid archive, or a required entry is missing.</exception>
    public static BundleArchive Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        IReadOnlyDictionary<string, string> entries;
        try
        {
            entries = WorkflowArchiveCodec.Read(bytes, BundleLimits);
        }
        catch (WorkflowArchiveException ex)
        {
            throw new BundleArchiveException(ex.Message);
        }

        string? manifestJson = null;
        string? lockJson = null;
        var packages = new List<BundleArchiveEntry>();
        var workflows = new List<BundleArchiveEntry>();

        foreach (var (fullName, content) in entries)
        {
            switch (fullName)
            {
                case ManifestEntryName:
                    manifestJson = content;
                    break;
                case LockEntryName:
                    lockJson = content;
                    break;
                default:
                    Classify(fullName, content, packages, workflows);
                    break;
            }
        }

        if (manifestJson is null)
        {
            throw new BundleArchiveException($"The bundle archive is missing '{ManifestEntryName}'.");
        }

        if (lockJson is null)
        {
            throw new BundleArchiveException($"The bundle archive is missing '{LockEntryName}'.");
        }

        return new BundleArchive(
            BundleSerializer.DeserializeManifest(manifestJson),
            BundleSerializer.DeserializeLock(lockJson),
            packages,
            workflows);
    }

    private static void Classify(
        string fullName,
        string content,
        List<BundleArchiveEntry> packages,
        List<BundleArchiveEntry> workflows)
    {
        if (TryStrip(fullName, PackagesPrefix, out var packageName))
        {
            packages.Add(new BundleArchiveEntry(packageName, content));
            return;
        }

        if (TryStrip(fullName, WorkflowsPrefix, out var workflowName))
        {
            workflows.Add(new BundleArchiveEntry(workflowName, content));
            return;
        }

        // Fail loud rather than silently drop: an unrecognised entry means the archive is a shape we don't
        // round-trip, so callers can't assume read→write is identity. Better to reject than lose data.
        throw new BundleArchiveException($"The bundle archive contains an unexpected entry '{fullName}'.");
    }

    private static void AppendNamespaced(
        Dictionary<string, string> entries,
        string prefix,
        IReadOnlyList<BundleArchiveEntry> files,
        string kind)
    {
        foreach (var file in files)
        {
            ValidateLeafName(file.Name, kind);
            if (!entries.TryAdd(prefix + file.Name, file.Content))
            {
                throw new BundleArchiveException($"Duplicate {kind} entry '{file.Name}' in the bundle archive.");
            }
        }
    }

    // A leaf name is a single path segment: no folder separators, no traversal, no rooting. This keeps the
    // archive flat-per-namespace and sidesteps zip-slip even though Read never writes to disk.
    private static void ValidateLeafName(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('/')
            || name.Contains('\\')
            || name == "."
            || name == ".."
            || Path.IsPathRooted(name))
        {
            throw new BundleArchiveException($"Invalid {kind} entry name '{name}' in the bundle archive.");
        }
    }

    private static bool TryStrip(string fullName, string prefix, out string leaf)
    {
        if (fullName.StartsWith(prefix, StringComparison.Ordinal))
        {
            var candidate = fullName[prefix.Length..];
            // Only direct children of the namespace folder are valid; nested paths are rejected on read.
            if (candidate.Length > 0 && !candidate.Contains('/'))
            {
                leaf = candidate;
                return true;
            }
        }

        leaf = string.Empty;
        return false;
    }
}
