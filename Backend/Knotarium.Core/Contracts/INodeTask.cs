// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Domain;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

public interface INodeTask
{
    Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken);
}
