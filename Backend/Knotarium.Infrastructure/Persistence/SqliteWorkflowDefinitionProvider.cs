// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// Backward-compatible workflow definition provider that delegates to the database-backed workflow store.
/// </summary>
public class SqliteWorkflowDefinitionProvider : DatabaseWorkflowStore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteWorkflowDefinitionProvider"/> class.
    /// </summary>
    /// <param name="context">The application database context.</param>
    public SqliteWorkflowDefinitionProvider(AppDbContext context)
        : base(context)
    {
    }
}
