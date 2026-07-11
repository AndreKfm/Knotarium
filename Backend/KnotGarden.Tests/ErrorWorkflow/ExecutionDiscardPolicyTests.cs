using KnotGarden.Core.Domain;
using KnotGarden.Features.Execution;
using Xunit;

namespace KnotGarden.Tests.ErrorWorkflow;

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
