// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

public interface IDatabaseProvider
{
    string Name { get; }
    void Configure(DbContextOptionsBuilder builder, string connectionString);
}
