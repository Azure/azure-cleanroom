// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class DeploymentSpec
{
    [JsonPropertyName("data")]
    public DeploymentTemplate? Data { get; set; }
}

public class DeploymentTemplate
{
    [JsonPropertyName("chartMetadata")]
    public ChartMetadata ChartMetadata { get; set; } = default!;

    [JsonPropertyName("values")]
    public AgentChartValues Values { get; set; } = default!;

    public void Validate()
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

public class AgentChartValues
{
    [JsonPropertyName("ccrgovEndpoint")]
    public string CcrgovEndpoint { get; set; } = default!;

    [JsonPropertyName("ccrgovApiPathPrefix")]
    public string CcrgovApiPathPrefix { get; set; } = default!;

    [JsonPropertyName("ccrgovServiceCert")]
    public string? CcrgovServiceCert { get; set; }

    [JsonPropertyName("ccrgovServiceCertDiscovery")]
    public ServiceCertDiscoveryInput? CcrgovServiceCertDiscovery { get; set; }

    [JsonPropertyName("sparkFrontendEndpoint")]
    public string SparkFrontendEndpoint { get; set; } = default!;

    [JsonPropertyName("sparkFrontendSnpHostData")]
    public string SparkFrontendSnpHostData { get; set; } = default!;

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

    public static AgentChartValues ToAgentChartValues(
        ContractData contractData,
        bool telemetryCollectionEnabled,
        string sparkFrontendEndpoint,
        string sparkFrontendSnpHostData)
    {
        var values = new AgentChartValues()
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

    public void Validate()
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
                    throw new ArgumentException("BadInput: constitutionDigest cannot be " +
                        "specified as chart input.");
                }

                if (!this.CcrgovServiceCertDiscovery.SkipDigestCheck &&
                    string.IsNullOrEmpty(this.CcrgovServiceCertDiscovery.JsappBundleDigest))
                {
                    throw new ArgumentException("BadInput: jsappBundleDigest cannot be specified " +
                        "as chart input.");
                }
            }
        }

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