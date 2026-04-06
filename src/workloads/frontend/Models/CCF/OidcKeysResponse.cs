// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

/// <summary>
/// Response from the CCF /oidc/keys endpoint containing JSON Web Key Set (JWKS).
/// </summary>
public class OidcKeysResponse
{
    [JsonPropertyName("keys")]
    public List<JsonWebKey> Keys { get; set; } = new();
}

/// <summary>
/// Represents a JSON Web Key (JWK) as defined in RFC 7517.
/// </summary>
public class JsonWebKey
{
    [JsonPropertyName("kty")]
    public string KeyType { get; set; } = default!;

    [JsonPropertyName("kid")]
    public string? KeyId { get; set; }

    [JsonPropertyName("use")]
    public string? Use { get; set; }

    [JsonPropertyName("alg")]
    public string? Algorithm { get; set; }

    [JsonPropertyName("n")]
    public string? N { get; set; }

    [JsonPropertyName("e")]
    public string? E { get; set; }

    [JsonPropertyName("x5c")]
    public List<string>? X5c { get; set; }

    [JsonPropertyName("x5t")]
    public string? X5t { get; set; }

    [JsonPropertyName("x5t#S256")]
    public string? X5tS256 { get; set; }
}
