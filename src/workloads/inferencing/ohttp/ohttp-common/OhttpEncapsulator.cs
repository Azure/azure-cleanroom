// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OhttpCommon;

/// <summary>
/// Client-side OHTTP encapsulation (RFC 9458 §4).
/// Encapsulates a Binary HTTP request and decapsulates the response.
/// </summary>
public class OhttpEncapsulator
{
    private readonly KeyConfig keyConfig;
    private HpkeSenderContext? senderContext;

    public OhttpEncapsulator(KeyConfig keyConfig)
    {
        this.keyConfig = keyConfig;
    }

    /// <summary>
    /// Encapsulates a Binary HTTP request into an OHTTP request message.
    /// </summary>
    /// <param name="binaryHttpRequest">The Binary HTTP request bytes to encapsulate.</param>
    /// <returns>
    /// The OHTTP-encapsulated message (Content-Type: message/ohttp-req).
    /// </returns>
    public byte[] EncapsulateRequest(byte[] binaryHttpRequest)
    {
        // Build HPKE info for OHTTP request:
        //   info = "message/bhttp request" || 0x00 || keyId || kemId || kdfId || aeadId
        byte[] info = BuildRequestInfo(this.keyConfig);

        // Setup HPKE sender context.
        this.senderContext = Hpke.SetupBaseSender(this.keyConfig.PublicKey, info);

        // AAD for the request is empty per RFC 9458.
        byte[] aad = BuildRequestAad(this.keyConfig);

        // Encrypt the Binary HTTP request.
        byte[] ciphertext = this.senderContext.Seal(aad, binaryHttpRequest);

        // Wire format: keyId (1) || kemId (2) || enc (Nenc) || ciphertext.
        using var ms = new MemoryStream();
        ms.WriteByte(this.keyConfig.KeyId);
        byte[] kemBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(kemBytes, this.keyConfig.KemId);
        ms.Write(kemBytes);
        ms.Write(this.senderContext.Enc);
        ms.Write(ciphertext);

        return ms.ToArray();
    }

    /// <summary>
    /// Decapsulates an OHTTP response to recover the Binary HTTP response.
    /// </summary>
    /// <param name="ohttpResponse">The OHTTP response bytes.</param>
    /// <returns>The decrypted Binary HTTP response bytes.</returns>
    public byte[] DecapsulateResponse(byte[] ohttpResponse)
    {
        if (this.senderContext == null)
        {
            throw new InvalidOperationException(
                "Must call EncapsulateRequest before DecapsulateResponse.");
        }

        // Response wire format: nonce (Nn=12) || ciphertext.
        int nonceLen = 12; // AES-GCM nonce length.
        byte[] responseNonce = ohttpResponse[..nonceLen];
        byte[] ct = ohttpResponse[nonceLen..];

        // Derive response key and nonce from the sender's exporter secret.
        // secret = context.Export("message/bhttp response", Nk + Nn)
        //   where Nk=32, Nn=12 for AES-256-GCM.
        byte[] exportContext = BuildResponseExportContext(responseNonce);
        byte[] exported = HkdfExpandLabel(
            this.senderContext.ExporterSecret,
            "message/bhttp response"u8.ToArray(),
            exportContext,
            32 + 12); // Nk + Nn.

        byte[] responseKey = exported[..32];
        byte[] responseNonceXored = exported[32..];

        // XOR the response nonce with the derived nonce.
        for (int i = 0; i < responseNonceXored.Length; i++)
        {
            responseNonceXored[i] ^= responseNonce[i];
        }

        // AAD is empty for response.
        byte[] plaintext = Hpke.AeadOpen(responseKey, responseNonceXored, [], ct);
        return plaintext;
    }

    /// <summary>
    /// Creates a chunked response decryptor for streaming OHTTP responses
    /// (draft-ohai-chunked-ohttp-01 §6.2). Must be called after EncapsulateRequest.
    /// </summary>
    /// <param name="responseNonce">
    /// The response nonce (32 bytes) read from the start of the chunked response.
    /// </param>
    /// <returns>A chunked response decryptor.</returns>
    public ChunkedResponseDecryptor CreateChunkedResponseDecryptor(byte[] responseNonce)
    {
        if (this.senderContext == null)
        {
            throw new InvalidOperationException(
                "Must call EncapsulateRequest before CreateChunkedResponseDecryptor.");
        }

        return new ChunkedResponseDecryptor(
            this.senderContext.ExporterSecret,
            this.senderContext.Enc,
            responseNonce);
    }

    private static byte[] BuildRequestInfo(KeyConfig keyConfig)
    {
        // "message/bhttp request" || 0x00 || keyId (1) || kemId (2) || kdfId (2) || aeadId (2).
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
        // AAD = keyId (1) || kemId (2) || kdfId (2) || aeadId (2).
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

    private static byte[] BuildResponseExportContext(byte[] responseNonce)
    {
        return responseNonce;
    }

    private static byte[] HkdfExpandLabel(byte[] secret, byte[] label, byte[] context, int length)
    {
        // Labeled expand using the exporter secret:
        //   prk = secret
        //   info = I2OSP(length, 2) || "HPKE-v1" || suite_id || "sec" || context
        // Simplified: we use HKDF-Expand directly with the label+context as info.
        byte[] lengthBytes = [(byte)(length >> 8), (byte)(length & 0xFF)];
        byte[] labeledInfo = Concat(lengthBytes, label, context);
        return HkdfExpand(secret, labeledInfo, length);
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
