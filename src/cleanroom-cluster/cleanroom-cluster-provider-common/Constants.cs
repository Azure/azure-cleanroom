// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public static class Constants
{
    public const string ServiceFqdnAnnotation = "cleanroom.azure.com/fqdn";
    public const string AnalyticsWorkloadNamespace = "analytics";
    public const string AnalyticsWorkloadZoneName = AnalyticsWorkloadNamespace + ".svc";
    public const string AnalyticsAgentReleaseName = "cleanroom-spark-analytics-agent";
    public const string AnalyticsAgentNamespace = "cleanroom-spark-analytics-agent";
    public const string SparkFrontendReleaseName = "cleanroom-spark-frontend";
    public const string SparkFrontendServiceNamespace = "cleanroom-spark-frontend";
    public const string SparkFrontendServiceZoneName = SparkFrontendServiceNamespace + ".svc";

    public const string SparkFrontendEndpoint =
        $"https://{SparkFrontendReleaseName}.{SparkFrontendServiceNamespace}.svc";

    public const string ObservabilityNamespace = "telemetry";
    public const string ObservabilityZoneName = ObservabilityNamespace + ".svc";
    public const string LokiReleaseName = "cleanroom-spark-loki";
    public const string LokiServiceEndpoint = $"http://loki-headless.{ObservabilityNamespace}.svc";
    public const string TempoReleaseName = "cleanroom-spark-tempo";

    public const string TempoServiceEndpoint =
    $"http://{TempoReleaseName}.{ObservabilityNamespace}.svc";

    public const string PrometheusReleaseName = "cleanroom-spark-prometheus";

    public const string PrometheusServiceEndpoint =
        $"http://{PrometheusReleaseName}-server.{ObservabilityNamespace}.svc";

    public const string GrafanaReleaseName = "cleanroom-spark-grafana";

    public const string GrafanaServiceEndpoint =
        $"http://{GrafanaReleaseName}.{ObservabilityNamespace}.svc";
}