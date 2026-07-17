// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

/// <summary>Evaluates active polling triggers that are due and conditionally enqueues runs.</summary>
public interface IPollEvaluationService
{
    Task EvaluateDuePollsAsync(CancellationToken cancellationToken = default);
}
