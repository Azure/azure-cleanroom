// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace OhttpCommon;

/// <summary>
/// Constants for OHTTP media types, HPKE identifiers, and well-known paths.
/// </summary>
public static class OhttpConstants
{
    // RFC 9458 media types.
    public const string OhttpKeysMediaType = "application/ohttp-keys";
    public const string OhttpRequestMediaType = "message/ohttp-req";
    public const string OhttpResponseMediaType = "message/ohttp-res";
    public const string OhttpChunkedResponseMediaType = "message/ohttp-chunked-res";

    // Base path prefix for all ohttp-gateway endpoints.
    public const string BasePathPrefix = "/ohttp-gateway";

    // Well-known paths.
    public const string WellKnownKeysPath = BasePathPrefix + "/.well-known/ohttp-keys";
    public const string AttestationPath = BasePathPrefix + "/ohttp-keys/attestation";
    public const string GatewayPathPrefix = BasePathPrefix + "/gateway/";

    // HPKE KEM identifiers (RFC 9180).
    public const ushort KemP384HkdfSha384 = 0x0011;

    // HPKE KDF identifiers (RFC 9180).
    public const ushort KdfHkdfSha384 = 0x0002;

    // HPKE AEAD identifiers (RFC 9180).
    public const ushort AeadAes256Gcm = 0x0002;

    // Default Key ID.
    public const byte DefaultKeyId = 0;
}
