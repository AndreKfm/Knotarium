using System;
using System.Security.Cryptography;
using System.Text;
using KnotGarden.Core.Contracts;

namespace KnotGarden.Infrastructure.Security;

public class CorrelationTokenCrypto : ICorrelationTokenCrypto
{
    public string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        // Using built-in high-performance .NET 9+ Base64Url helper
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    public string HashToken(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new ArgumentException("Token cannot be null or empty.", nameof(rawToken));
        }

        var bytes = Encoding.UTF8.GetBytes(rawToken);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
