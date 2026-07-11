using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knotarium.Features.Bundles;

/// <summary>
/// Lossless (de)serialization of the bundle manifest and lock. Web-cased JSON with enum-as-string;
/// the manifest is intentionally hash-free, so a serialized <c>bundle.json</c> never carries lock-only
/// fields. Hashing uses the canonical serializer instead (see <see cref="BundleHasher"/>); this writer
/// is for human-readable, round-trippable on-disk files.
/// </summary>
public static class BundleSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializeManifest(BundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    public static BundleManifest DeserializeManifest(string json)
    {
        var manifest = JsonSerializer.Deserialize<BundleManifest>(json, Options);
        if (manifest is null)
        {
            throw new InvalidOperationException("The bundle manifest is empty or not valid bundle.json.");
        }

        return manifest;
    }

    public static string SerializeLock(BundleLock @lock)
    {
        ArgumentNullException.ThrowIfNull(@lock);
        return JsonSerializer.Serialize(@lock, Options);
    }

    public static BundleLock DeserializeLock(string json)
    {
        var @lock = JsonSerializer.Deserialize<BundleLock>(json, Options);
        if (@lock is null)
        {
            throw new InvalidOperationException("The bundle lock is empty or not valid bundle.lock.");
        }

        return @lock;
    }

    /// <summary>
    /// Serializes a resolved package as carried under <c>packages/</c> in the archive: the exact signed
    /// payload plus its detached signature, so the installer can re-hash and re-verify it against the lock.
    /// </summary>
    public static string SerializePackage(ResolvedBundlePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return JsonSerializer.Serialize(package, Options);
    }

    public static ResolvedBundlePackage DeserializePackage(string json)
    {
        var package = JsonSerializer.Deserialize<ResolvedBundlePackage>(json, Options);
        if (package is null || package.Payload is null)
        {
            throw new InvalidOperationException("The bundle package file is empty or not a valid package entry.");
        }

        return package;
    }
}
