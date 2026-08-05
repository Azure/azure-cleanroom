// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers.Binary;

namespace OhttpCommon;

/// <summary>
/// RFC 9458 §3 — Binary serialization and parsing of OHTTP KeyConfig.
/// </summary>
/// <remarks>
/// Wire format:
///   KeyID (1 byte) || KEM ID (2 bytes) || Public Key (Npk bytes) ||
///   Symmetric Algorithms Length (2 bytes) ||
///     [ KDF ID (2 bytes) || AEAD ID (2 bytes) ]+.
/// </remarks>
public class KeyConfig
{
    public KeyConfig(byte keyId, ushort kemId, byte[] publicKey, SymmetricAlgorithms[] algorithms)
    {
        this.KeyId = keyId;
        this.KemId = kemId;
        this.PublicKey = publicKey;
        this.Algorithms = algorithms;
    }

    public byte KeyId { get; }

    public ushort KemId { get; }

    public byte[] PublicKey { get; }

    public SymmetricAlgorithms[] Algorithms { get; }

    /// <summary>
    /// Creates a KeyConfig for DHKEM(P-384, HKDF-SHA384) / AES-256-GCM with the given
    /// key ID and public key.
    /// </summary>
    /// <param name="keyId">The key identifier byte.</param>
    /// <param name="publicKey">The P-384 public key bytes (uncompressed).</param>
    /// <returns>A new KeyConfig instance.</returns>
    public static KeyConfig CreateDefault(byte keyId, byte[] publicKey)
    {
        return new KeyConfig(
            keyId,
            OhttpConstants.KemP384HkdfSha384,
            publicKey,
            [
                new SymmetricAlgorithms(
                    OhttpConstants.KdfHkdfSha384,
                    OhttpConstants.AeadAes256Gcm)
            ]);
    }

    /// <summary>
    /// Parses a KeyConfig from binary wire format.
    /// </summary>
    /// <param name="data">The binary data to parse.</param>
    /// <returns>The deserialized KeyConfig.</returns>
    public static KeyConfig Deserialize(ReadOnlySpan<byte> data)
    {
        int offset = 0;

        byte keyId = data[offset++];

        ushort kemId = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        // Public key length depends on KEM. For P-384 it's 97 bytes (uncompressed point).
        int pkLen = GetPublicKeyLength(kemId);
        byte[] publicKey = data.Slice(offset, pkLen).ToArray();
        offset += pkLen;

        ushort symAlgLen = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        int numAlgorithms = symAlgLen / 4;
        var algorithms = new SymmetricAlgorithms[numAlgorithms];
        for (int i = 0; i < numAlgorithms; i++)
        {
            ushort kdfId = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            ushort aeadId = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            algorithms[i] = new SymmetricAlgorithms(kdfId, aeadId);
        }

        return new KeyConfig(keyId, kemId, publicKey, algorithms);
    }

    /// <summary>
    /// Serializes this KeyConfig to its binary wire format (RFC 9458 §3).
    /// </summary>
    /// <returns>The serialized binary representation.</returns>
    public byte[] Serialize()
    {
        // Calculate symmetric algorithms length: 4 bytes per entry.
        ushort symAlgLen = (ushort)(this.Algorithms.Length * 4);

        // Total: 1 (keyId) + 2 (kemId) + publicKey.Length + 2 (symAlgLen) + symAlgLen.
        int totalLen = 1 + 2 + this.PublicKey.Length + 2 + symAlgLen;
        byte[] buffer = new byte[totalLen];
        int offset = 0;

        buffer[offset++] = this.KeyId;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), this.KemId);
        offset += 2;

        Buffer.BlockCopy(this.PublicKey, 0, buffer, offset, this.PublicKey.Length);
        offset += this.PublicKey.Length;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), symAlgLen);
        offset += 2;

        foreach (SymmetricAlgorithms alg in this.Algorithms)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), alg.KdfId);
            offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), alg.AeadId);
            offset += 2;
        }

        return buffer;
    }

    private static int GetPublicKeyLength(ushort kemId)
    {
        return kemId switch
        {
            OhttpConstants.KemP384HkdfSha384 => 97,
            _ => throw new NotSupportedException($"Unsupported KEM ID: 0x{kemId:X4}")
        };
    }
}

/// <summary>
/// A KDF + AEAD symmetric algorithm pair in the KeyConfig.
/// </summary>
public record SymmetricAlgorithms(ushort KdfId, ushort AeadId);
