// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Xunit;

namespace Knotarium.Tests.ErrorWorkflow;

public class ExecutionDiscardPolicyTests
{
    [Fact]
    public void Failed_CanBeDiscarded()
        => Assert.True(ExecutionDiscardPolicy.CanDiscard(ExecutionStatus.Failed));

    [Theory]
    [InlineData(ExecutionStatus.Pending)]
    [InlineData(ExecutionStatus.Running)]
    [InlineData(ExecutionStatus.Suspended)]
    [InlineData(ExecutionStatus.Cancelled)]
    [InlineData(ExecutionStatus.Completed)]
    [InlineData(ExecutionStatus.WaitingForRetry)]
    [InlineData(ExecutionStatus.Discarded)]
    public void NonFailed_CannotBeDiscarded(ExecutionStatus status)
        => Assert.False(ExecutionDiscardPolicy.CanDiscard(status));
}
