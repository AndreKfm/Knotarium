// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Features.Compiler;
using Xunit;

namespace Knotarium.Tests.Compiler;

public class SubflowScopeTests
{
    [Fact]
    public void TopLevel_HasNoScope()
    {
        Assert.Equal(string.Empty, SubflowScope.FromPrefix(string.Empty));
        Assert.Equal(string.Empty, SubflowScope.ForNodeId("inline-1"));
        Assert.Equal("counter", SubflowScope.Apply(string.Empty, "counter"));
    }

    [Fact]
    public void FromPrefix_IsIdentifierSafe()
    {
        // '-' and '/' (expression-tokenizer delimiters) are collapsed to '_'.
        Assert.Equal("sf_subflow_a__", SubflowScope.FromPrefix("subflow-a"));
        Assert.Equal("sf_subflow_a_subflow_b__", SubflowScope.FromPrefix("subflow-a/subflow-b"));
    }

    [Theory]
    [InlineData("subflow-a/inline-1", "subflow-a")]
    [InlineData("subflow-a/subflow-b/inline-1", "subflow-a/subflow-b")]
    public void ForNodeId_MatchesCompileTimePrefixScope(string nodeId, string prefix)
    {
        // The runtime (Inline Code) derives the scope from the node id; the compiler derives it from
        // the inline prefix. They must produce the same token or scoped reads/writes won't line up.
        Assert.Equal(SubflowScope.FromPrefix(prefix), SubflowScope.ForNodeId(nodeId));
    }

    [Fact]
    public void Apply_PrefixesWithinAScope()
    {
        Assert.Equal("sf_subflow_a__counter", SubflowScope.Apply("sf_subflow_a__", "counter"));
    }
}
