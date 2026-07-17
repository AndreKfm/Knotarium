// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

public interface IExecutionJournalWriter
{
    Task WriteAsync(ExecutionJournal entry);

    /// <summary>
    /// Writes several entries as one unit — ideally a single transaction, so the batch costs one
    /// write-lock acquisition and one commit instead of one per row. Default: sequential
    /// <see cref="WriteAsync"/> calls, for writers without a cheaper bulk path.
    /// </summary>
    async Task WriteBatchAsync(IReadOnlyList<ExecutionJournal> entries)
    {
        foreach (var entry in entries)
        {
            await WriteAsync(entry);
        }
    }
}
