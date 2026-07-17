// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Reactive;
using Xunit;

namespace Knotarium.Tests.Reactive;

public class ReactiveEventPhaseTests
{
    [Theory]
    [InlineData("3:started", "3", EventPhase.Started)]
    [InlineData("3:stopped", "3", EventPhase.Stopped)]
    [InlineData("3:STARTED", "3", EventPhase.Started)]
    [InlineData("VehicleRecognised:stopped", "VehicleRecognised", EventPhase.Stopped)]
    [InlineData("3", "3", EventPhase.Started)]                       // bare → default Started
    [InlineData("ns:3", "ns:3", EventPhase.Started)]                 // unrelated colon left intact
    public void Parse_splits_phase_suffix(string input, string expectedBase, EventPhase expectedPhase)
    {
        var (baseType, phase) = ReactiveEventPhase.Parse(input);
        Assert.Equal(expectedBase, baseType);
        Assert.Equal(expectedPhase, phase);
    }

    [Fact]
    public void Qualify_round_trips_through_Parse()
    {
        Assert.Equal("3:started", ReactiveEventPhase.Qualify("3", EventPhase.Started));
        Assert.Equal("3:stopped", ReactiveEventPhase.Qualify("3", EventPhase.Stopped));

        var (baseType, phase) = ReactiveEventPhase.Parse(ReactiveEventPhase.Qualify("42", EventPhase.Stopped));
        Assert.Equal("42", baseType);
        Assert.Equal(EventPhase.Stopped, phase);
    }

    [Theory]
    [InlineData(EventPhase.Started, true, true)]
    [InlineData(EventPhase.Started, false, false)]
    [InlineData(EventPhase.Started, null, true)]    // lifecycle-less events flow through a Started pin
    [InlineData(EventPhase.Stopped, false, true)]
    [InlineData(EventPhase.Stopped, true, false)]
    [InlineData(EventPhase.Stopped, null, false)]
    public void Matches_gates_on_the_active_flag(EventPhase phase, bool? active, bool expected)
        => Assert.Equal(expected, ReactiveEventPhase.Matches(phase, active));
}
