// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public static class Constants
{
    public const string AnalyticsAgentReleaseName = "cleanroom-spark-analytics-agent";
    public const string AnalyticsAgentNamespace = "cleanroom-spark-analytics-agent";
    public const string SparkFrontendReleaseName = "cleanroom-spark-frontend";
    public const string SparkFrontendServiceNamespace = "cleanroom-spark-frontend";
    public const string SparkFrontendEndpoint =
        $"https://{SparkFrontendReleaseName}.{SparkFrontendServiceNamespace}.svc";

    public const string ObservabilityNamespace = "observability";
    public const string ObservabilityZoneName = ObservabilityNamespace + ".svc";
    public const string LokiReleaseName = "cleanroom-loki";
    public const string LokiServiceEndpoint = $"http://loki-headless.{ObservabilityNamespace}.svc";

    public const string TempoReleaseName = "cleanroom-tempo";
    public const string TempoServiceEndpoint =
        $"http://{TempoReleaseName}.{ObservabilityNamespace}.svc";

    public const string PrometheusReleaseName = "cleanroom-prometheus";
    public const string PrometheusServiceEndpoint =
        $"http://{PrometheusReleaseName}-server.{ObservabilityNamespace}.svc";
}