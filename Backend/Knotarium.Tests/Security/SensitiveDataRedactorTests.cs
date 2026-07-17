// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text.Json;
using Knotarium.Core.Domain;
using Xunit;

namespace Knotarium.Tests.Security;

public class SensitiveDataRedactorTests
{
    [Fact]
    public void Redact_masks_sensitive_keys_keeps_others()
    {
        var source = new Dictionary<string, object>
        {
            ["apiKey"] = "sk-super-secret",
            ["password"] = "hunter2",
            ["Authorization"] = "Bearer abc",
            ["userName"] = "alice",
            ["count"] = 3,
        };

        var result = SensitiveDataRedactor.Redact(source);

        Assert.Equal(SensitiveDataRedactor.Mask, result["apiKey"]);
        Assert.Equal(SensitiveDataRedactor.Mask, result["password"]);
        Assert.Equal(SensitiveDataRedactor.Mask, result["Authorization"]);
        Assert.Equal("alice", result["userName"]);
        Assert.Equal(3, result["count"]);
    }

    [Theory]
    [InlineData("passwd")]
    [InlineData("db_pwd")]
    [InlineData("passphrase")]
    [InlineData("x-api-key")]
    [InlineData("privateKey")]
    [InlineData("private_key")]
    [InlineData("bearerToken")]
    [InlineData("Cookie")]
    public void IsSensitiveKey_matches_broadened_secret_indicators(string key)
    {
        Assert.True(SensitiveDataRedactor.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("passenger")]   // must not match a bare "pass"
    [InlineData("author")]      // must not match a bare "auth"
    [InlineData("credentialRef")] // a slot reference is not a secret value
    [InlineData("primaryKey")]  // must not match a bare "key"
    [InlineData("endpoint")]
    [InlineData("region")]
    public void IsSensitiveKey_does_not_over_match_benign_keys(string key)
    {
        Assert.False(SensitiveDataRedactor.IsSensitiveKey(key));
    }

    [Fact]
    public void Redact_recurses_into_nested_json_objects()
    {
        var nested = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{ "config": { "token": "t0ken", "region": "eu" }, "name": "svc" }""")!;

        var result = SensitiveDataRedactor.Redact(nested);

        var config = Assert.IsType<Dictionary<string, object>>(result["config"]);
        Assert.Equal(SensitiveDataRedactor.Mask, config["token"]);
        Assert.Equal("eu", ((JsonElement)config["region"]).GetString());
        Assert.Equal("svc", ((JsonElement)result["name"]).GetString());
    }

    [Fact]
    public void RedactJsonString_masks_and_roundtrips()
    {
        var redacted = SensitiveDataRedactor.RedactJsonString("""{ "secretValue": "x", "keep": 1 }""");
        Assert.NotNull(redacted);
        using var doc = JsonDocument.Parse(redacted!);
        Assert.Equal("***", doc.RootElement.GetProperty("secretValue").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("keep").GetInt32());
    }

    [Fact]
    public void RedactJsonString_returns_input_when_not_json()
    {
        Assert.Equal("not json", SensitiveDataRedactor.RedactJsonString("not json"));
        Assert.Null(SensitiveDataRedactor.RedactJsonString(null));
    }
}
