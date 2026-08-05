// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace OhttpCommon;

/// <summary>
/// High-level streaming helpers for chunked OHTTP responses
/// (draft-ohai-chunked-ohttp-01). These methods encapsulate the entire
/// wire-format pipeline — nonce, varint-framed encrypted chunks, and final
/// sentinel — so that callers only need to supply <see cref="Stream"/>
/// instances for input and output.
/// </summary>
public static class ChunkedOhttpStream
{
    private const int DefaultReadBufferSize = 8192;

    /// <summary>
    /// Server-side: reads plaintext body bytes from <paramref name="input"/>,
    /// encrypts them as chunked OHTTP, and writes the framed ciphertext to
    /// <paramref name="output"/>. The first encrypted chunk carries the
    /// Binary HTTP indeterminate-length response header; subsequent chunks
    /// carry body data; the final chunk carries the content terminator.
    /// </summary>
    /// <param name="input">
    /// The plaintext response body stream (e.g. from an upstream server).
    /// </param>
    /// <param name="output">The wire output stream to write encrypted chunks to.</param>
    /// <param name="encryptor">
    /// A <see cref="ChunkedResponseEncryptor"/> created from
    /// <see cref="OhttpDecapsulator.CreateChunkedResponseEncryptor"/>.
    /// </param>
    /// <param name="statusCode">The inner HTTP status code.</param>
    /// <param name="headers">The inner HTTP response headers.</param>
    /// <param name="bufferSize">Read buffer size for body chunks.</param>
    /// <returns>A task that completes when the entire response has been written.</returns>
    public static async Task EncapsulateAsync(
        Stream input,
        Stream output,
        ChunkedResponseEncryptor encryptor,
        int statusCode,
        Dictionary<string, string> headers,
        int bufferSize = DefaultReadBufferSize)
    {
        // Write the response nonce (32 bytes).
        await output.WriteAsync(encryptor.ResponseNonce);

        // First chunk: Binary HTTP indeterminate-length response header.
        byte[] binaryHttpHeader = BinaryHttp.SerializeIndeterminateResponseStart(
            statusCode,
            headers);
        await WriteNonFinalChunkAsync(output, encryptor.SealChunk(binaryHttpHeader));

        // Stream body chunks.
        byte[] buffer = new byte[bufferSize];
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer)) > 0)
        {
            byte[] bodyData = buffer[..bytesRead];
            byte[] binaryHttpChunk = BinaryHttp.SerializeContentChunk(bodyData);
            await WriteNonFinalChunkAsync(output, encryptor.SealChunk(binaryHttpChunk));
        }

        // Final chunk: Binary HTTP content terminator + empty trailer.
        byte[] binaryHttpEnd = BinaryHttp.SerializeContentEnd();
        await WriteFinalChunkAsync(output, encryptor.SealFinalChunk(binaryHttpEnd));
    }

    /// <summary>
    /// Client-side: reads chunked OHTTP ciphertext from <paramref name="input"/>,
    /// decrypts each chunk, and writes the plaintext body bytes to
    /// <paramref name="output"/>. Returns the inner HTTP status code and headers.
    /// </summary>
    /// <param name="input">
    /// The wire input stream (chunked OHTTP response starting with the response nonce).
    /// </param>
    /// <param name="output">The output stream to write decrypted body bytes to.</param>
    /// <param name="encapsulator">
    /// The <see cref="OhttpEncapsulator"/> that was used to send the request
    /// (holds the HPKE exporter secret and enc).
    /// </param>
    /// <param name="onHeadersReady">
    /// Optional callback invoked after the inner HTTP headers have been
    /// decrypted but before any body bytes are written to
    /// <paramref name="output"/>. Use this to set response status code
    /// and headers on the outer HTTP response.
    /// </param>
    /// <returns>The inner HTTP status code, headers, and total body bytes written.</returns>
    public static async Task<DecapsulateResult> DecapsulateAsync(
        Stream input,
        Stream output,
        OhttpEncapsulator encapsulator,
        Func<int, Dictionary<string, string>, Task>? onHeadersReady = null)
    {
        // Read the response nonce (max(Nn, Nk) = 32 bytes).
        const int entropyLen = 32;
        byte[] responseNonce = new byte[entropyLen];
        await QuicVarint.ReadExactAsync(input, responseNonce, 0, entropyLen);

        // Create decryptor.
        ChunkedResponseDecryptor decryptor =
            encapsulator.CreateChunkedResponseDecryptor(responseNonce);

        bool headersWritten = false;
        int statusCode = 0;
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        while (true)
        {
            long chunkLen = await QuicVarint.ReadFromStreamAsync(input);
            byte[] plaintext;

            if (chunkLen == 0)
            {
                // Final chunk: extends to the end of the stream.
                using var remaining = new MemoryStream();
                await input.CopyToAsync(remaining);
                plaintext = decryptor.OpenFinalChunk(remaining.ToArray());

                // Extract any trailing body data if present.
                byte[]? bodyData = BinaryHttp.ExtractContentChunkData(plaintext);
                if (bodyData != null)
                {
                    await output.WriteAsync(bodyData);
                    await output.FlushAsync();
                    totalBytes += bodyData.Length;
                }

                break;
            }

            byte[] chunkData = new byte[(int)chunkLen];
            await QuicVarint.ReadExactAsync(input, chunkData, 0, (int)chunkLen);
            plaintext = decryptor.OpenChunk(chunkData);

            if (!headersWritten)
            {
                // First chunk: Binary HTTP header (status + response headers).
                (statusCode, headers) =
                    BinaryHttp.ParseIndeterminateResponseStart(plaintext);
                headersWritten = true;

                if (onHeadersReady != null)
                {
                    await onHeadersReady(statusCode, headers);
                }
            }
            else
            {
                // Body content chunk: extract raw data and stream to output.
                byte[]? bodyData = BinaryHttp.ExtractContentChunkData(plaintext);
                if (bodyData != null)
                {
                    await output.WriteAsync(bodyData);
                    await output.FlushAsync();
                    totalBytes += bodyData.Length;
                }
            }
        }

        return new DecapsulateResult(statusCode, headers, totalBytes);
    }

    /// <summary>
    /// Writes a non-final encrypted chunk: varint(length) || sealedChunk, then flushes.
    /// </summary>
    /// <param name="output">The output stream to write to.</param>
    /// <param name="sealedChunk">The sealed (encrypted) chunk bytes.</param>
    /// <returns>A task that completes when the chunk has been written.</returns>
    public static async Task WriteNonFinalChunkAsync(Stream output, byte[] sealedChunk)
    {
        byte[] lenPrefix = QuicVarint.Encode(sealedChunk.Length);
        await output.WriteAsync(lenPrefix);
        await output.WriteAsync(sealedChunk);
        await output.FlushAsync();
    }

    /// <summary>
    /// Writes the final encrypted chunk: varint(0) || sealedChunk, then flushes.
    /// </summary>
    /// <param name="output">The output stream to write to.</param>
    /// <param name="sealedChunk">The sealed (encrypted) chunk bytes.</param>
    /// <returns>A task that completes when the chunk has been written.</returns>
    public static async Task WriteFinalChunkAsync(Stream output, byte[] sealedChunk)
    {
        byte[] lenPrefix = QuicVarint.Encode(0);
        await output.WriteAsync(lenPrefix);
        await output.WriteAsync(sealedChunk);
        await output.FlushAsync();
    }
}

/// <summary>
/// Decapsulated response metadata returned by
/// <see cref="ChunkedOhttpStream.DecapsulateAsync"/>.
/// </summary>
/// <param name="StatusCode">The inner HTTP status code.</param>
/// <param name="Headers">The inner HTTP response headers.</param>
/// <param name="TotalBodyBytes">
/// Total number of body bytes written to the output stream.
/// </param>
public record DecapsulateResult(
    int StatusCode,
    Dictionary<string, string> Headers,
    long TotalBodyBytes);
