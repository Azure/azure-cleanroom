// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Text;

namespace OhttpCommon;

/// <summary>
/// RFC 9292 — Binary Representation of HTTP Messages.
/// Supports Known-Length messages (framing indicator 0x00 for requests, 0x01 for responses)
/// and Indeterminate-Length responses (framing indicator 0x03).
/// </summary>
public static class BinaryHttp
{
    // Framing indicators.
    private const byte KnownLengthRequest = 0x00;
    private const byte KnownLengthResponse = 0x01;
    private const byte IndeterminateLengthResponse = 0x03;

    /// <summary>
    /// Serializes an HTTP request into Binary HTTP format (RFC 9292).
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="scheme">The URI scheme.</param>
    /// <param name="authority">The URI authority.</param>
    /// <param name="path">The request path.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="body">The request body.</param>
    /// <returns>The serialized Binary HTTP request bytes.</returns>
    public static byte[] SerializeRequest(
        string method,
        string scheme,
        string authority,
        string path,
        Dictionary<string, string> headers,
        byte[] body)
    {
        using var ms = new MemoryStream();

        // Framing indicator: known-length request.
        ms.WriteByte(KnownLengthRequest);

        // Request control data: method, scheme, authority, path as length-prefixed fields.
        WriteLengthPrefixedString(ms, method);
        WriteLengthPrefixedString(ms, scheme);
        WriteLengthPrefixedString(ms, authority);
        WriteLengthPrefixedString(ms, path);

        // Header section (as a single length-prefixed block).
        byte[] headerBlock = SerializeHeaders(headers);
        WriteVarint(ms, headerBlock.Length);
        ms.Write(headerBlock);

        // Body (content) as a length-prefixed block.
        WriteVarint(ms, body.Length);
        ms.Write(body);

        // No trailers — zero-length trailer section.
        WriteVarint(ms, 0);

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a Binary HTTP request back into its components.
    /// </summary>
    /// <param name="data">The binary data to deserialize.</param>
    /// <returns>The deserialized Binary HTTP request.</returns>
    public static BinaryHttpRequest DeserializeRequest(ReadOnlySpan<byte> data)
    {
        int offset = 0;

        byte framing = data[offset++];
        if (framing != KnownLengthRequest)
        {
            throw new FormatException(
                $"Expected known-length request framing (0x00), got 0x{framing:X2}.");
        }

        string method = ReadLengthPrefixedString(data, ref offset);
        string scheme = ReadLengthPrefixedString(data, ref offset);
        string authority = ReadLengthPrefixedString(data, ref offset);
        string path = ReadLengthPrefixedString(data, ref offset);

        int headerLen = (int)ReadVarint(data, ref offset);
        Dictionary<string, string> headers = DeserializeHeaders(data.Slice(offset, headerLen));
        offset += headerLen;

        int bodyLen = (int)ReadVarint(data, ref offset);
        byte[] body = data.Slice(offset, bodyLen).ToArray();

        return new BinaryHttpRequest(method, scheme, authority, path, headers, body);
    }

    /// <summary>
    /// Serializes an HTTP response into Binary HTTP format (RFC 9292).
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="headers">The response headers.</param>
    /// <param name="body">The response body.</param>
    /// <returns>The serialized Binary HTTP response bytes.</returns>
    public static byte[] SerializeResponse(
        int statusCode,
        Dictionary<string, string> headers,
        byte[] body)
    {
        using var ms = new MemoryStream();

        // Framing indicator: known-length response.
        ms.WriteByte(KnownLengthResponse);

        // No informational responses — zero-length block.
        WriteVarint(ms, 0);

        // Final response control data: status code as varint.
        WriteVarint(ms, statusCode);

        // Headers section.
        byte[] headerBlock = SerializeHeaders(headers);
        WriteVarint(ms, headerBlock.Length);
        ms.Write(headerBlock);

        // Body.
        WriteVarint(ms, body.Length);
        ms.Write(body);

        // No trailers.
        WriteVarint(ms, 0);

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a Binary HTTP response back into its components.
    /// Supports both Known-Length (framing 0x01) and Indeterminate-Length (framing 0x03).
    /// </summary>
    /// <param name="data">The binary data to deserialize.</param>
    /// <returns>The deserialized Binary HTTP response.</returns>
    public static BinaryHttpResponse DeserializeResponse(ReadOnlySpan<byte> data)
    {
        int offset = 0;

        byte framing = data[offset++];
        if (framing == KnownLengthResponse)
        {
            return DeserializeKnownLengthResponse(data, ref offset);
        }
        else if (framing == IndeterminateLengthResponse)
        {
            return DeserializeIndeterminateLengthResponse(data, ref offset);
        }
        else
        {
            throw new FormatException(
                $"Expected response framing (0x01 or 0x03), got 0x{framing:X2}.");
        }
    }

    /// <summary>
    /// Serializes the start of an indeterminate-length Binary HTTP response
    /// (RFC 9292 §3.2): framing indicator, control data, and header section.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="headers">The response headers.</param>
    /// <returns>The serialized header bytes.</returns>
    public static byte[] SerializeIndeterminateResponseStart(
        int statusCode,
        Dictionary<string, string> headers)
    {
        using var ms = new MemoryStream();

        // Framing indicator: indeterminate-length response.
        WriteVarint(ms, IndeterminateLengthResponse);

        // No informational responses — final response indicator.
        WriteVarint(ms, 0);

        // Status code.
        WriteVarint(ms, statusCode);

        // Header section (field lines terminated by zero-length name).
        foreach (KeyValuePair<string, string> header in headers)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(header.Key.ToLowerInvariant());
            byte[] valueBytes = Encoding.ASCII.GetBytes(header.Value);
            WriteVarint(ms, nameBytes.Length);
            ms.Write(nameBytes);
            WriteVarint(ms, valueBytes.Length);
            ms.Write(valueBytes);
        }

        WriteVarint(ms, 0); // Header section terminator.

        return ms.ToArray();
    }

    /// <summary>
    /// Serializes a content chunk for an indeterminate-length Binary HTTP message.
    /// </summary>
    /// <param name="data">The content bytes.</param>
    /// <returns>The serialized content chunk (varint length + data).</returns>
    public static byte[] SerializeContentChunk(byte[] data)
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, data.Length);
        ms.Write(data);
        return ms.ToArray();
    }

    /// <summary>
    /// Serializes the content terminator and empty trailer section for an
    /// indeterminate-length Binary HTTP message.
    /// </summary>
    /// <returns>The terminator bytes.</returns>
    public static byte[] SerializeContentEnd()
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, 0); // Content terminator.
        WriteVarint(ms, 0); // Empty trailer section terminator.
        return ms.ToArray();
    }

    /// <summary>
    /// Parses the header portion of an indeterminate-length Binary HTTP response
    /// (framing indicator, informational responses, status code, and header fields).
    /// Used for streaming: parse the first decrypted OHTTP chunk to extract
    /// the status code and headers without waiting for the body.
    /// </summary>
    /// <param name="data">The decrypted first OHTTP chunk.</param>
    /// <returns>The status code and response headers.</returns>
    public static (int StatusCode, Dictionary<string, string> Headers)
        ParseIndeterminateResponseStart(ReadOnlySpan<byte> data)
    {
        int offset = 0;

        byte framing = data[offset++];
        if (framing != IndeterminateLengthResponse)
        {
            throw new FormatException(
                $"Expected indeterminate-length response framing (0x03), " +
                $"got 0x{framing:X2}.");
        }

        // Skip informational responses.
        while (true)
        {
            long v = ReadVarint(data, ref offset);
            if (v == 0)
            {
                break;
            }

            int infoHeaderLen = (int)ReadVarint(data, ref offset);
            offset += infoHeaderLen;
        }

        // Status code.
        int statusCode = (int)ReadVarint(data, ref offset);

        // Header section (terminated by zero-length name).
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            long nameLen = ReadVarint(data, ref offset);
            if (nameLen == 0)
            {
                break;
            }

            string name = Encoding.ASCII.GetString(data.Slice(offset, (int)nameLen));
            offset += (int)nameLen;

            int valueLen = (int)ReadVarint(data, ref offset);
            string value = Encoding.ASCII.GetString(data.Slice(offset, valueLen));
            offset += valueLen;

            headers[name] = value;
        }

        return (statusCode, headers);
    }

    /// <summary>
    /// Extracts the raw body data from a serialized content chunk
    /// (varint length prefix + data). Returns the data without the prefix.
    /// Returns null if this is a content terminator (zero-length chunk).
    /// </summary>
    /// <param name="data">A decrypted OHTTP chunk containing a Binary HTTP content chunk.</param>
    /// <returns>The raw body data, or null if this is the content terminator.</returns>
    public static byte[]? ExtractContentChunkData(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        long chunkLen = ReadVarint(data, ref offset);
        if (chunkLen == 0)
        {
            return null; // Content terminator.
        }

        return data.Slice(offset, (int)chunkLen).ToArray();
    }

    private static BinaryHttpResponse DeserializeKnownLengthResponse(
        ReadOnlySpan<byte> data,
        ref int offset)
    {
        // Skip informational responses block.
        int infoLen = (int)ReadVarint(data, ref offset);
        offset += infoLen;

        // Status code.
        int statusCode = (int)ReadVarint(data, ref offset);

        // Headers.
        int headerLen = (int)ReadVarint(data, ref offset);
        Dictionary<string, string> headers = DeserializeHeaders(data.Slice(offset, headerLen));
        offset += headerLen;

        // Body.
        int bodyLen = (int)ReadVarint(data, ref offset);
        byte[] body = data.Slice(offset, bodyLen).ToArray();

        return new BinaryHttpResponse(statusCode, headers, body);
    }

    private static BinaryHttpResponse DeserializeIndeterminateLengthResponse(
        ReadOnlySpan<byte> data,
        ref int offset)
    {
        // Skip informational responses (each starts with a status code 100-199,
        // terminated by a zero-valued indicator).
        while (true)
        {
            long v = ReadVarint(data, ref offset);
            if (v == 0)
            {
                break; // End of informational responses.
            }

            // v is an informational status code. Skip its known-length header section.
            int infoHeaderLen = (int)ReadVarint(data, ref offset);
            offset += infoHeaderLen;
        }

        // Final response status code.
        int statusCode = (int)ReadVarint(data, ref offset);

        // Header section (field lines terminated by zero-length name).
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            long nameLen = ReadVarint(data, ref offset);
            if (nameLen == 0)
            {
                break; // Header section terminator.
            }

            string name = Encoding.ASCII.GetString(data.Slice(offset, (int)nameLen));
            offset += (int)nameLen;

            int valueLen = (int)ReadVarint(data, ref offset);
            string value = Encoding.ASCII.GetString(data.Slice(offset, valueLen));
            offset += valueLen;

            headers[name] = value;
        }

        // Content chunks (terminated by zero-length chunk).
        using var bodyStream = new MemoryStream();
        while (true)
        {
            long chunkLen = ReadVarint(data, ref offset);
            if (chunkLen == 0)
            {
                break; // Content terminator.
            }

            bodyStream.Write(data.Slice(offset, (int)chunkLen));
            offset += (int)chunkLen;
        }

        // Skip trailer section (terminated by zero-length name).
        while (offset < data.Length)
        {
            long nameLen = ReadVarint(data, ref offset);
            if (nameLen == 0)
            {
                break;
            }

            offset += (int)nameLen;
            int valueLen = (int)ReadVarint(data, ref offset);
            offset += valueLen;
        }

        return new BinaryHttpResponse(statusCode, headers, bodyStream.ToArray());
    }

    private static byte[] SerializeHeaders(Dictionary<string, string> headers)
    {
        using var ms = new MemoryStream();
        foreach (KeyValuePair<string, string> header in headers)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(header.Key.ToLowerInvariant());
            byte[] valueBytes = Encoding.ASCII.GetBytes(header.Value);
            WriteVarint(ms, nameBytes.Length);
            ms.Write(nameBytes);
            WriteVarint(ms, valueBytes.Length);
            ms.Write(valueBytes);
        }

        return ms.ToArray();
    }

    private static Dictionary<string, string> DeserializeHeaders(ReadOnlySpan<byte> data)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int offset = 0;

        while (offset < data.Length)
        {
            int nameLen = (int)ReadVarint(data, ref offset);
            string name = Encoding.ASCII.GetString(data.Slice(offset, nameLen));
            offset += nameLen;

            int valueLen = (int)ReadVarint(data, ref offset);
            string value = Encoding.ASCII.GetString(data.Slice(offset, valueLen));
            offset += valueLen;

            headers[name] = value;
        }

        return headers;
    }

    private static void WriteLengthPrefixedString(MemoryStream ms, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        WriteVarint(ms, bytes.Length);
        ms.Write(bytes);
    }

    private static string ReadLengthPrefixedString(ReadOnlySpan<byte> data, ref int offset)
    {
        int length = (int)ReadVarint(data, ref offset);
        string value = Encoding.ASCII.GetString(data.Slice(offset, length));
        offset += length;
        return value;
    }

    // RFC 9000 variable-length integer encoding (used by RFC 9292).
    private static void WriteVarint(MemoryStream ms, long value)
    {
        if (value < 0x40)
        {
            ms.WriteByte((byte)value);
        }
        else if (value < 0x4000)
        {
            byte[] buf = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)(value | 0x4000));
            ms.Write(buf);
        }
        else if (value < 0x40000000)
        {
            byte[] buf = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)(value | 0x80000000));
            ms.Write(buf);
        }
        else
        {
            byte[] buf = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buf, (ulong)value | 0xC000000000000000);
            ms.Write(buf);
        }
    }

    private static long ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        byte first = data[offset];
        int prefix = first >> 6;
        long value;

        switch (prefix)
        {
            case 0:
                value = first;
                offset += 1;
                break;
            case 1:
                value = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]) & 0x3FFF;
                offset += 2;
                break;
            case 2:
                value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]) & 0x3FFFFFFF;
                offset += 4;
                break;
            case 3:
                value = (long)(BinaryPrimitives.ReadUInt64BigEndian(data[offset..])
                    & 0x3FFFFFFFFFFFFFFF);
                offset += 8;
                break;
            default:
                throw new FormatException("Invalid varint prefix.");
        }

        return value;
    }
}

/// <summary>
/// A deserialized Binary HTTP request.
/// </summary>
public record BinaryHttpRequest(
    string Method,
    string Scheme,
    string Authority,
    string Path,
    Dictionary<string, string> Headers,
    byte[] Body);

/// <summary>
/// A deserialized Binary HTTP response.
/// </summary>
public record BinaryHttpResponse(
    int StatusCode,
    Dictionary<string, string> Headers,
    byte[] Body);
