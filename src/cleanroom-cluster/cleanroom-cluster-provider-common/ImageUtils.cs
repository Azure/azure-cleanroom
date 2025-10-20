// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CleanRoomProvider;

public static class ImageUtils
{
    private const string McrRegistryUrl = "mcr.microsoft.com/azurecleanroom";
    private const string McrTag = "6.0.0";

    private static SemaphoreSlim semaphore = new(1, 1);

    public static string GetAnalyticsAgentSecurityPolicyDocumentUrl()
    {
        return AnalyticsAgentSecurityPolicyDocumentUrl();
    }

    public static async Task<SecurityPolicyDocument> GetAnalyticsAgentSecurityPolicyDocument(
        ILogger logger,
        IConfiguration config)
    {
        var oras = new OrasClient(logger, config);
        string outDir = Path.GetTempPath();
        string documentUrl = AnalyticsAgentSecurityPolicyDocumentUrl();
        string document =
            Path.Combine(outDir, "cleanroom-spark-analytics-agent-security-policy.yaml");

        try
        {
            // Avoid simultaneous downloads to the same location to avoid races in reading the
            // file.
            await semaphore.WaitAsync();
            await oras.Pull(documentUrl, outDir);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var yml = await File.ReadAllTextAsync(document);
            var policyDocument = deserializer.Deserialize<SecurityPolicyDocument>(yml);
            return policyDocument;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public static async Task<SecurityPolicyDocument> GetSparkFrontendSecurityPolicyDocument(
        ILogger logger,
        IConfiguration config)
    {
        var oras = new OrasClient(logger, config);
        string outDir = Path.GetTempPath();
        string documentUrl = SparkFrontendSecurityPolicyDocumentUrl();
        string document =
            Path.Combine(outDir, "cleanroom-spark-frontend-security-policy.yaml");

        try
        {
            // Avoid simultaneous downloads to the same location to avoid races in reading the
            // file.
            await semaphore.WaitAsync();
            await oras.Pull(documentUrl, outDir);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var yml = await File.ReadAllTextAsync(document);
            var policyDocument = deserializer.Deserialize<SecurityPolicyDocument>(yml);
            return policyDocument;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public static string RegistryUrl()
    {
        var url = Environment.GetEnvironmentVariable("CR_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL");

        return !string.IsNullOrEmpty(url) ? url.TrimEnd('/') : McrRegistryUrl;
    }

    public static string SidecarsPolicyDocumentRegistryUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CR_CLUSTER_PROVIDER_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL");

        return !string.IsNullOrEmpty(url) ? url.TrimEnd('/') : McrRegistryUrl;
    }

    public static string RegistryUseHttp()
    {
        _ = bool.TryParse(
            Environment.GetEnvironmentVariable("CR_CLUSTER_PROVIDER_CONTAINER_REGISTRY_USE_HTTP"),
            out var useHttp);

        return useHttp.ToString().ToLower();
    }

    public static string AnalyticsAgentSecurityPolicyDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CR_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}" +
            $"/policies/workloads/cleanroom-spark-analytics-agent-security-policy:{McrTag}";
    }

    public static string SparkFrontendSecurityPolicyDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CR_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}" +
            $"/policies/workloads/cleanroom-spark-frontend-security-policy:{McrTag}";
    }

    public static string GetAnalyticsAgentChartPath()
    {
        return GetImage("CR_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL") ??
            $"{McrRegistryUrl}/workloads/helm/cleanroom-spark-analytics-agent";
    }

    public static string GetAnalyticsAgentChartVersion()
    {
        return GetTag("CR_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL") ??
            McrTag;
    }

    public static string GetSparkFrontendChartPath()
    {
        return GetImage("CR_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL") ??
            $"{McrRegistryUrl}/workloads/helm/cleanroom-spark-frontend";
    }

    public static string GetSparkFrontendChartVersion()
    {
        return GetTag("CR_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL") ??
            McrTag;
    }

    public static string AnalyticsAgentImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE") ??
        $"{McrRegistryUrl}/workloads/cleanroom-spark-analytics-agent";
    }

    public static string AnalyticsAgentTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE") ?? $"{McrTag}";
    }

    public static string SparkFrontendImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE") ??
        $"{McrRegistryUrl}/workloads/cleanroom-spark-frontend";
    }

    public static string SparkFrontendTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE") ?? $"{McrTag}";
    }

    public static string CcrProxyImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_PROXY_IMAGE") ??
        $"{McrRegistryUrl}/ccr-proxy";
    }

    public static string CcrProxyTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_PROXY_IMAGE") ?? $"{McrTag}";
    }

    public static string CcrGovernanceImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_GOVERNANCE_IMAGE")
            ?? $"{McrRegistryUrl}/ccr-governance";
    }

    public static string CcrGovernanceTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_GOVERNANCE_IMAGE") ?? McrTag;
    }

    public static string CcrAttestationImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_ATTESTATION_IMAGE")
            ?? $"{McrRegistryUrl}/ccr-attestation";
    }

    public static string CcrAttestationTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_ATTESTATION_IMAGE") ?? McrTag;
    }

    public static string OtelCollectorImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE")
            ?? $"{McrRegistryUrl}/otel-collector";
    }

    public static string OtelCollectorTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE") ?? McrTag;
    }

    public static string SkrImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_SKR_IMAGE") ?? $"{McrRegistryUrl}/skr";
    }

    public static string SkrTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_SKR_IMAGE") ?? $"{McrTag}";
    }

    public static string CredentialsProxyImage()
    {
        // TODO (anrdesai): Move test image references to test project
        return "cleanroombuild.azurecr.io/workleap/azure-cli-credentials-proxy";
    }

    public static string CredentialsProxyTag()
    {
        return "1.2.5";
    }

    public static string GetCleanroomVersionsDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
           "CR_CLUSTER_PROVIDER_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/sidecar-digests:{McrTag}";
    }

    public static string CleanroomAnalyticsApp()
    {
        var url = Environment.GetEnvironmentVariable(
           "CR_CLUSTER_PROVIDER_CLEANROOM_ANALYTICS_IMAGE_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/workloads/cleanroom-spark-analytics-app:{McrTag}";
    }

    public static string CleanroomAnalyticsAppPolicyDocument()
    {
        var url = Environment.GetEnvironmentVariable(
           "CR_CLUSTER_PROVIDER_CLEANROOM_ANALYTICS_IMAGE_POLICY_DOCUMENT_URL");
        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/policies/workloads/cleanroom-spark-analytics-app:{McrTag}";
    }

    private static string? GetImage(string envVar)
    {
        var image = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(image))
        {
            // localhost:5000/foo/bar:123 => localhost:500/foo/bar
            int finalPart = image.LastIndexOf("/");
            int finalColon = image.LastIndexOf(":");
            if (finalColon > finalPart)
            {
                return image.Substring(0, finalColon);
            }

            return image;
        }

        return null;
    }

    private static string? GetTag(string envVar)
    {
        var image = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(image))
        {
            // localhost:5000/foo/bar:123 => 123
            int finalPart = image.LastIndexOf("/");
            var parts = image.Substring(finalPart + 1).Split(":");
            if (parts.Length > 1)
            {
                return parts[1];
            }

            return "latest";
        }

        return null;
    }
}
