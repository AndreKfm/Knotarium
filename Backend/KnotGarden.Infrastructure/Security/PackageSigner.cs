using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace KnotGarden.Infrastructure.Security;

public sealed record PackageSigningPayload(
    string PackageId,
    string Version,
    string DisplayName,
    string Category,
    string ManifestJson,
    string Source,
    IReadOnlyList<string> Capabilities
);

public static class PackageSigner
{
    public static byte[] DerivePublicKey(byte[] privateKey)
    {
        var privateKeyParameters = new Ed25519PrivateKeyParameters(privateKey, 0);
        return privateKeyParameters.GeneratePublicKey().GetEncoded();
    }

    public static byte[] ComputeDigest(PackageSigningPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var canonicalPayload = CanonicalJsonSerializer.Serialize(payload);
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));
    }

    public static string ComputeDigestHex(PackageSigningPayload payload)
    {
        return Convert.ToHexString(ComputeDigest(payload)).ToLowerInvariant();
    }

    public static string Sign(PackageSigningPayload payload, byte[] privateKey)
    {
        var digest = ComputeDigest(payload);
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(digest, 0, digest.Length);
        var signature = signer.GenerateSignature();
        return Convert.ToBase64String(signature);
    }

    public static bool Verify(PackageSigningPayload payload, string signatureBase64, IEnumerable<string> trustedPublicKeysBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            return false;
        }

        trustedPublicKeysBase64 ??= Array.Empty<string>();

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var digest = ComputeDigest(payload);
        foreach (var keyBase64 in trustedPublicKeysBase64.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            byte[] publicKey;
            try
            {
                publicKey = Convert.FromBase64String(keyBase64);
            }
            catch (FormatException)
            {
                continue;
            }

            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(digest, 0, digest.Length);

            if (verifier.VerifySignature(signature))
            {
                return true;
            }
        }

        return false;
    }
}