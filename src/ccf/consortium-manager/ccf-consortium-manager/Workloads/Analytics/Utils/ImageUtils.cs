// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using CcfConsortiumMgr;
using OrasProject.Oras.Content;
using OrasProject.Oras.Oci;
using OrasProject.Oras.Registry;
using OrasProject.Oras.Registry.Remote;
using OrasProject.Oras.Registry.Remote.Auth;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CleanRoomProvider;

public static class ImageUtils
{
    private const string McrRegistryUrl = "mcr.microsoft.com/azurecleanroom";
    private const string McrTag = "7.0.0";

    public static async Task<(string, SecurityPolicyDocument)> DownloadAndExpandAnalyticsAgentPolicy(
        SecurityPolicyCreationOption policyCreationOption,
        AgentChartValues values)
    {
        var policyDocument = await GetAnalyticsAgentSecurityPolicyDocument();

        foreach (var container in policyDocument.Containers)
        {
            container.Image = container.Image.Replace("@@RegistryUrl@@", GetRegistryUrl());
        }

        var policyRego =
            policyCreationOption == SecurityPolicyCreationOption.cachedDebug ?
            policyDocument.RegoDebug :
            policyCreationOption == SecurityPolicyCreationOption.cached ? policyDocument.Rego :
                throw new ArgumentException($"Unexpected option: {policyCreationOption}");

        // Replace placeholder variables in the policy.
        var caType = !string.IsNullOrEmpty(values.CcrgovEndpoint) ? "cgs" : "local";
        var discovery = values.CcrgovServiceCertDiscovery ?? new ServiceCertDiscoveryInput();
        policyRego = policyRego.Replace("$caType", caType);
        policyRego = policyRego.Replace("$cgsEndpoint", values.CcrgovEndpoint);
        policyRego = policyRego.Replace("$ccrgovApiPathPrefix", values.CcrgovApiPathPrefix);
        policyRego = policyRego.Replace("$serviceCertBase64", values.CcrgovServiceCert);
        policyRego = policyRego.Replace("$serviceCertDiscoveryEndpoint", discovery.Endpoint);
        policyRego = policyRego.Replace("$serviceCertDiscoverySnpHostData", discovery.SnpHostData);
        policyRego = policyRego.Replace(
            "$serviceCertDiscoverySkipDigestCheck",
            discovery.SkipDigestCheck.ToString().ToLower());
        policyRego = policyRego.Replace(
            "$serviceCertDiscoveryConstitutionDigest",
            discovery.ConstitutionDigest);
        policyRego = policyRego.Replace(
            "$serviceCertDiscoveryJsappBundleDigest",
            discovery.JsappBundleDigest);
        policyRego = policyRego.Replace("$sparkFrontendEndpoint", values.SparkFrontendEndpoint);
        policyRego = policyRego.Replace(
            "$sparkFrontendSnpHostData",
            values.SparkFrontendSnpHostData);
        policyRego = policyRego.Replace(
            "$ccfNetworkRecoveryMembers",
            values.CcfNetworkRecoveryMembers);
        policyRego = policyRego.Replace(
            "$telemetryCollectionEnabled", values.TelemetryCollectionEnabled.ToString().ToLower());
        policyRego = policyRego.Replace("$prometheusEndpoint", values.PrometheusEndpoint);
        policyRego = policyRego.Replace("$lokiEndpoint", values.LokiEndpoint);
        policyRego = policyRego.Replace("$tempoEndpoint", values.TempoEndpoint);

        return (policyRego, policyDocument);
    }

    public static async Task<(string, SecurityPolicyDocument)> DownloadAndExpandSparkFrontendPolicy(
        SecurityPolicyCreationOption policyCreationOption,
        bool telemetryCollectionEnabled)
    {
        var policyDocument = await GetSparkFrontendSecurityPolicyDocument();

        foreach (var container in policyDocument.Containers)
        {
            container.Image = container.Image.Replace("@@RegistryUrl@@", GetRegistryUrl());
        }

        var policyRego =
            policyCreationOption == SecurityPolicyCreationOption.cachedDebug ?
            policyDocument.RegoDebug :
            policyCreationOption == SecurityPolicyCreationOption.cached ? policyDocument.Rego :
                throw new ArgumentException($"Unexpected option: {policyCreationOption}");

        policyRego = policyRego.Replace(
            "$telemetryCollectionEnabled", telemetryCollectionEnabled.ToString().ToLower());
        policyRego = policyRego.Replace(
            "$prometheusEndpoint",
            telemetryCollectionEnabled ?
            $"{Constants.PrometheusServiceEndpoint}:9090/api/v1/write" :
            string.Empty);
        policyRego = policyRego.Replace(
            "$lokiEndpoint",
            telemetryCollectionEnabled ?
            $"{Constants.LokiServiceEndpoint}:3100/otlp" :
            string.Empty);
        policyRego = policyRego.Replace(
            "$tempoEndpoint",
            telemetryCollectionEnabled ?
            $"{Constants.TempoServiceEndpoint}:4317" :
            string.Empty);
        return (policyRego, policyDocument);
    }

    public static async Task<SecurityPolicyDocument> GetAnalyticsAgentSecurityPolicyDocument()
    {
        string documentPath = GetAnalyticsAgentSecurityPolicyDocumentPath();
        string documentVersion = GetAnalyticsAgentSecurityPolicyDocumentVersion();
        return await FetchSecurityPolicyDocument(documentPath, documentVersion);
    }

    public static async Task<SecurityPolicyDocument> GetSparkFrontendSecurityPolicyDocument()
    {
        string documentPath = GetSparkFrontendSecurityPolicyDocumentPath();
        string documentVersion = GetSparkFrontendSecurityPolicyDocumentVersion();
        return await FetchSecurityPolicyDocument(documentPath, documentVersion);
    }

    public static string GetAnalyticsAgentChartPath()
    {
        return GetImage(SettingName.SparkAnalyticsAgentChartUrl) ??
            $"{McrRegistryUrl}/workloads/helm/cleanroom-spark-analytics-agent";
    }

    public static string GetAnalyticsAgentChartVersion()
    {
        return GetTag(SettingName.SparkAnalyticsAgentChartUrl) ??
            McrTag;
    }

    private static string GetAnalyticsAgentSecurityPolicyDocumentPath()
    {
        return GetImage(SettingName.SparkAnalyticsAgentSecurityPolicyDocumentUrl) ??
            $"{McrRegistryUrl}/policies/workloads/cleanroom-spark-analytics-agent-security-policy";
    }

    private static string GetAnalyticsAgentSecurityPolicyDocumentVersion()
    {
        return GetTag(SettingName.SparkAnalyticsAgentSecurityPolicyDocumentUrl) ??
            McrTag;
    }

    private static string GetSparkFrontendSecurityPolicyDocumentPath()
    {
        return GetImage(SettingName.SparkFrontendSecurityPolicyDocumentUrl) ??
            $"{McrRegistryUrl}/policies/workloads/cleanroom-spark-frontend-security-policy";
    }

    private static string GetSparkFrontendSecurityPolicyDocumentVersion()
    {
        return GetTag(SettingName.SparkFrontendSecurityPolicyDocumentUrl) ??
            McrTag;
    }

    private static string GetRegistryUrl()
    {
        var url = Environment.GetEnvironmentVariable(SettingName.SparkContainerRegistryUrl);

        return !string.IsNullOrEmpty(url) ? url.TrimEnd('/') : McrRegistryUrl;
    }

    private static string? GetImage(string envVar)
    {
        var image = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(image))
        {
            // localhost:5000/foo/bar:123 => localhost:5000/foo/bar
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

    private static async Task<SecurityPolicyDocument> FetchSecurityPolicyDocument(
        string documentPath,
        string documentVersion)
    {
        // Create a repository instance to interact with the target repository.
        var repo = new Repository(new RepositoryOptions
        {
            Reference = Reference.Parse(documentPath),
            Client = new Client(new HttpClient()),
        });

        // Fetch manifest content and read it with validation.
        byte[] manifestData;
        var (manifestDescriptor, manifestStream) = await repo.FetchAsync(documentVersion);

        using (manifestStream)
        {
            manifestData = await manifestStream.ReadAllAsync(manifestDescriptor);
        }

        // Parse the manifest JSON and fetch the first layer.
        var documentManifest = JsonSerializer.Deserialize<Manifest>(manifestData)!;
        byte[] documentData = await repo.FetchAllAsync(documentManifest.Layers[0]);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var policyDocument =
            deserializer.Deserialize<SecurityPolicyDocument>(
                Encoding.UTF8.GetString(documentData));

        return policyDocument;
    }
}
