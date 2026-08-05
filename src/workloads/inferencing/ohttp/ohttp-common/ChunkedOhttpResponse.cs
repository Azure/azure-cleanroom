// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;

namespace OhttpCommon;

/// <summary>
/// Server-side chunked OHTTP response encryptor (draft-ohai-chunked-ohttp-01 §6.2).
/// Encrypts response chunks incrementally using per-chunk AEAD with counter-based nonces.
/// </summary>
public class ChunkedResponseEncryptor
{
    private const int Nk = 32;  // AES-256-GCM key length.
    private const int Nn = 12;  // AES-256-GCM nonce length.
    private const int EntropyLen = 32;  // max(Nn, Nk).

    private static readonly byte[] FinalAad = "final"u8.ToArray();

    private readonly byte[] aeadKey;
    private readonly byte[] aeadNonce;
    private ulong counter;

    public ChunkedResponseEncryptor(byte[] exporterSecret, byte[] enc)
    {
        this.ResponseNonce = RandomNumberGenerator.GetBytes(EntropyLen);
        this.counter = 0;

        // Derive AEAD key and nonce per draft-ohai-chunked-ohttp-01 §6.2.
        // secret = context.Export("message/bhttp chunked response", entropy_len)
        // salt = concat(enc, response_nonce)
        // prk = Extract(salt, secret)
        // aead_key = Expand(prk, "key", Nk)
        // aead_nonce = Expand(prk, "nonce", Nn)
        byte[] secret = HkdfExpandLabel(
            exporterSecret,
            "message/bhttp chunked response"u8.ToArray(),
            [],
            EntropyLen);

        byte[] salt = Concat(enc, this.ResponseNonce);
        byte[] prk = HkdfExtract(salt, secret);
        this.aeadKey = HkdfExpand(prk, "key"u8.ToArray(), Nk);
        this.aeadNonce = HkdfExpand(prk, "nonce"u8.ToArray(), Nn);
    }

    /// <summary>
    /// Gets the response nonce to write as the first bytes of the chunked response.
    /// Length is max(Nn, Nk) = 32 bytes.
    /// </summary>
    public byte[] ResponseNonce { get; }

    /// <summary>
    /// Encrypts a non-final chunk. The caller is responsible for writing the
    /// varint length prefix before the sealed chunk.
    /// </summary>
    /// <param name="plaintext">The plaintext chunk to encrypt.</param>
    /// <returns>The AEAD-sealed ciphertext (includes authentication tag).</returns>
    public byte[] SealChunk(byte[] plaintext)
    {
        byte[] nonce = Hpke.ComputeNonce(this.aeadNonce, this.counter);
        byte[] ct = Hpke.AeadSeal(this.aeadKey, nonce, [], plaintext);
        this.counter++;
        return ct;
    }

    /// <summary>
    /// Encrypts the final chunk with "final" AAD sentinel. The caller should
    /// write a varint(0) prefix before this sealed chunk.
    /// </summary>
    /// <param name="plaintext">The plaintext of the final chunk.</param>
    /// <returns>The AEAD-sealed ciphertext with "final" AAD.</returns>
    public byte[] SealFinalChunk(byte[] plaintext)
    {
        byte[] nonce = Hpke.ComputeNonce(this.aeadNonce, this.counter);
        byte[] ct = Hpke.AeadSeal(this.aeadKey, nonce, FinalAad, plaintext);
        this.counter++;
        return ct;
    }

    private static byte[] HkdfExpandLabel(
        byte[] secret,
        byte[] label,
        byte[] context,
        int length)
    {
        byte[] lengthBytes = [(byte)(length >> 8), (byte)(length & 0xFF)];
        byte[] labeledInfo = Concat(lengthBytes, label, context);
        return HkdfExpand(secret, labeledInfo, length);
    }

    private static byte[] HkdfExtract(byte[] salt, byte[] ikm)
    {
        if (salt.Length == 0)
        {
            salt = new byte[48]; // SHA-384 hash length.
        }

        return HMACSHA384.HashData(salt, ikm);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        int hashLen = 48; // SHA-384.
        int n = (int)Math.Ceiling((double)length / hashLen);

        byte[] okm = [];
        byte[] t = [];

        for (int i = 1; i <= n; i++)
        {
            byte[] input = Concat(t, info, [(byte)i]);
            t = HMACSHA384.HashData(prk, input);
            okm = Concat(okm, t);
        }

        return okm[..length];
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        int totalLength = 0;
        foreach (byte[] a in arrays)
        {
            totalLength += a.Length;
        }

        byte[] result = new byte[totalLength];
        int offset = 0;
        foreach (byte[] a in arrays)
        {
            Buffer.BlockCopy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }

        return result;
    }
}

/// <summary>
/// Client-side chunked OHTTP response decryptor (draft-ohai-chunked-ohttp-01 §6.2).
/// Decrypts response chunks incrementally using per-chunk AEAD with counter-based nonces.
/// </summary>
public class ChunkedResponseDecryptor
{
    private const int Nk = 32;
    private const int Nn = 12;
    private const int EntropyLen = 32;

    private static readonly byte[] FinalAad = "final"u8.ToArray();

    private readonly byte[] aeadKey;
    private readonly byte[] aeadNonce;
    private ulong counter;

    public ChunkedResponseDecryptor(
        byte[] exporterSecret,
        byte[] enc,
        byte[] responseNonce)
    {
        this.counter = 0;

        byte[] secret = HkdfExpandLabel(
            exporterSecret,
            "message/bhttp chunked response"u8.ToArray(),
            [],
            EntropyLen);

        byte[] salt = Concat(enc, responseNonce);
        byte[] prk = HkdfExtract(salt, secret);
        this.aeadKey = HkdfExpand(prk, "key"u8.ToArray(), Nk);
        this.aeadNonce = HkdfExpand(prk, "nonce"u8.ToArray(), Nn);
    }

    /// <summary>
    /// Decrypts a non-final chunk (empty AAD).
    /// </summary>
    /// <param name="ciphertext">The AEAD-sealed ciphertext.</param>
    /// <returns>The decrypted plaintext.</returns>
    public byte[] OpenChunk(byte[] ciphertext)
    {
        byte[] nonce = Hpke.ComputeNonce(this.aeadNonce, this.counter);
        byte[] plaintext = Hpke.AeadOpen(this.aeadKey, nonce, [], ciphertext);
        this.counter++;
        return plaintext;
    }

    /// <summary>
    /// Decrypts the final chunk ("final" AAD sentinel).
    /// </summary>
    /// <param name="ciphertext">The AEAD-sealed ciphertext.</param>
    /// <returns>The decrypted plaintext.</returns>
    public byte[] OpenFinalChunk(byte[] ciphertext)
    {
        byte[] nonce = Hpke.ComputeNonce(this.aeadNonce, this.counter);
        byte[] plaintext = Hpke.AeadOpen(this.aeadKey, nonce, FinalAad, ciphertext);
        this.counter++;
        return plaintext;
    }

    private static byte[] HkdfExpandLabel(
        byte[] secret,
        byte[] label,
        byte[] context,
        int length)
    {
        byte[] lengthBytes = [(byte)(length >> 8), (byte)(length & 0xFF)];
        byte[] labeledInfo = Concat(lengthBytes, label, context);
        return HkdfExpand(secret, labeledInfo, length);
    }

    private static byte[] HkdfExtract(byte[] salt, byte[] ikm)
    {
        if (salt.Length == 0)
        {
            salt = new byte[48];
        }

        return HMACSHA384.HashData(salt, ikm);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        int hashLen = 48;
        int n = (int)Math.Ceiling((double)length / hashLen);

        byte[] okm = [];
        byte[] t = [];

        for (int i = 1; i <= n; i++)
        {
            byte[] input = Concat(t, info, [(byte)i]);
            t = HMACSHA384.HashData(prk, input);
            okm = Concat(okm, t);
        }

        return okm[..length];
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        int totalLength = 0;
        foreach (byte[] a in arrays)
        {
            totalLength += a.Length;
        }

        byte[] result = new byte[totalLength];
        int offset = 0;
        foreach (byte[] a in arrays)
        {
            Buffer.BlockCopy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }

        return result;
    }
}
