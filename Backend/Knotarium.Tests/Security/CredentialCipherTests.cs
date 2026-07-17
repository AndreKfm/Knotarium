// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Knotarium.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Knotarium.Tests.Security;

public class CredentialCipherTests
{
    private static AesCredentialCipher CreateCipher()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Credentials:EncryptionKeyBase64"] = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA="
            })
            .Build();

        return new AesCredentialCipher(config);
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsPlaintext()
    {
        var cipher = CreateCipher();

        var encrypted = cipher.Encrypt("super-secret");
        var decrypted = cipher.Decrypt(encrypted);

        Assert.StartsWith("v1:", encrypted, StringComparison.Ordinal);
        Assert.Equal("super-secret", decrypted);
    }

    [Fact]
    public void Decrypt_LegacyPlaintext_ReturnsInput()
    {
        var cipher = CreateCipher();
        var decrypted = cipher.Decrypt("legacy-plaintext-value");
        Assert.Equal("legacy-plaintext-value", decrypted);
    }

    [Fact]
    public void ConstructingWithoutKey_DoesNotThrowUntilEncryptionIsUsed()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var cipher = new AesCredentialCipher(config);

        Assert.Equal("legacy-plaintext-value", cipher.Decrypt("legacy-plaintext-value"));
        var ex = Assert.Throws<InvalidOperationException>(() => cipher.Encrypt("super-secret"));
        Assert.Contains("Missing credential encryption key", ex.Message, StringComparison.Ordinal);
    }
}