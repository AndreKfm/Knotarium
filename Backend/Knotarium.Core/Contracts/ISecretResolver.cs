// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

public interface ISecretResolver
{
    Task<string?> ResolveAsync(string secretRef, CancellationToken cancellationToken = default);
}
