// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Knotarium.Infrastructure.Security;

/// <summary>
/// Where to persist a self-generated credential key when none is configured. <see cref="Directory"/> is
/// the folder the key file is written to (next to the executable in a productive build); <c>null</c>
/// disables auto-provisioning (e.g. in Development, where a missing key is a configuration error).
/// </summary>
public sealed record CredentialKeyProvisioning(string? Directory);

/// <summary>
/// Resolves the host's 32-byte at-rest credential encryption key from configuration/environment/file.
/// This key lives outside the database — it is what makes a raw DB copy useless on a host with a
/// different key. Shared by <see cref="AesCredentialCipher"/> (credential at-rest crypto) and the backup
/// "server-key" envelope mode (a backup bound to this host, no passphrase).
/// </summary>
public static class CredentialEncryptionKey
{
    public const string ConfigPath = "Security:Credentials:EncryptionKeyBase64";
    public const string EnvVar = "Knotarium_CREDENTIAL_ENCRYPTION_KEY_BASE64";

    /// <summary>The file name a self-generated key is persisted to, inside the provisioning directory.</summary>
    public const string KeyFileName = ".credential-key";

    /// <summary>
    /// Resolves the key from config/env only, throwing when it is missing or malformed. Kept for callers
    /// that must not silently auto-generate a key.
    /// </summary>
    public static byte[] Resolve(IConfiguration configuration) => Resolve(configuration, null);

    /// <summary>
    /// Resolves and validates the key (exactly 32 bytes for AES-256). A configured key (config or env)
    /// always wins. When none is configured and <paramref name="provisioning"/> supplies a directory, a
    /// random key is generated, persisted there, and reused on subsequent runs — so a copy-and-run
    /// productive build works without any manual key setup, while the key still lives outside the DB.
    /// Throws when no key is configured and auto-provisioning is disabled.
    /// </summary>
    public static byte[] Resolve(IConfiguration configuration, CredentialKeyProvisioning? provisioning)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // A configured key wins. Treat an empty/whitespace config value as "not set" so it doesn't shadow
        // the env-var fallback — productive appsettings.json ships this key as an empty string, and a plain
        // `?? env` would stop at that non-null empty value.
        var configured = configuration[ConfigPath];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable(EnvVar);
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Decode(configured);
        }

        if (provisioning?.Directory is { } directory)
        {
            return ProvisionPersistentKey(directory);
        }

        throw new InvalidOperationException(
            $"Missing credential encryption key. Configure {ConfigPath} or {EnvVar}.");
    }

    private static byte[] Decode(string base64)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Credential encryption key must be valid base64.", ex);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException("Credential encryption key must decode to exactly 32 bytes for AES-256.");
        }

        return key;
    }

    /// <summary>
    /// Reads the persisted key from <paramref name="directory"/>, generating and writing a fresh one when
    /// absent. Race-safe: a concurrent first-run that loses the create falls back to reading the winner's key.
    /// </summary>
    private static byte[] ProvisionPersistentKey(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, KeyFileName);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return Decode(existing);
            }
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(Convert.ToBase64String(generated));
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created the file first — use its key so every instance agrees.
            return Decode(File.ReadAllText(path).Trim());
        }

        return generated;
    }
}
