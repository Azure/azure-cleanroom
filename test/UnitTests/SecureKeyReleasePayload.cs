// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace UnitTests;

/// <summary>
/// The payload for secure key release.
/// </summary>
public class SecureKeyReleasePayload
{
    /// <summary>
    /// Gets or sets the MAA endpoint.
    /// </summary>
    [JsonPropertyName("maa_endpoint")]
    public string MaaEndpoint { get; set; } = default!;

    /// <summary>
    /// Gets or sets the AKV endpoint.
    /// </summary>
    [JsonPropertyName("akv_endpoint")]
    public string AkvEndpoint { get; set; } = default!;

    /// <summary>
    /// Gets or sets the KID.
    /// </summary>
    [JsonPropertyName("kid")]
    public string KID { get; set; } = default!;

    /// <summary>
    /// Gets or sets the access token.
    /// </summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = default!;
}
