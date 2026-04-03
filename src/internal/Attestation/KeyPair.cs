// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AttestationClient;

public class KeyPair
{
    public KeyPair(string publicKey, string privateKey)
    {
        this.PublicKey = publicKey;
        this.PrivateKey = privateKey;
    }

    // PEM encoded string.
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; } = default!;

    // PEM encoded string.
    [JsonPropertyName("privateKey")]
    public string PrivateKey { get; } = default!;
}