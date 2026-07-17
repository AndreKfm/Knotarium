// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

public interface IExecutionEventPublisher
{
    Task PublishAsync(ExecutionInstanceId executionId, ExecutionJournal entry, CancellationToken cancellationToken = default);
}
