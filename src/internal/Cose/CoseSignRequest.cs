// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseUtils;

public class CoseSignRequest
{
    public CoseSignRequest(
        CoseSignKey signKey,
        Dictionary<string, string>? protectedHeaders,
        Dictionary<string, string>? unprotectedHeaders,
        string? payload)
    {
        this.SignKey = signKey;
        this.ProtectedHeaders = protectedHeaders;
        this.UnprotectedHeaders = unprotectedHeaders;
        this.Payload = payload;
    }

    public string Algorithm { get; } = "ES384";

    public CoseSignKey SignKey { get; } = default!;

    public Dictionary<string, string>? ProtectedHeaders { get; }

    public Dictionary<string, string>? UnprotectedHeaders { get; }

    public string? Payload { get; }
}
