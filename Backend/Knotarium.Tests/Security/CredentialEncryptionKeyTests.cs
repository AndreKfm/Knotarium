// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Knotarium.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Knotarium.Tests.Security;

public class CredentialEncryptionKeyTests
{
    private static IConfiguration Config(string? keyValue) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CredentialEncryptionKey.ConfigPath] = keyValue,
            })
            .Build();

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kg-key-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ConfiguredKey_Wins()
    {
        var config = Config("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=");
        var key = CredentialEncryptionKey.Resolve(config, new CredentialKeyProvisioning(TempDir()));
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void EmptyConfigValue_DoesNotShadowProvisioning()
    {
        // Productive appsettings ships this key as an empty string; that must be treated as "not set".
        var dir = TempDir();
        try
        {
            var key = CredentialEncryptionKey.Resolve(Config(""), new CredentialKeyProvisioning(dir));
            Assert.Equal(32, key.Length);
            Assert.True(File.Exists(Path.Combine(dir, CredentialEncryptionKey.KeyFileName)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NoKey_WithProvisioning_GeneratesPersistsAndReuses()
    {
        var dir = TempDir();
        try
        {
            var provisioning = new CredentialKeyProvisioning(dir);
            var first = CredentialEncryptionKey.Resolve(Config(null), provisioning);
            var second = CredentialEncryptionKey.Resolve(Config(null), provisioning);

            Assert.Equal(32, first.Length);
            Assert.Equal(first, second); // same persisted key reused across calls
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NoKey_WithoutProvisioning_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CredentialEncryptionKey.Resolve(Config(null), null));
        Assert.Contains("Missing credential encryption key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cipher_WithProvisioning_EncryptsWithoutConfiguredKey()
    {
        var dir = TempDir();
        try
        {
            var cipher = new AesCredentialCipher(Config(""), new CredentialKeyProvisioning(dir));
            var round = cipher.Decrypt(cipher.Encrypt("super-secret"));
            Assert.Equal("super-secret", round);
        }
        finally { Directory.Delete(dir, true); }
    }
}
