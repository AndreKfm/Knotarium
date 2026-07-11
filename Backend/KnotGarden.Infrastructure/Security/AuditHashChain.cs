using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KnotGarden.Core.Domain;

namespace KnotGarden.Infrastructure.Security;

public sealed record AuditChainPayload(
    Guid Id,
    string Action,
    string Actor,
    DateTimeOffset Timestamp,
    string Details
);

public static class AuditHashChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeEntryHash(AuditEntry entry, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var previousHashBytes = DecodeHash(previousHash);
        var canonicalEntry = CanonicalJsonSerializer.Serialize(new AuditChainPayload(
            entry.Id,
            entry.Action,
            entry.Actor,
            entry.Timestamp,
            entry.Details));

        var entryBytes = Encoding.UTF8.GetBytes(canonicalEntry);
        var combinedBytes = new byte[previousHashBytes.Length + entryBytes.Length];
        Buffer.BlockCopy(previousHashBytes, 0, combinedBytes, 0, previousHashBytes.Length);
        Buffer.BlockCopy(entryBytes, 0, combinedBytes, previousHashBytes.Length, entryBytes.Length);

        return Convert.ToHexString(SHA256.HashData(combinedBytes)).ToLowerInvariant();
    }

    public static IReadOnlyList<AuditEntry> RebuildChain(IEnumerable<AuditEntry> entries)
    {
        var orderedEntries = entries
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.Id)
            .ToList();

        var previousHash = GenesisHash;
        foreach (var entry in orderedEntries)
        {
            entry.PreviousHash = previousHash;
            entry.EntryHash = ComputeEntryHash(entry, previousHash);
            previousHash = entry.EntryHash;
        }

        return orderedEntries;
    }

    public static bool VerifyChain(IEnumerable<AuditEntry> entries)
    {
        var orderedEntries = entries
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.Id)
            .ToList();

        var previousHash = GenesisHash;
        foreach (var entry in orderedEntries)
        {
            if (!string.Equals(entry.PreviousHash, previousHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var expectedHash = ComputeEntryHash(entry, previousHash);
            if (!string.Equals(entry.EntryHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            previousHash = entry.EntryHash;
        }

        return true;
    }

    private static byte[] DecodeHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return new byte[32];
        }

        return Convert.FromHexString(hash);
    }
}