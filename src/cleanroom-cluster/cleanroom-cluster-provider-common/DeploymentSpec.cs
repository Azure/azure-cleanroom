// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class DeploymentSpec<T>
    where T : DeploymentTemplateBase
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

public abstract class DeploymentTemplateBase
{
    public abstract void Validate();
}

public class AnalyticsDeploymentTemplate : DeploymentTemplateBase
{
    [JsonPropertyName("chartMetadata")]
    public ChartMetadata ChartMetadata { get; set; } = default!;

    [JsonPropertyName("values")]
    public AnalyticsAgentChartValues Values { get; set; } = default!;

    public override void Validate()
    {
        this.Values.Validate();
    }
}

public class KServeInferencingDeploymentTemplate : DeploymentTemplateBase
{
    [JsonPropertyName("chartMetadata")]
    public ChartMetadata ChartMetadata { get; set; } = default!;

    [JsonPropertyName("values")]
    public KServeInferencingAgentChartValues Values { get; set; } = default!;

    public override void Validate()
    {
        this.Values.Validate();
    }
}

public class ChartMetadata
{
    [JsonPropertyName("release")]
    public string Release { get; set; } = default!;

    [JsonPropertyName("chart")]
    public string Chart { get; set; } = default!;

    [JsonPropertyName("version")]
    public string Version { get; set; } = default!;

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = default!;
}

public abstract class AgentChartValuesBase
{
    [JsonPropertyName("ccrgovEndpoint")]
    public string CcrgovEndpoint { get; set; } = default!;

    [JsonPropertyName("ccrgovApiPathPrefix")]
    public string CcrgovApiPathPrefix { get; set; } = default!;

    [JsonPropertyName("ccrgovServiceCert")]
    public string? CcrgovServiceCert { get; set; }

    [JsonPropertyName("ccrgovServiceCertDiscovery")]
    public ServiceCertDiscoveryInput? CcrgovServiceCertDiscovery { get; set; }

    [JsonPropertyName("ccfNetworkRecoveryMembers")]
    public string? CcfNetworkRecoveryMembers { get; set; }

    [JsonPropertyName("telemetryCollectionEnabled")]
    public bool TelemetryCollectionEnabled { get; set; } = false;

    [JsonPropertyName("prometheusEndpoint")]
    public string PrometheusEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("lokiEndpoint")]
    public string LokiEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("tempoEndpoint")]
    public string TempoEndpoint { get; set; } = string.Empty;

    public abstract void Validate();

    protected void ValidateCommon()
    {
        if (!string.IsNullOrEmpty(this.CcrgovEndpoint))
        {
            if (string.IsNullOrEmpty(this.CcrgovApiPathPrefix))
            {
                throw new ArgumentException("BadInput: ccrgovApiPathPrefix must be specified " +
                "as chart input.");
            }

            if (string.IsNullOrEmpty(this.CcrgovServiceCert) &&
                this.CcrgovServiceCertDiscovery == null)
            {
                throw new ArgumentException(
                    "BadInput: Either ccrgovServiceCert or ccrgovServiceCertDiscovery must be " +
                    "specified as chart input.");
            }

            if (this.CcrgovServiceCertDiscovery != null)
            {
                if (!string.IsNullOrEmpty(this.CcrgovServiceCert))
                {
                    throw new ArgumentException("BadInput: ccrgovServiceCert cannot be specified " +
                        "along with certificate discovery inputs as chart inputs.");
                }

                if (string.IsNullOrEmpty(this.CcrgovServiceCertDiscovery.SnpHostData))
                {
                    throw new ArgumentException("BadInput: snpHostData must be specified " +
                        "as chart input.");
                }

                if (!this.CcrgovServiceCertDiscovery.SkipDigestCheck &&
                    string.IsNullOrEmpty(this.CcrgovServiceCertDiscovery.ConstitutionDigest))
                {
                    throw new ArgumentException("BadInput: constitutionDigest must be " +
                        "specified as chart input.");
                }

                if (!this.CcrgovServiceCertDiscovery.SkipDigestCheck &&
                    string.IsNullOrEmpty(this.CcrgovServiceCertDiscovery.JsappBundleDigest))
                {
                    throw new ArgumentException("BadInput: jsappBundleDigest must be specified " +
                        "as chart input.");
                }
            }
        }
    }
}

public class AnalyticsAgentChartValues : AgentChartValuesBase
{
    [JsonPropertyName("sparkFrontendEndpoint")]
    public string SparkFrontendEndpoint { get; set; } = default!;

    [JsonPropertyName("sparkFrontendSnpHostData")]
    public string SparkFrontendSnpHostData { get; set; } = default!;

    public static AnalyticsAgentChartValues ToAgentChartValues(
        ContractData contractData,
        bool telemetryCollectionEnabled,
        string sparkFrontendEndpoint,
        string sparkFrontendSnpHostData)
    {
        var values = new AnalyticsAgentChartValues()
        {
            CcrgovApiPathPrefix = contractData.CcrgovApiPathPrefix,
            CcrgovEndpoint = contractData.CcrgovEndpoint,
            CcrgovServiceCert = contractData.CcrgovServiceCert,
            CcrgovServiceCertDiscovery = contractData.CcrgovServiceCertDiscovery,
            SparkFrontendEndpoint = sparkFrontendEndpoint,
            SparkFrontendSnpHostData = sparkFrontendSnpHostData,
            CcfNetworkRecoveryMembers = contractData.CcfNetworkRecoveryMembers != null ?
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(contractData.CcfNetworkRecoveryMembers))) : null
        };

        if (telemetryCollectionEnabled)
        {
            values.TelemetryCollectionEnabled = true;
            values.PrometheusEndpoint =
                $"{Constants.PrometheusServiceEndpoint}:9090/api/v1/write";
            values.LokiEndpoint =
                $"{Constants.LokiServiceEndpoint}:3100/otlp";
            values.TempoEndpoint =
                $"{Constants.TempoServiceEndpoint}:4317";
        }

        return values;
    }

    public override void Validate()
    {
        this.ValidateCommon();

        if (string.IsNullOrEmpty(this.SparkFrontendEndpoint))
        {
            throw new ArgumentException("BadInput: sparkFrontendEndpoint must be specified " +
                "as chart input.");
        }

        if (string.IsNullOrEmpty(this.SparkFrontendSnpHostData))
        {
            throw new ArgumentException("BadInput: sparkFrontendSnpHostData must be specified " +
                "as chart input.");
        }
    }
}

public class KServeInferencingAgentChartValues : AgentChartValuesBase
{
    [JsonPropertyName("inferencingFrontendEndpoint")]
    public string InferencingFrontendEndpoint { get; set; } = default!;

    [JsonPropertyName("inferencingFrontendSnpHostData")]
    public string InferencingFrontendSnpHostData { get; set; } = default!;

    public static KServeInferencingAgentChartValues ToAgentChartValues(
        ContractData contractData,
        bool telemetryCollectionEnabled,
        string inferencingFrontendEndpoint,
        string inferencingFrontendSnpHostData)
    {
        var values = new KServeInferencingAgentChartValues()
        {
            CcrgovApiPathPrefix = contractData.CcrgovApiPathPrefix,
            CcrgovEndpoint = contractData.CcrgovEndpoint,
            CcrgovServiceCert = contractData.CcrgovServiceCert,
            CcrgovServiceCertDiscovery = contractData.CcrgovServiceCertDiscovery,
            InferencingFrontendEndpoint = inferencingFrontendEndpoint,
            InferencingFrontendSnpHostData = inferencingFrontendSnpHostData,
            CcfNetworkRecoveryMembers = contractData.CcfNetworkRecoveryMembers != null ?
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(contractData.CcfNetworkRecoveryMembers))) : null
        };

        if (telemetryCollectionEnabled)
        {
            values.TelemetryCollectionEnabled = true;
            values.PrometheusEndpoint =
                $"{Constants.PrometheusServiceEndpoint}:9090/api/v1/write";
            values.LokiEndpoint =
                $"{Constants.LokiServiceEndpoint}:3100/otlp";
            values.TempoEndpoint =
                $"{Constants.TempoServiceEndpoint}:4317";
        }

        return values;
    }

    public override void Validate()
    {
        this.ValidateCommon();

        if (string.IsNullOrEmpty(this.InferencingFrontendEndpoint))
        {
            throw new ArgumentException("BadInput: inferencingFrontendEndpoint must be specified " +
                "as chart input.");
        }

        if (string.IsNullOrEmpty(this.InferencingFrontendSnpHostData))
        {
            throw new ArgumentException("BadInput: inferencingFrontendSnpHostData must be " +
                "specified as chart input.");
        }
    }
}
