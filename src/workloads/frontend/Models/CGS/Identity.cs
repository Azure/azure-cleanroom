// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FrontendSvc.Models.CCF;

namespace FrontendSvc.Models;

public class Identity
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("clientId")]
    public required string ClientId { get; set; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; set; }

    [JsonPropertyName("tokenIssuer")]
    public required JsonObject TokenIssuer { get; set; }

    public static Identity? FromDatasetIdentity(
        DatasetIdentity? datasetIdentity)
    {
        if (datasetIdentity != null)
        {
            var federatedIdentity = JsonSerializer.SerializeToNode(
                GetDefaultIdentity());

            return new Identity
            {
                ClientId = datasetIdentity.ClientId,
                Name = datasetIdentity.Name,
                TenantId = datasetIdentity.TenantId,
                TokenIssuer = new JsonObject
                {
                    ["federatedIdentity"] = federatedIdentity,
                    ["issuer"] = new JsonObject
                    {
                        ["configuration"] = string.Empty,
                        ["protocol"] = ProtocolType.AzureAD_Federated.ToString(),
                        ["url"] = datasetIdentity.IssuerUrl,
                    },
                    ["issuerType"] = IssuerType.FederatedIdentityBasedTokenIssuer.ToString(),
                }
            };
        }
        else
        {
            return GetDefaultIdentity();
        }
    }

    private static Identity GetDefaultIdentity()
    {
        return new Identity
        {
            ClientId = string.Empty,
            Name = "cleanroom_cgs_oidc",
            TenantId = string.Empty,
            TokenIssuer = new JsonObject
            {
                ["issuer"] = new JsonObject
                {
                    ["configuration"] = string.Empty,
                    ["protocol"] = ProtocolType.Attested_OIDC.ToString(),
                    ["url"] = "https://cgs/oidc",
                },
                ["issuerType"] = IssuerType.AttestationBasedTokenIssuer.ToString(),
            }
        };
    }
}
