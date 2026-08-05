// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;

namespace OhttpCommon;

/// <summary>
/// HPKE (RFC 9180) implementation using DHKEM(P-384, HKDF-SHA384) / AES-256-GCM.
/// Uses native .NET cryptography — no external dependencies.
/// </summary>
public static class Hpke
{
    private const int Nk = 32; // AES-256-GCM key length.
    private const int Nn = 12; // AES-256-GCM nonce length.
    private const int Nh = 48; // SHA-384 hash length.
    private const int NSecret = 48; // KEM shared secret length.
    private const int NEnc = 97; // P-384 uncompressed public key length (0x04 || x || y).
    private const int AeadTagLength = 16; // AES-GCM tag length.
    private const int CoordinateSize = 48; // P-384 coordinate size in bytes.

    // Suite ID for KEM: "KEM" || I2OSP(0x0011, 2).
    private static readonly byte[] KemSuiteId = BuildSuiteId(
        "KEM"u8.ToArray(),
        OhttpConstants.KemP384HkdfSha384);

    // Suite ID for HPKE: "HPKE" || I2OSP(kem, 2) || I2OSP(kdf, 2) || I2OSP(aead, 2).
    private static readonly byte[] HpkeSuiteId = BuildHpkeSuiteId();

    /// <summary>
    /// Generates a P-384 ECDH key pair.
    /// </summary>
    /// <returns>An HPKE key pair with public and private key bytes.</returns>
    public static HpkeKeyPair GenerateKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        ECParameters parameters = ecdh.ExportParameters(includePrivateParameters: true);

        // Public key: uncompressed point (0x04 || X || Y).
        byte[] publicKey = new byte[NEnc];
        publicKey[0] = 0x04;
        Buffer.BlockCopy(parameters.Q.X!, 0, publicKey, 1, CoordinateSize);
        Buffer.BlockCopy(
            parameters.Q.Y!,
            0,
            publicKey,
            1 + CoordinateSize,
            CoordinateSize);

        return new HpkeKeyPair(publicKey, parameters.D!);
    }

    /// <summary>
    /// HPKE SetupBaseS (sender): encapsulate a shared secret and derive sender context.
    /// </summary>
    /// <param name="recipientPublicKey">The recipient's P-384 public key (uncompressed).</param>
    /// <param name="info">The info parameter for key schedule.</param>
    /// <returns>A sender context containing enc, key, base nonce, and exporter secret.</returns>
    public static HpkeSenderContext SetupBaseSender(
        byte[] recipientPublicKey,
        byte[] info)
    {
        // Generate ephemeral key pair.
        HpkeKeyPair ephemeral = GenerateKeyPair();

        // Encapsulate: enc = ephemeral public key, shared_secret = DH(ephemeral, recipient).
        byte[] sharedSecret = ComputeSharedSecret(
            ephemeral.PrivateKey,
            ephemeral.PublicKey,
            recipientPublicKey);

        // KEM context: enc || pkR.
        byte[] kemContext = Concat(ephemeral.PublicKey, recipientPublicKey);

        // Extract and expand to derive the shared secret.
        byte[] extractedSecret = ExtractAndExpand(sharedSecret, kemContext);

        // Key schedule.
        (byte[] key, byte[] baseNonce, byte[] exporterSecret) =
            KeySchedule(extractedSecret, info);

        return new HpkeSenderContext(
            ephemeral.PublicKey,
            key,
            baseNonce,
            exporterSecret,
            sequenceNumber: 0);
    }

    /// <summary>
    /// HPKE SetupBaseR (receiver): decapsulate and derive receiver context.
    /// </summary>
    /// <param name="enc">The encapsulated key from the sender.</param>
    /// <param name="recipientPrivateKey">The recipient's P-384 private key (D parameter).</param>
    /// <param name="recipientPublicKey">The recipient's P-384 public key (uncompressed).</param>
    /// <param name="info">The info parameter for key schedule.</param>
    /// <returns>A receiver context containing key, base nonce, and exporter secret.</returns>
    public static HpkeReceiverContext SetupBaseReceiver(
        byte[] enc,
        byte[] recipientPrivateKey,
        byte[] recipientPublicKey,
        byte[] info)
    {
        // Decapsulate: shared_secret = DH(recipientPrivate, enc).
        byte[] sharedSecret = ComputeSharedSecret(recipientPrivateKey, recipientPublicKey, enc);

        // KEM context: enc || pkR.
        byte[] kemContext = Concat(enc, recipientPublicKey);

        byte[] extractedSecret = ExtractAndExpand(sharedSecret, kemContext);

        (byte[] key, byte[] baseNonce, byte[] exporterSecret) =
            KeySchedule(extractedSecret, info);

        return new HpkeReceiverContext(
            key,
            baseNonce,
            exporterSecret,
            sequenceNumber: 0);
    }

    /// <summary>
    /// AEAD seal (encrypt) using AES-256-GCM.
    /// </summary>
    /// <param name="key">The AES-256 key.</param>
    /// <param name="nonce">The 12-byte nonce.</param>
    /// <param name="aad">Associated data.</param>
    /// <param name="plaintext">The plaintext to encrypt.</param>
    /// <returns>The ciphertext with appended authentication tag.</returns>
    public static byte[] AeadSeal(byte[] key, byte[] nonce, byte[] aad, byte[] plaintext)
    {
        using var aes = new AesGcm(key, AeadTagLength);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[AeadTagLength];
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        // Return ciphertext || tag.
        byte[] output = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, output, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, ciphertext.Length, tag.Length);
        return output;
    }

    /// <summary>
    /// AEAD open (decrypt) using AES-256-GCM.
    /// </summary>
    /// <param name="key">The AES-256 key.</param>
    /// <param name="nonce">The 12-byte nonce.</param>
    /// <param name="aad">Associated data.</param>
    /// <param name="ciphertext">The ciphertext with appended tag to decrypt.</param>
    /// <returns>The decrypted plaintext.</returns>
    public static byte[] AeadOpen(byte[] key, byte[] nonce, byte[] aad, byte[] ciphertext)
    {
        int ctLen = ciphertext.Length - AeadTagLength;
        byte[] ct = ciphertext[..ctLen];
        byte[] tag = ciphertext[ctLen..];

        using var aes = new AesGcm(key, AeadTagLength);
        byte[] plaintext = new byte[ctLen];
        aes.Decrypt(nonce, ct, tag, plaintext, aad);
        return plaintext;
    }

    /// <summary>
    /// Compute the nonce for a given sequence number: base_nonce XOR seq.
    /// </summary>
    /// <param name="baseNonce">The base nonce from the HPKE context.</param>
    /// <param name="sequenceNumber">The current sequence number.</param>
    /// <returns>The computed nonce.</returns>
    public static byte[] ComputeNonce(byte[] baseNonce, ulong sequenceNumber)
    {
        byte[] seqBytes = new byte[Nn];
        for (int i = 0; i < 8; i++)
        {
            seqBytes[Nn - 1 - i] = (byte)(sequenceNumber >> (8 * i));
        }

        byte[] nonce = new byte[Nn];
        for (int i = 0; i < Nn; i++)
        {
            nonce[i] = (byte)(baseNonce[i] ^ seqBytes[i]);
        }

        return nonce;
    }

    private static byte[] ComputeSharedSecret(
        byte[] privateKey,
        byte[] ownPublicKey,
        byte[] peerPublicKey)
    {
        using var ecdh = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP384,
            D = privateKey,
            Q = ParseUncompressedPoint(ownPublicKey)
        });

        using var peerEcdh = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP384,
            Q = ParseUncompressedPoint(peerPublicKey)
        });

        return ecdh.DeriveRawSecretAgreement(peerEcdh.PublicKey);
    }

    private static ECPoint ParseUncompressedPoint(byte[] uncompressedKey)
    {
        return new ECPoint
        {
            X = uncompressedKey[1..(1 + CoordinateSize)],
            Y = uncompressedKey[(1 + CoordinateSize)..]
        };
    }

    private static byte[] ExtractAndExpand(byte[] sharedSecret, byte[] kemContext)
    {
        // shared_secret = ExtractAndExpand(dh, kem_context)
        // where: prk = LabeledExtract("", "shared_secret", dh)
        //        shared_secret = LabeledExpand(prk, "shared_secret", kem_context, Nsecret)
        byte[] prk = LabeledExtract(KemSuiteId, [], "shared_secret"u8.ToArray(), sharedSecret);
        return LabeledExpand(KemSuiteId, prk, "shared_secret"u8.ToArray(), kemContext, NSecret);
    }

    private static (byte[] Key, byte[] BaseNonce, byte[] ExporterSecret) KeySchedule(
        byte[] sharedSecret,
        byte[] info)
    {
        // mode = 0 (Base mode).
        byte[] mode = [0];

        byte[] pskIdHash = LabeledExtract(HpkeSuiteId, [], "psk_id_hash"u8.ToArray(), []);
        byte[] infoHash = LabeledExtract(HpkeSuiteId, [], "info_hash"u8.ToArray(), info);

        byte[] ksContext = Concat(mode, pskIdHash, infoHash);

        byte[] secret = LabeledExtract(
            HpkeSuiteId,
            sharedSecret,
            "secret"u8.ToArray(),
            []); // psk = "" for base mode.

        byte[] key = LabeledExpand(HpkeSuiteId, secret, "key"u8.ToArray(), ksContext, Nk);
        byte[] baseNonce = LabeledExpand(
            HpkeSuiteId, secret, "base_nonce"u8.ToArray(), ksContext, Nn);
        byte[] exporterSecret = LabeledExpand(
            HpkeSuiteId, secret, "exp"u8.ToArray(), ksContext, Nh);

        return (key, baseNonce, exporterSecret);
    }

    private static byte[] LabeledExtract(
        byte[] suiteId,
        byte[] salt,
        byte[] label,
        byte[] ikm)
    {
        // labeled_ikm = "HPKE-v1" || suite_id || label || ikm
        byte[] labeledIkm = Concat("HPKE-v1"u8.ToArray(), suiteId, label, ikm);
        return HkdfExtract(salt, labeledIkm);
    }

    private static byte[] LabeledExpand(
        byte[] suiteId,
        byte[] prk,
        byte[] label,
        byte[] info,
        int length)
    {
        // labeled_info = I2OSP(L, 2) || "HPKE-v1" || suite_id || label || info
        byte[] lengthBytes = [(byte)(length >> 8), (byte)(length & 0xFF)];
        byte[] labeledInfo = Concat(lengthBytes, "HPKE-v1"u8.ToArray(), suiteId, label, info);
        return HkdfExpand(prk, labeledInfo, length);
    }

    private static byte[] HkdfExtract(byte[] salt, byte[] ikm)
    {
        if (salt.Length == 0)
        {
            salt = new byte[Nh];
        }

        return HMACSHA384.HashData(salt, ikm);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        int hashLen = Nh;
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

    private static byte[] BuildSuiteId(byte[] prefix, ushort id)
    {
        return Concat(prefix, [(byte)(id >> 8), (byte)(id & 0xFF)]);
    }

    private static byte[] BuildHpkeSuiteId()
    {
        byte[] prefix = "HPKE"u8.ToArray();
        byte[] kem = I2Osp2(OhttpConstants.KemP384HkdfSha384);
        byte[] kdf = I2Osp2(OhttpConstants.KdfHkdfSha384);
        byte[] aead = I2Osp2(OhttpConstants.AeadAes256Gcm);
        return Concat(prefix, kem, kdf, aead);
    }

    private static byte[] I2Osp2(ushort value)
    {
        return [(byte)(value >> 8), (byte)(value & 0xFF)];
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
/// An HPKE P-384 key pair.
/// </summary>
public record HpkeKeyPair(byte[] PublicKey, byte[] PrivateKey);

/// <summary>
/// Sender context returned by SetupBaseSender.
/// </summary>
public class HpkeSenderContext
{
    public HpkeSenderContext(
        byte[] enc,
        byte[] key,
        byte[] baseNonce,
        byte[] exporterSecret,
        ulong sequenceNumber)
    {
        this.Enc = enc;
        this.Key = key;
        this.BaseNonce = baseNonce;
        this.ExporterSecret = exporterSecret;
        this.SequenceNumber = sequenceNumber;
    }

    public byte[] Enc { get; }

    public byte[] Key { get; }

    public byte[] BaseNonce { get; }

    public byte[] ExporterSecret { get; }

    public ulong SequenceNumber { get; private set; }

    /// <summary>
    /// Encrypt plaintext with associated data and increment the sequence number.
    /// </summary>
    /// <param name="aad">Associated data.</param>
    /// <param name="plaintext">The plaintext to encrypt.</param>
    /// <returns>The encrypted ciphertext.</returns>
    public byte[] Seal(byte[] aad, byte[] plaintext)
    {
        byte[] nonce = Hpke.ComputeNonce(this.BaseNonce, this.SequenceNumber);
        byte[] ct = Hpke.AeadSeal(this.Key, nonce, aad, plaintext);
        this.SequenceNumber++;
        return ct;
    }
}

/// <summary>
/// Receiver context returned by SetupBaseReceiver.
/// </summary>
public class HpkeReceiverContext
{
    public HpkeReceiverContext(
        byte[] key,
        byte[] baseNonce,
        byte[] exporterSecret,
        ulong sequenceNumber)
    {
        this.Key = key;
        this.BaseNonce = baseNonce;
        this.ExporterSecret = exporterSecret;
        this.SequenceNumber = sequenceNumber;
    }

    public byte[] Key { get; }

    public byte[] BaseNonce { get; }

    public byte[] ExporterSecret { get; }

    public ulong SequenceNumber { get; private set; }

    /// <summary>
    /// Decrypt ciphertext with associated data and increment the sequence number.
    /// </summary>
    /// <param name="aad">Associated data.</param>
    /// <param name="ciphertext">The ciphertext to decrypt.</param>
    /// <returns>The decrypted plaintext.</returns>
    public byte[] Open(byte[] aad, byte[] ciphertext)
    {
        byte[] nonce = Hpke.ComputeNonce(this.BaseNonce, this.SequenceNumber);
        byte[] pt = Hpke.AeadOpen(this.Key, nonce, aad, ciphertext);
        this.SequenceNumber++;
        return pt;
    }
}
