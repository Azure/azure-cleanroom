// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Azure.Security.KeyVault.Keys.Cryptography;

// Based off:
// https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/keyvault/Azure.Security.KeyVault.Keys/src/Cryptography/RSAKeyVault.cs
// https://github.com/novotnyllc/RSAKeyVaultProvider/blob/main/RSAKeyVaultProvider/ECDsaKeyVault.cs
public class ECDsaKeyVault : ECDsa
{
    private ECDsa publicKey;
    private CryptographyClient cryptographyClient;

    public ECDsaKeyVault(X509Certificate2 cert, CryptographyClient cryptographyClient)
    {
        string algorithm = cert.GetKeyAlgorithm();
        string ecAlgo = "1.2.840.10045.2.1";
        if (algorithm != ecAlgo)
        {
            throw new NotSupportedException(
                $"Certificate algorithm '{algorithm}' is not supported.");
        }

        this.publicKey = cert.GetECDsaPublicKey() ??
            throw new ArgumentException("Certificate does not contain ECDsa public key.");
        this.cryptographyClient = cryptographyClient;
        this.KeySizeValue = this.publicKey.KeySize;
    }

    public override byte[] SignHash(byte[] hash)
    {
        ValidateKeyDigestCombination(this.KeySize, hash.Length);

        // We know from ValidateKeyDigestCombination that the key size and hash size are matched up
        // according to RFC 7518 Sect. 3.1.
        SignatureAlgorithm algorithm;
        if (this.KeySize == 256)
        {
            algorithm = Cryptography.SignatureAlgorithm.ES256;
        }
        else if (this.KeySize == 384)
        {
            algorithm = Cryptography.SignatureAlgorithm.ES384;
        }
        else if (this.KeySize == 521)
        {
            //ES512 uses nistP521.
            algorithm = Cryptography.SignatureAlgorithm.ES512;
        }
        else
        {
            throw new ArgumentException(
                "Digest length is not valid for the key size.",
                nameof(hash));
        }

        var sigResult = this.cryptographyClient.Sign(algorithm, hash);
        return sigResult.Signature;
    }

    public override bool VerifyHash(byte[] hash, byte[] signature)
    {
        ValidateKeyDigestCombination(this.KeySize, hash.Length);
        return this.publicKey.VerifyHash(hash, signature);
    }

    public override ECParameters ExportParameters(bool includePrivateParameters)
    {
        return this.publicKey.ExportParameters(includePrivateParameters);
    }

    public override ECParameters ExportExplicitParameters(bool includePrivateParameters)
    {
        return this.publicKey.ExportExplicitParameters(includePrivateParameters);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.publicKey?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override byte[] HashData(
    byte[] data,
    int offset,
    int count,
    HashAlgorithmName hashAlgorithm)
    {
        ValidateKeyDigestCombination(this.KeySize, hashAlgorithm);
        using HashAlgorithm algorithm = Create(hashAlgorithm);
        return algorithm.ComputeHash(data, offset, count);
    }

    private static void ValidateKeyDigestCombination(int keySizeBits, int digestSizeBytes)
    {
        if ((keySizeBits == 256 && digestSizeBytes == 32) ||
            (keySizeBits == 384 && digestSizeBytes == 48) ||
            (keySizeBits == 521 && digestSizeBytes == 64))
        {
            return;
        }

        throw new NotSupportedException(
            $"The key size '{keySizeBits}' is not valid for digest of size '{digestSizeBytes}' " +
            $"bytes.");
    }

    private static void ValidateKeyDigestCombination(
        int keySizeBits,
        HashAlgorithmName hashAlgorithmName)
    {
        if (hashAlgorithmName != HashAlgorithmName.SHA256 &&
            hashAlgorithmName != HashAlgorithmName.SHA384 &&
            hashAlgorithmName != HashAlgorithmName.SHA512)
        {
            throw new NotSupportedException("The specified algorithm is not supported.");
        }

        if ((keySizeBits == 256 && hashAlgorithmName == HashAlgorithmName.SHA256) ||
            (keySizeBits == 384 && hashAlgorithmName == HashAlgorithmName.SHA384) ||
            (keySizeBits == 521 && hashAlgorithmName == HashAlgorithmName.SHA512))
        {
            return;
        }

        throw new NotSupportedException(
            $"The key size '{keySizeBits}' is not valid for digest algorithm " +
            $"'{hashAlgorithmName}'.");
    }

    private static HashAlgorithm Create(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA256)
        {
            return SHA256.Create();
        }

        if (algorithm == HashAlgorithmName.SHA384)
        {
            return SHA384.Create();
        }

        if (algorithm == HashAlgorithmName.SHA512)
        {
            return SHA512.Create();
        }

        throw new NotSupportedException($"{algorithm} is not supported.");
    }
}