// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knotarium.Core.Domain;
using Xunit;

namespace Knotarium.Tests;

public class NodeManifestCompilerTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void DefaultDeny_AssignsNonIdempotentSideEffectWhenOmitted()
    {
        // JSON omitting SideEffectKind
        var json = "{" +
            "\"Id\":\"custom-node\"," +
            "\"Version\":\"1.0.0\"," +
            "\"DisplayName\":\"Custom Node\"," +
            "\"Category\":\"Utility\"," +
            "\"Tier\":\"Declarative\"," +
            "\"RecoveryMode\":\"FailImmediately\"," +
            "\"DefaultTimeoutSeconds\":10," +
            "\"Capabilities\":[]," +
            "\"Parameters\":[]," +
            "\"Outputs\":[]" +
            "}";

        var manifest = JsonSerializer.Deserialize<NodePackageManifest>(json, Options);

        Assert.NotNull(manifest);
        // Verify default-deny is assigned
        Assert.Equal(NodeSideEffectKind.NonIdempotentSideEffect, manifest.SideEffectKind);
        // Verify default retry policy is assigned
        Assert.NotNull(manifest.RetryPolicy);
        Assert.Equal(3, manifest.RetryPolicy.MaxAttempts);
        Assert.Equal(2, manifest.RetryPolicy.InitialDelaySeconds);
        Assert.Equal(2.0, manifest.RetryPolicy.BackoffRate);
        Assert.True(manifest.RetryPolicy.Jitter);
        Assert.Equal(30, manifest.RetryPolicy.MaxDelaySeconds);
    }

    [Fact]
    public void RetryPolicy_ParsesCustomMaxAttemptsAndConfigurationCorrectly()
    {
        // JSON containing custom retryPolicy
        var json = "{" +
            "\"Id\":\"custom-node\"," +
            "\"Version\":\"1.0.0\"," +
            "\"DisplayName\":\"Custom Node\"," +
            "\"Category\":\"Utility\"," +
            "\"Tier\":\"Declarative\"," +
            "\"SideEffectKind\":\"IdempotentSideEffect\"," +
            "\"RecoveryMode\":\"FailImmediately\"," +
            "\"DefaultTimeoutSeconds\":10," +
            "\"Capabilities\":[]," +
            "\"Parameters\":[]," +
            "\"Outputs\":[]," +
            "\"RetryPolicy\":{" +
                "\"MaxAttempts\":5," +
                "\"InitialDelaySeconds\":5," +
                "\"BackoffRate\":3.5," +
                "\"Jitter\":false," +
                "\"MaxDelaySeconds\":60" +
            "}" +
            "}";

        var manifest = JsonSerializer.Deserialize<NodePackageManifest>(json, Options);

        Assert.NotNull(manifest);
        Assert.Equal(NodeSideEffectKind.IdempotentSideEffect, manifest.SideEffectKind);
        Assert.NotNull(manifest.RetryPolicy);
        Assert.Equal(5, manifest.RetryPolicy.MaxAttempts); // 1-indexed total attempts
        Assert.Equal(5, manifest.RetryPolicy.InitialDelaySeconds);
        Assert.Equal(3.5, manifest.RetryPolicy.BackoffRate);
        Assert.False(manifest.RetryPolicy.Jitter);
        Assert.Equal(60, manifest.RetryPolicy.MaxDelaySeconds);
    }

    [Fact]
    public void TriggerMetadata_ParsesTriggerOnlyCorrectly()
    {
        // JSON specifying TriggerOnly: true
        var json = "{" +
            "\"Id\":\"scheduler\"," +
            "\"Version\":\"1.0.0\"," +
            "\"DisplayName\":\"Scheduler\"," +
            "\"Category\":\"Triggers\"," +
            "\"Tier\":\"Declarative\"," +
            "\"SideEffectKind\":\"IdempotentSideEffect\"," +
            "\"RecoveryMode\":\"FailImmediately\"," +
            "\"DefaultTimeoutSeconds\":0," +
            "\"Capabilities\":[]," +
            "\"Parameters\":[]," +
            "\"Outputs\":[]," +
            "\"TriggerOnly\":true" +
            "}";

        var manifest = JsonSerializer.Deserialize<NodePackageManifest>(json, Options);

        Assert.NotNull(manifest);
        Assert.True(manifest.TriggerOnly);
    }
}
