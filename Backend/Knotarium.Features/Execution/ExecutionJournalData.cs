// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// Builds and reads the structured <c>Data</c> payloads of execution journal entries
/// (failure data, attempt data, and pending non-idempotent attempt lookup).
/// </summary>
internal static class ExecutionJournalData
{
    public static Dictionary<string, object> CreateFailureJournalData(
        string? errorMessage, Guid? attemptId, string? errorCode = null)
    {
        var data = new Dictionary<string, object>
        {
            ["error"] = errorMessage ?? "Node execution failed."
        };

        if (attemptId.HasValue)
        {
            data["AttemptId"] = attemptId.Value.ToString();
        }

        // R6: a discrete, field-queryable error code in the hash-chained audit Data (vs substring-matching
        // the message). Present only when the failing task supplied a structured code.
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            data["errorCode"] = errorCode;
        }

        return data;
    }

    public static Dictionary<string, object> CreateAttemptData(string? reason, string? attemptId)
    {
        var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            data["reason"] = reason;
        }

        if (!string.IsNullOrWhiteSpace(attemptId))
        {
            data["AttemptId"] = attemptId;
        }

        return data;
    }

    public static string? FindPendingAttemptId(IEnumerable<ExecutionJournal> journalEntries, NodeId nodeId)
    {
        var attemptEntries = journalEntries
            .Where(entry => entry.EventType == JournalEventTypes.AttemptingExternalEffect && entry.NodeId == nodeId)
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();

        foreach (var attemptEntry in attemptEntries)
        {
            var attemptId = TryReadString(attemptEntry.Data, "AttemptId");
            if (string.IsNullOrWhiteSpace(attemptId))
            {
                continue;
            }

            var hasCompletion = journalEntries.Any(entry =>
                entry.NodeId == nodeId &&
                (entry.EventType == JournalEventTypes.NodeExecutionCompleted || entry.EventType == JournalEventTypes.NodeExecutionFailed) &&
                string.Equals(TryReadString(entry.Data, "AttemptId"), attemptId, StringComparison.OrdinalIgnoreCase));

            if (!hasCompletion)
            {
                return attemptId;
            }
        }

        return null;
    }

    private static string? TryReadString(IReadOnlyDictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string stringValue => stringValue,
            Guid guidValue => guidValue.ToString(),
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString(),
            _ => value.ToString()
        };
    }
}
