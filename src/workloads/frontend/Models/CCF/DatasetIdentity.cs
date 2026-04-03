// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models.CCF;

public class DatasetIdentity
{
    [JsonPropertyName("clientId")]
    public required string ClientId { get; set; }

    [JsonPropertyName("issuerUrl")]
    public required string IssuerUrl { get; set; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    public static DatasetIdentity? FromIdentity(Identity? identity)
    {
        if (identity == null ||
            string.IsNullOrWhiteSpace(identity.ClientId) ||
            string.IsNullOrWhiteSpace(identity.TenantId) ||
            string.IsNullOrWhiteSpace(identity.Name))
        {
            return default;
        }

        return new DatasetIdentity
        {
            ClientId = identity.ClientId,
            Name = identity.Name,
            TenantId = identity.TenantId,
            IssuerUrl = identity.TokenIssuer["issuer"]?["url"]?.ToString() ?? string.Empty,
        };
    }
}