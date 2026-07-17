// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Security.Cryptography;
using System.Text;
using Knotarium.Core.Contracts;
using Microsoft.Extensions.Configuration;

namespace Knotarium.Infrastructure.Security;

public class AesCredentialCipher : ICredentialCipher
{
    private const string VersionPrefix = "v1:";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly IConfiguration _configuration;
    private readonly CredentialKeyProvisioning? _provisioning;
    private byte[]? _key;

    public AesCredentialCipher(IConfiguration configuration, CredentialKeyProvisioning? provisioning = null)
    {
        _configuration = configuration;
        _provisioning = provisioning;
    }

    public string Encrypt(string plainText)
    {
        if (plainText == null)
        {
            throw new ArgumentNullException(nameof(plainText));
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(GetKey(), TagLength);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var payload = new byte[NonceLength + TagLength + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceLength);
        Buffer.BlockCopy(tag, 0, payload, NonceLength, TagLength);
        Buffer.BlockCopy(cipherBytes, 0, payload, NonceLength + TagLength, cipherBytes.Length);

        return VersionPrefix + Convert.ToBase64String(payload);
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return string.Empty;
        }

        // Backward compatibility for pre-E2 plaintext rows.
        if (!cipherText.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return cipherText;
        }

        var payload = Convert.FromBase64String(cipherText.Substring(VersionPrefix.Length));
        if (payload.Length < NonceLength + TagLength)
        {
            throw new InvalidOperationException("Credential payload is invalid or truncated.");
        }

        var nonce = payload.AsSpan(0, NonceLength).ToArray();
        var tag = payload.AsSpan(NonceLength, TagLength).ToArray();
        var cipherBytes = payload.AsSpan(NonceLength + TagLength).ToArray();
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(GetKey(), TagLength);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private byte[] GetKey()
    {
        _key ??= CredentialEncryptionKey.Resolve(_configuration, _provisioning);
        return _key;
    }
}