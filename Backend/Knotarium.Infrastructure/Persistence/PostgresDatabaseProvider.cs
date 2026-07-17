// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

public class PostgresDatabaseProvider : IDatabaseProvider
{
    public string Name => "Postgres";

    public void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        // TODO(v1.5): Postgres provider implementation
        throw new NotImplementedException("Postgres database provider is not yet implemented.");
    }
}
