// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Features.Execution;
using Xunit;

namespace Knotarium.Tests.Execution;

/// <summary>
/// The friendly payload alias name (e.g. action "CustomAction" → `signal.customAction.<field>`). Must be
/// identifier-safe so it can be a dotted variable head; numeric event type ids must opt out (callers fall
/// back to `signal.params`).
/// </summary>
public class SignalTypeAliasTests
{
    [Theory]
    [InlineData("CustomAction", "customAction")]
    [InlineData("CameraCycleStart", "cameraCycleStart")]
    [InlineData("ABCConnect", "aBCConnect")] // only the first char is lowered
    [InlineData("_Internal", "_Internal")]
    public void Lowercases_the_first_char_of_identifier_safe_types(string type, string expected)
    {
        Assert.Equal(expected, ExternalSignalRunEnqueuer.TypeAlias(type));
    }

    [Theory]
    [InlineData("3")]            // numeric event type id — not a valid head
    [InlineData("123Event")]    // starts with a digit
    [InlineData("Has Space")]
    [InlineData("dotted.name")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_for_non_identifier_types(string? type)
    {
        Assert.Null(ExternalSignalRunEnqueuer.TypeAlias(type));
    }
}
