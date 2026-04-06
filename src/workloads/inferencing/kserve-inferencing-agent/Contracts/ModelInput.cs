// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

// Top-level request body for creating an inference service.
// Mirrors the KServe InferenceService CRD spec.
public record ModelInput(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("predictor")] PredictorSpec Predictor,
    [property: JsonPropertyName("modelId")] string? ModelId = null,
    [property: JsonPropertyName("placement")] PlacementSpec? Placement = null);

// Platform placement — triggers pod-policy injection by the frontend.
public record PlacementSpec(
    [property: JsonPropertyName("hostNetwork")] bool? HostNetwork = null);

// KServe predictor spec (raw deployment mode).
public record PredictorSpec(
    [property: JsonPropertyName("model")] ModelSpec Model,
    [property: JsonPropertyName("minReplicas")] int? MinReplicas = null,
    [property: JsonPropertyName("maxReplicas")] int? MaxReplicas = null,
    [property: JsonPropertyName("timeout")] int? Timeout = null,
    [property: JsonPropertyName("batcher")] BatcherSpec? Batcher = null,
    [property: JsonPropertyName("deploymentStrategy")]
    DeploymentStrategySpec? DeploymentStrategy = null,
    [property: JsonPropertyName("scaleMetricType")] string? ScaleMetricType = null,
    [property: JsonPropertyName("autoScaling")] AutoScalingSpec? AutoScaling = null);

// KServe autoscaling spec (v0.17+, backed by HPA or KEDA).
public record AutoScalingSpec(
    [property: JsonPropertyName("metrics")]
    List<AutoScalingMetricSpec>? Metrics = null,
    [property: JsonPropertyName("behavior")]
    Dictionary<string, object>? Behavior = null);

// KServe autoscaling metric spec.
public record AutoScalingMetricSpec(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("resource")]
    Dictionary<string, object>? Resource = null,
    [property: JsonPropertyName("external")]
    Dictionary<string, object>? External = null,
    [property: JsonPropertyName("podmetric")]
    Dictionary<string, object>? PodMetric = null);

// KServe model spec.
public record ModelSpec(
    [property: JsonPropertyName("modelFormat")] ModelFormat ModelFormat,
    [property: JsonPropertyName("storageUri")] string? StorageUri = null,
    [property: JsonPropertyName("protocolVersion")] string? ProtocolVersion = null,
    [property: JsonPropertyName("runtime")] string? Runtime = null,
    [property: JsonPropertyName("args")] List<string>? Args = null,
    [property: JsonPropertyName("resources")] ResourceRequirementsSpec? Resources = null,
    [property: JsonPropertyName("env")] List<EnvVarSpec>? Env = null,
    [property: JsonPropertyName("storage")] StorageSpec? Storage = null);

// KServe model format.
public record ModelFormat(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version = null);

// KServe batcher spec.
public record BatcherSpec(
    [property: JsonPropertyName("maxBatchSize")] int? MaxBatchSize = null,
    [property: JsonPropertyName("maxLatency")] int? MaxLatency = null,
    [property: JsonPropertyName("timeout")] int? Timeout = null);

// Kubernetes deployment strategy.
public record DeploymentStrategySpec(
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("rollingUpdate")] RollingUpdateSpec? RollingUpdate = null);

// Rolling update parameters.
public record RollingUpdateSpec(
    [property: JsonPropertyName("maxUnavailable")] string? MaxUnavailable = null,
    [property: JsonPropertyName("maxSurge")] string? MaxSurge = null);

// Kubernetes resource requirements.
public record ResourceRequirementsSpec(
    [property: JsonPropertyName("requests")] Dictionary<string, string>? Requests = null,
    [property: JsonPropertyName("limits")] Dictionary<string, string>? Limits = null);

// Kubernetes environment variable.
public record EnvVarSpec(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string? Value = null);

// KServe storage spec.
public record StorageSpec(
    [property: JsonPropertyName("key")] string? Key = null,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("schemaPath")] string? SchemaPath = null,
    [property: JsonPropertyName("parameters")] Dictionary<string, string>? Parameters = null);
