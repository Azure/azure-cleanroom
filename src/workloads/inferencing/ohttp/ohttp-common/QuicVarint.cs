// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers.Binary;

namespace OhttpCommon;

/// <summary>
/// QUIC variable-length integer encoding and decoding (RFC 9000 §16).
/// </summary>
public static class QuicVarint
{
    /// <summary>
    /// Encodes a value as a QUIC variable-length integer.
    /// </summary>
    /// <param name="value">The value to encode (must be non-negative).</param>
    /// <returns>The encoded bytes.</returns>
    public static byte[] Encode(long value)
    {
        if (value < 0x40)
        {
            return [(byte)value];
        }
        else if (value < 0x4000)
        {
            byte[] buf = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)(value | 0x4000));
            return buf;
        }
        else if (value < 0x40000000)
        {
            byte[] buf = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)(value | 0x80000000));
            return buf;
        }
        else
        {
            byte[] buf = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(
                buf,
                (ulong)value | 0xC000000000000000);
            return buf;
        }
    }

    /// <summary>
    /// Decodes a QUIC variable-length integer from a span.
    /// </summary>
    /// <param name="data">The data to decode from.</param>
    /// <returns>The decoded value and the number of bytes consumed.</returns>
    public static (long Value, int BytesRead) Decode(ReadOnlySpan<byte> data)
    {
        byte first = data[0];
        int prefix = first >> 6;

        return prefix switch
        {
            0 => (first & 0x3F, 1),
            1 => (BinaryPrimitives.ReadUInt16BigEndian(data) & 0x3FFF, 2),
            2 => (BinaryPrimitives.ReadUInt32BigEndian(data) & 0x3FFFFFFF, 4),
            3 => ((long)(BinaryPrimitives.ReadUInt64BigEndian(data)
                & 0x3FFFFFFFFFFFFFFF), 8),
            _ => throw new FormatException("Invalid QUIC varint prefix.")
        };
    }

    /// <summary>
    /// Reads a QUIC variable-length integer from a stream asynchronously.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <returns>The decoded value.</returns>
    public static async Task<long> ReadFromStreamAsync(Stream stream)
    {
        byte[] firstByte = new byte[1];
        await ReadExactAsync(stream, firstByte, 0, 1);

        int prefix = firstByte[0] >> 6;
        int totalLen = 1 << prefix; // 1, 2, 4, or 8 bytes.

        if (totalLen == 1)
        {
            return firstByte[0] & 0x3F;
        }

        byte[] buf = new byte[totalLen];
        buf[0] = firstByte[0];
        await ReadExactAsync(stream, buf, 1, totalLen - 1);

        return Decode(buf).Value;
    }

    /// <summary>
    /// Reads exactly the specified number of bytes from a stream.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="offset">The offset in the buffer to start writing.</param>
    /// <param name="count">The number of bytes to read.</param>
    /// <returns>A task that completes when the bytes have been read.</returns>
    public static async Task ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(offset + totalRead, count - totalRead));
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream.");
            }

            totalRead += read;
        }
    }
}
