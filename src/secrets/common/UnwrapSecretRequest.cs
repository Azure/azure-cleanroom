// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcrSecrets;

public class UnwrapSecretRequest
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = default!;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = default!;

    [JsonPropertyName("kid")]
    public string Kid { get; set; } = default!;

    [JsonPropertyName("akvEndpoint")]
    public string AkvEndpoint { get; set; } = default!;

    [JsonPropertyName("kek")]
    public KekInfo Kek { get; set; } = default!;

    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; } = default!;

    public override string ToString()
    {
        UnwrapSecretRequest copy = this;
        if (!string.IsNullOrEmpty(this.AccessToken) || !string.IsNullOrEmpty(this.Kek.AccessToken))
        {
            copy = JsonSerializer.Deserialize<UnwrapSecretRequest>(JsonSerializer.Serialize(this))!;
            copy.AccessToken = "redacted";
            copy.Kek.AccessToken = "redacted";
        }

        return JsonSerializer.Serialize(copy);
    }
}

public class KekInfo
{
    [JsonPropertyName("kid")]
    public string Kid { get; set; } = default!;

    [JsonPropertyName("akvEndpoint")]
    public string AkvEndpoint { get; set; } = default!;

    [JsonPropertyName("maaEndpoint")]
    public string MaaEndpoint { get; set; } = default!;

    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; } = default!;
}