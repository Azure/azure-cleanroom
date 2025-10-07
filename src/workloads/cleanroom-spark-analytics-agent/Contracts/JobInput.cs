// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record JobInput(
    [property: JsonPropertyName("contractId")] string ContractId,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("datasets")] List<Dataset> Datasets,
    [property: JsonPropertyName("datasink")] Dataset Datasink,
    [property: JsonPropertyName("governance")] GovernanceJobInput Governance,
    [property: JsonPropertyName("startDate")] DateTimeOffset? StartDate,
    [property: JsonPropertyName("endDate")] DateTimeOffset? EndDate);

public record GovernanceJobInput(
    [property: JsonPropertyName("serviceUrl")] string ServiceUrl,
    [property: JsonPropertyName("certBase64")] string? CertBase64,
    [property: JsonPropertyName("serviceCertDiscovery")]
    CcfServiceCertDiscoveryModel? ServiceCertDiscovery);