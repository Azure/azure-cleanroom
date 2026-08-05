// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OhttpCommon;

/// <summary>
/// Server-side OHTTP decapsulation (RFC 9458 §4).
/// Decapsulates an OHTTP request and encapsulates the response.
/// </summary>
public class OhttpDecapsulator
{
    private readonly byte[] privateKey;
    private readonly byte[] publicKey;
    private readonly KeyConfig keyConfig;
    private HpkeReceiverContext? receiverContext;
    private byte[]? enc;

    public OhttpDecapsulator(KeyConfig keyConfig, byte[] privateKey)
    {
        this.keyConfig = keyConfig;
        this.privateKey = privateKey;
        this.publicKey = keyConfig.PublicKey;
    }

    /// <summary>
    /// Decapsulates an OHTTP request to recover the Binary HTTP request.
    /// </summary>
    /// <param name="ohttpRequest">The OHTTP request bytes.</param>
    /// <returns>The decrypted Binary HTTP request bytes.</returns>
    public byte[] DecapsulateRequest(byte[] ohttpRequest)
    {
        int offset = 0;

        // Parse the OHTTP request header.
        byte keyId = ohttpRequest[offset++];
        if (keyId != this.keyConfig.KeyId)
        {
            throw new CryptographicException(
                $"Unknown Key ID: {keyId}. Expected: {this.keyConfig.KeyId}.");
        }

        ushort kemId = BinaryPrimitives.ReadUInt16BigEndian(ohttpRequest.AsSpan(offset));
        offset += 2;

        if (kemId != this.keyConfig.KemId)
        {
            throw new CryptographicException(
                $"Unsupported KEM ID: 0x{kemId:X4}. Expected: 0x{this.keyConfig.KemId:X4}.");
        }

        // enc is 97 bytes for P-384 (uncompressed point).
        int encLen = 97;
        byte[] enc = ohttpRequest[offset..(offset + encLen)];
        offset += encLen;

        // Store enc for use in chunked response key derivation.
        this.enc = enc;

        byte[] ciphertext = ohttpRequest[offset..];

        // Build info for HPKE receiver.
        byte[] info = BuildRequestInfo(this.keyConfig);

        // Setup HPKE receiver context.
        this.receiverContext = Hpke.SetupBaseReceiver(
            enc,
            this.privateKey,
            this.publicKey,
            info);

        // AAD for the request.
        byte[] aad = BuildRequestAad(this.keyConfig);

        // Decrypt.
        return this.receiverContext.Open(aad, ciphertext);
    }

    /// <summary>
    /// Encapsulates a Binary HTTP response into an OHTTP response.
    /// </summary>
    /// <param name="binaryHttpResponse">The Binary HTTP response bytes.</param>
    /// <returns>The OHTTP-encapsulated response bytes.</returns>
    public byte[] EncapsulateResponse(byte[] binaryHttpResponse)
    {
        if (this.receiverContext == null)
        {
            throw new InvalidOperationException(
                "Must call DecapsulateRequest before EncapsulateResponse.");
        }

        // Generate a random response nonce (Nn = 12 bytes for AES-GCM).
        byte[] responseNonce = RandomNumberGenerator.GetBytes(12);

        // Derive response key and nonce.
        byte[] exportContext = responseNonce;
        byte[] exported = HkdfExpandLabel(
            this.receiverContext.ExporterSecret,
            "message/bhttp response"u8.ToArray(),
            exportContext,
            32 + 12); // Nk + Nn.

        byte[] responseKey = exported[..32];
        byte[] responseNonceXored = exported[32..];

        for (int i = 0; i < responseNonceXored.Length; i++)
        {
            responseNonceXored[i] ^= responseNonce[i];
        }

        // Encrypt with empty AAD.
        byte[] ciphertext = Hpke.AeadSeal(responseKey, responseNonceXored, [], binaryHttpResponse);

        // Wire format: nonce (12) || ciphertext.
        byte[] result = new byte[responseNonce.Length + ciphertext.Length];
        Buffer.BlockCopy(responseNonce, 0, result, 0, responseNonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, responseNonce.Length, ciphertext.Length);

        return result;
    }

    /// <summary>
    /// Creates a chunked response encryptor for streaming OHTTP responses
    /// (draft-ohai-chunked-ohttp-01 §6.2). Must be called after DecapsulateRequest.
    /// </summary>
    /// <returns>A chunked response encryptor.</returns>
    public ChunkedResponseEncryptor CreateChunkedResponseEncryptor()
    {
        if (this.receiverContext == null || this.enc == null)
        {
            throw new InvalidOperationException(
                "Must call DecapsulateRequest before CreateChunkedResponseEncryptor.");
        }

        return new ChunkedResponseEncryptor(this.receiverContext.ExporterSecret, this.enc);
    }

    private static byte[] BuildRequestInfo(KeyConfig keyConfig)
    {
        byte[] label = "message/bhttp request"u8.ToArray();
        using var ms = new MemoryStream();
        ms.Write(label);
        ms.WriteByte(0x00);
        ms.WriteByte(keyConfig.KeyId);

        byte[] buf = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, keyConfig.KemId);
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, keyConfig.Algorithms[0].KdfId);
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, keyConfig.Algorithms[0].AeadId);
        ms.Write(buf);

        return ms.ToArray();
    }

    private static byte[] BuildRequestAad(KeyConfig keyConfig)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(keyConfig.KeyId);

        byte[] buf = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, keyConfig.KemId);
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, keyConfig.Algorithms[0].KdfId);
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, keyConfig.Algorithms[0].AeadId);
        ms.Write(buf);

        return ms.ToArray();
    }

    private static byte[] HkdfExpandLabel(byte[] secret, byte[] label, byte[] context, int length)
    {
        byte[] lengthBytes = [(byte)(length >> 8), (byte)(length & 0xFF)];
        byte[] labeledInfo = Concat(lengthBytes, label, context);
        return HkdfExpand(secret, labeledInfo, length);
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
