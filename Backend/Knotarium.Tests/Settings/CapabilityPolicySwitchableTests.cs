// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Knotarium.Core.Domain;
using Knotarium.Features.Settings;
using Xunit;

namespace Knotarium.Tests.Settings;

/// <summary>
/// Pins the switchable-capability list, because it has a counterpart the compiler cannot see.
///
/// <para><c>Frontend/src/components/CapabilitiesSetting.tsx</c> hardcodes the rows rendered on
/// Settings → Capabilities, and that screen is the ONLY way to set the policy. A capability that is
/// switchable here but missing there cannot be enabled at all — which is precisely what happened to
/// <c>aiAgent</c>: <c>AiAgentNodeTask</c> refused to run and told the reader to enable it "under
/// Settings → Capabilities", where no such switch existed. The node, its tools and the Order Concierge
/// starter template were unreachable through the product.</para>
///
/// <para>So this test is not really about the backend list being correct; it is a tripwire. Changing
/// the list fails here, and the failure names the file that has to change with it.</para>
/// </summary>
public class CapabilityPolicySwitchableTests
{
    [Fact]
    public void Switchable_capabilities_match_the_settings_screen()
    {
        var expected = new[]
        {
            NodeCapabilities.CodeExecution,
            NodeCapabilities.Database,
            NodeCapabilities.AiAgent,
        };

        Assert.True(
            expected.OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(CapabilityPolicyStore.Switchable.OrderBy(x => x, StringComparer.Ordinal)),
            "The switchable capability list changed. Settings → Capabilities renders a hardcoded list in "
            + "Frontend/src/components/CapabilitiesSetting.tsx and is the only way to set this policy, so a "
            + "capability added here without a row there can never be enabled. Update both, then update this "
            + "test.\n"
            + $"  backend:  {string.Join(", ", CapabilityPolicyStore.Switchable)}\n"
            + $"  expected: {string.Join(", ", expected)}");
    }
}
