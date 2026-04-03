// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IssuerType
{
    /// <summary>
    /// Attestation based token issuer.
    /// </summary>
    [JsonStringEnumMemberName("AttestationBasedTokenIssuer")]
    AttestationBasedTokenIssuer,

    /// <summary>
    /// Federated identity based token issuer.
    /// </summary>
    [JsonStringEnumMemberName("FederatedIdentityBasedTokenIssuer")]
    FederatedIdentityBasedTokenIssuer,
}
