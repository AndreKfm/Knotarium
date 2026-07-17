// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Knotarium.Core.Domain;

namespace Knotarium.Infrastructure.Persistence;

public class JsonValueConverter<T> : ValueConverter<T, string>
{
    public JsonValueConverter()
        : base(
            v => JsonSerializer.Serialize(v, PersistenceJsonOptions.Default),
            s => JsonSerializer.Deserialize<T>(s, PersistenceJsonOptions.Default) ?? default!)
    {
    }
}
