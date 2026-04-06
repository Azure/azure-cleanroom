// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

// Internal DTO sent to the frontend. Matches the frontend's expected shape.
public record FrontendJobInput(
    [property: JsonPropertyName("contractId")] string ContractId,
    [property: JsonPropertyName("modelName")] string ModelName,
    [property: JsonPropertyName("predictor")] FrontendPredictorInput Predictor,
    [property: JsonPropertyName("modelDir")] string? ModelDir,
    [property: JsonPropertyName("datasets")] List<DatasetInfo> Datasets,
    [property: JsonPropertyName("governance")] GovernanceJobInput? Governance,
    [property: JsonPropertyName("placement")] FrontendPlacementInput? Placement = null);

// Frontend predictor shape (raw deployment mode).
public record FrontendPredictorInput(
    [property: JsonPropertyName("model")] FrontendModelInput Model,
    [property: JsonPropertyName("minReplicas")] int? MinReplicas = null,
    [property: JsonPropertyName("maxReplicas")] int? MaxReplicas = null,
    [property: JsonPropertyName("timeout")] int? Timeout = null,
    [property: JsonPropertyName("batcher")] FrontendBatcherInput? Batcher = null,
    [property: JsonPropertyName("deploymentStrategy")]
    DeploymentStrategySpec? DeploymentStrategy = null,
    [property: JsonPropertyName("scaleMetricType")] string? ScaleMetricType = null,
    [property: JsonPropertyName("autoScaling")] AutoScalingSpec? AutoScaling = null);

// Frontend model shape.
public record FrontendModelInput(
    [property: JsonPropertyName("modelFormat")] FrontendModelFormatInput ModelFormat,
    [property: JsonPropertyName("protocolVersion")] string? ProtocolVersion,
    [property: JsonPropertyName("runtime")] string? Runtime,
    [property: JsonPropertyName("storageUri")] string? StorageUri,
    [property: JsonPropertyName("args")] List<string>? Args = null,
    [property: JsonPropertyName("resources")] ResourceRequirementsSpec? Resources = null,
    [property: JsonPropertyName("env")] List<EnvVarSpec>? Env = null,
    [property: JsonPropertyName("storage")] StorageSpec? Storage = null);

// Frontend batcher shape.
public record FrontendBatcherInput(
    [property: JsonPropertyName("maxBatchSize")] int? MaxBatchSize = null,
    [property: JsonPropertyName("maxLatency")] int? MaxLatency = null,
    [property: JsonPropertyName("timeout")] int? Timeout = null);

// Frontend model format shape.
public record FrontendModelFormatInput(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version = null);

// Frontend placement shape.
public record FrontendPlacementInput(
    [property: JsonPropertyName("hostNetwork")] bool? HostNetwork = null);

// Governance job input.
public record GovernanceJobInput(
    [property: JsonPropertyName("serviceUrl")] string ServiceUrl,
    [property: JsonPropertyName("certBase64")] string? CertBase64,
    [property: JsonPropertyName("serviceCertDiscovery")]
    CcfServiceCertDiscoveryModel? ServiceCertDiscovery);
