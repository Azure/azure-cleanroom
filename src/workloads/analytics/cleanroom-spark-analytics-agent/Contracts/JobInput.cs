// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record JobInput(
    [property: JsonPropertyName("contractId")] string ContractId,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("datasets")] List<DatasetInfo> Datasets,
    [property: JsonPropertyName("datasink")] DatasetInfo Datasink,
    [property: JsonPropertyName("governance")] GovernanceJobInput Governance,
    [property: JsonPropertyName("startDate")] DateTimeOffset? StartDate,
    [property: JsonPropertyName("endDate")] DateTimeOffset? EndDate,
    [property: JsonPropertyName("dryRun")] bool? DryRun,
    [property: JsonPropertyName("useOptimizer")] bool? UseOptimizer);

public record GovernanceJobInput(
    [property: JsonPropertyName("serviceUrl")] string ServiceUrl,
    [property: JsonPropertyName("certBase64")] string? CertBase64,
    [property: JsonPropertyName("serviceCertDiscovery")]
    CcfServiceCertDiscoveryModel? ServiceCertDiscovery);