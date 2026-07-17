// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Knotarium.Infrastructure.Security;

using Knotarium.Features.Execution;

namespace Knotarium.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Bundle format — logical manifest (bundle.json) vs resolved lock (bundle.lock).
// Mirrors package.json vs package-lock.json: the manifest is authoring intent
// (human-edited, indexed for search, NO hashes); the lock is generated at
// resolve/export and is what the installer verifies and trusts.
//
// An on-disk `.kgbundle` is a zip of: bundle.json, bundle.lock, packages/…,
// workflows/<key>.json (each a reused WorkflowExportDocument). Reading/writing
// the zip is a later step; this file only defines the records + hashing.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A package reference in the manifest. Carries no hash — resolution lives in the lock.</summary>
public sealed record BundlePackageRef(
    string Id,
    string VersionConstraintOrPin,
    string Source);

/// <summary>
/// A symbolic credential slot. Intent only — never a secret value. Bundled workflows reference
/// credentials as <c>slot:&lt;Slot&gt;</c>; the installer rebinds these to real credential ids.
/// </summary>
public sealed record BundleCredentialSlot(
    string Slot,
    string Type,
    string DisplayName,
    string? Description,
    IReadOnlyList<string> Checklist);

/// <summary><c>Ref</c> is the filename under <c>workflows/</c> in the archive.</summary>
public sealed record BundleWorkflowRef(
    string Key,
    string Role,
    string Ref);

/// <summary>Source/publisher <strong>intent</strong>. Verified provenance lives in the lock.</summary>
public sealed record BundleProvenance(
    string Source,
    string Publisher);

/// <summary>
/// <c>bundle.json</c> — authoring intent, human-edited, version-controlled, indexed for search.
/// Deliberately hash-free: anything verified (hashes, resolved source, trust level) lives in the lock.
/// </summary>
public sealed record BundleManifest(
    string BundleId,
    string BundleVersion,
    string Name,
    string Publisher,
    IReadOnlyList<string> Tags,
    string Category,
    int SchemaVersion,
    string MinEngineVersion,
    IReadOnlyList<BundlePackageRef> Packages,
    IReadOnlyList<BundleCredentialSlot> CredentialSlots,
    IReadOnlyList<BundleWorkflowRef> Workflows,
    BundleProvenance Provenance);

/// <summary>A resolved package entry in the lock — the unit the installer hash-verifies.</summary>
public sealed record BundleLockPackage(
    string Id,
    string ResolvedVersion,
    string Sha256,
    string ResolvedSource,
    string TrustLevel);

/// <summary>
/// <c>bundle.lock</c> — generated at resolve/export; what the installer verifies &amp; trusts.
/// Lock-only fields (hashes, resolved source, trust level, resolvedAt) must never appear in the manifest.
/// </summary>
/// <remarks>
/// <see cref="BundleLockPackage.TrustLevel"/> is a string token here so the bundle format has no hard
/// dependency on the trust enum; the install path maps it to the derived trust level.
/// </remarks>
public sealed record BundleLock(
    IReadOnlyList<BundleLockPackage> Packages,
    string ResolvedAt,
    string ResolverVersion);

/// <summary>
/// Deterministic SHA256 hashing for bundle contents. Uses the shared
/// <see cref="CanonicalJsonSerializer"/> (recursively key-sorted JSON) so logically-equal payloads —
/// e.g. the same JSON object with keys in a different order — hash identically.
/// </summary>
public static class BundleHasher
{
    /// <summary>The small payload a package hash is computed over (manifest + source + signature).</summary>
    private sealed record PackageHashPayload(JsonNode? Manifest, string Source, string? Signature);

    /// <summary>
    /// Hashes a package by the data a bundle actually carries for it (its manifest JSON, source, and
    /// optional signature). When <paramref name="manifestJson"/> is valid JSON it is parsed and folded
    /// into the canonical payload, so the manifest's own key order does not affect the hash; otherwise
    /// it is hashed as an opaque string node.
    /// </summary>
    public static string ComputePackageHash(string manifestJson, string source, string? signature)
    {
        var canonical = CanonicalJsonSerializer.Serialize(new PackageHashPayload(
            ParseManifest(manifestJson),
            source ?? string.Empty,
            signature));
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static JsonNode? ParseManifest(string? manifestJson)
    {
        if (string.IsNullOrEmpty(manifestJson))
        {
            return JsonValue.Create(string.Empty);
        }

        try
        {
            // Fold the manifest into the payload as structured JSON so canonical key-sorting reaches
            // inside it; logically-equal manifests with different key order then hash identically.
            return JsonNode.Parse(manifestJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(manifestJson);
        }
    }

    /// <summary>SHA256 (lower-hex) of raw bytes — for embedded payloads carried verbatim.</summary>
    public static string ComputeBytesHash(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Hex(SHA256.HashData(bytes));
    }

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
