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
    private const string McrTag = "9.0.0";

    private static SemaphoreSlim semaphore = new(1, 1);

    public static string GetAnalyticsAgentSecurityPolicyDocumentUrl()
    {
        return AnalyticsAgentSecurityPolicyDocumentUrl();
    }

    public static string GetKServeInferencingAgentSecurityPolicyDocumentUrl()
    {
        return KServeInferencingAgentSecurityPolicyDocumentUrl();
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

    public static async Task<SecurityPolicyDocument> GetKServeInferencingAgentSecurityPolicyDocument(
        ILogger logger,
        IConfiguration config)
    {
        var oras = new OrasClient(logger, config);
        string outDir = Path.GetTempPath();
        string documentUrl = KServeInferencingAgentSecurityPolicyDocumentUrl();
        string document =
            Path.Combine(outDir, "cleanroom-kserve-inferencing-agent-security-policy.yaml");

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

    public static async Task<SecurityPolicyDocument>
        GetKServeInferencingFrontendSecurityPolicyDocument(
            ILogger logger,
            IConfiguration config)
    {
        var oras = new OrasClient(logger, config);
        string outDir = Path.GetTempPath();
        string documentUrl = KServeInferencingSecurityPolicyDocumentUrl();
        string document =
            Path.Combine(outDir, "cleanroom-kserve-inferencing-frontend-security-policy.yaml");

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

    public static string KServeInferencingAgentSecurityPolicyDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_SECURITY_POLICY_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}" +
            $"/policies/workloads/cleanroom-kserve-inferencing-agent-security-policy:{McrTag}";
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

    public static string KServeInferencingSecurityPolicyDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_SECURITY_POLICY_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}" +
            $"/policies/workloads/cleanroom-kserve-inferencing-frontend-security-policy:{McrTag}";
    }

    public static string ApiServerProxyPackageUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CR_CLUSTER_PROVIDER_API_SERVER_PROXY_PACKAGE_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/k8s-node/api-server-proxy:{McrTag}";
    }

    public static string KubeletProxyPackageUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CR_CLUSTER_PROVIDER_KUBELET_PROXY_PACKAGE_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/k8s-node/kubelet-proxy:{McrTag}";
    }

    public static string CleanroomBootPackageUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CR_CLUSTER_PROVIDER_CLEANROOM_BOOT_PACKAGE_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/k8s-node/cleanroom-boot:{McrTag}";
    }

    public static string FlexNodeImageDigestsUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CR_CLUSTER_PROVIDER_FLEX_NODE_IMAGE_DIGESTS_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/cleanroom-image-digests:{McrTag}";
    }

    public static async Task<string> GetFlexNodeImageId(
        ILogger logger,
        IConfiguration config)
    {
        var oras = new OrasClient(logger, config);
        string digestsUrl = FlexNodeImageDigestsUrl();
        string tempPath = Path.GetTempPath();
        string documentPath = Path.Combine(tempPath, "cleanroom-image-digests.yaml");

        try
        {
            // Avoid simultaneous downloads to the same location to avoid races in reading the
            // file.
            await semaphore.WaitAsync();
            await oras.Pull(digestsUrl, tempPath);

            if (!File.Exists(documentPath))
            {
                throw new FileNotFoundException(
                    $"cleanroom-image-digests.yaml not found after pulling " +
                    $"from {digestsUrl}");
            }

            string yamlContent = await File.ReadAllTextAsync(documentPath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var digests = deserializer
                .Deserialize<Dictionary<string, Dictionary<string, string>>>(
                    yamlContent);

            if (!digests.TryGetValue("flexNodeImage", out var flexNode) ||
                !flexNode.TryGetValue("imageId", out var imageId) ||
                string.IsNullOrEmpty(imageId))
            {
                throw new InvalidOperationException(
                    "flexNodeImage.imageId not found in " +
                    $"cleanroom-image-digests.yaml from {digestsUrl}");
            }

            return imageId;
        }
        finally
        {
            semaphore.Release();
        }
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

    public static string GetInferencingAgentChartPath()
    {
        return GetImage("CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_CHART_URL") ??
            $"{McrRegistryUrl}/workloads/helm/kserve-inferencing-agent";
    }

    public static string GetInferencingAgentChartVersion()
    {
        return GetTag("CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_CHART_URL") ??
            McrTag;
    }

    public static string GetInferencingFrontendChartPath()
    {
        return GetImage("CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_CHART_URL") ??
            $"{McrRegistryUrl}/workloads/helm/kserve-inferencing-frontend";
    }

    public static string GetInferencingFrontendChartVersion()
    {
        return GetTag("CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_CHART_URL") ??
            McrTag;
    }

    public static string InferencingAgentImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_IMAGE") ??
        $"{McrRegistryUrl}/workloads/kserve-inferencing-agent";
    }

    public static string InferencingAgentTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_IMAGE") ?? $"{McrTag}";
    }

    public static string OhttpGatewayImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_OHTTP_GATEWAY_IMAGE") ??
            $"{McrRegistryUrl}/workloads/ohttp-gateway";
    }

    public static string OhttpGatewayTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_OHTTP_GATEWAY_IMAGE") ?? $"{McrTag}";
    }

    public static string InferencingFrontendImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_IMAGE") ??
        $"{McrRegistryUrl}/workloads/kserve-inferencing-frontend";
    }

    public static string InferencingFrontendTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_IMAGE") ?? $"{McrTag}";
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

    public static string CcrGovernanceVirtualImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_GOVERNANCE_VIRTUAL_IMAGE")
            ?? $"{McrRegistryUrl}/ccr-governance-virtual";
    }

    public static string CcrGovernanceVirtualTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_GOVERNANCE_VIRTUAL_IMAGE") ?? McrTag;
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

    public static string LocalSkrImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_LOCAL_SKR_IMAGE") ?? $"{McrRegistryUrl}/local-skr";
    }

    public static string LocalSkrTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_LOCAL_SKR_IMAGE") ?? $"{McrTag}";
    }

    public static string GetCleanroomVersionsDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
           "CR_CLUSTER_PROVIDER_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/sidecar-digests:{McrTag}";
    }

    public static string GetCleanroomCvmMeasurementsVirtualDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
           "CR_CLUSTER_PROVIDER_CLEANROOM_CVM_MEASUREMENTS_VIRTUAL_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/cvm-measurements-virtual:{McrTag}";
    }

    public static string GetCleanroomCvmMeasurementsDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
           "CR_CLUSTER_PROVIDER_CLEANROOM_CVM_MEASUREMENTS_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/cvm-measurements:{McrTag}";
    }

    /// <summary>
    /// Gets the URL of the inferencing digests document.
    /// </summary>
    /// <returns>The inferencing digests document URL.</returns>
    public static string GetInferencingDigestsDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
           "CR_CLUSTER_PROVIDER_INFERENCING_DIGESTS_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/inferencing-digests:{McrTag}";
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

    public static string GetKarpenterProviderChartPath()
    {
        return GetImage("CR_CLUSTER_PROVIDER_KARPENTER_PROVIDER_CHART_URL")
            ?? $"{McrRegistryUrl}/helm/karpenter-provider-accr";
    }

    public static string GetKarpenterProviderChartVersion()
    {
        return GetTag("CR_CLUSTER_PROVIDER_KARPENTER_PROVIDER_CHART_URL") ?? McrTag;
    }

    public static string GetKarpenterProviderImage()
    {
        return GetImage("CR_CLUSTER_PROVIDER_KARPENTER_PROVIDER_IMAGE")
            ?? $"{McrRegistryUrl}/karpenter-provider-accr";
    }

    public static string GetKarpenterProviderTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_KARPENTER_PROVIDER_IMAGE") ?? McrTag;
    }

    public static string GetProviderClientImage()
    {
        return GetImage(
            "CR_CLUSTER_PROVIDER_CLIENT_IMAGE")
            ?? ($"{McrRegistryUrl}/cleanroom-cluster/" +
            "cleanroom-cluster-provider-client");
    }

    public static string GetProviderClientTag()
    {
        return GetTag("CR_CLUSTER_PROVIDER_CLIENT_IMAGE") ?? McrTag;
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
