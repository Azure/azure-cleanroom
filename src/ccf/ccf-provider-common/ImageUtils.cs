// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CcfProvider;

public static class ImageUtils
{
    private const string McrRegistryUrl = "mcr.microsoft.com/azurecleanroom";
    private const string McrTag = "5.0.0";

    private static SemaphoreSlim semaphore = new(1, 1);

    public static async Task<SecurityPolicyDocument> GetNetworkSecurityPolicyDocument(
        ILogger logger)
    {
        var oras = new OrasClient(logger);
        string outDir = Path.GetTempPath();
        string documentUrl = CcfNetworkSecurityPolicyDocumentUrl();
        string document = Path.Combine(outDir, "ccf-network-security-policy.yaml");

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

    public static async Task<SecurityPolicyDocument> GetRecoveryServiceSecurityPolicyDocument(
        ILogger logger)
    {
        var oras = new OrasClient(logger);
        string outDir = Path.GetTempPath();
        string documentUrl = CcfRecoveryServiceSecurityPolicyDocumentUrl();
        string document = Path.Combine(outDir, "ccf-recovery-service-security-policy.yaml");

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
        var url = Environment.GetEnvironmentVariable("CCF_PROVIDER_CONTAINER_REGISTRY_URL");

        return !string.IsNullOrEmpty(url) ? url.TrimEnd('/') : McrRegistryUrl;
    }

    public static string CcfNetworkSecurityPolicyDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/policies/ccf/ccf-network-security-policy:{McrTag}";
    }

    public static string CcfRecoveryServiceSecurityPolicyDocumentUrl()
    {
        var url = Environment.GetEnvironmentVariable(
            "CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL");

        return !string.IsNullOrEmpty(url) ? url :
            $"{McrRegistryUrl}/policies/ccf/ccf-recovery-service-security-policy:{McrTag}";
    }

    public static string CcfRunJsAppVirtualImage()
    {
        return GetImage("CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE") ??
        $"{McrRegistryUrl}/ccf/app/run-js/virtual";
    }

    public static string CcfRunJsAppVirtualTag()
    {
        return GetTag("CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE") ?? $"{McrTag}";
    }

    public static string CcfRunJsAppSnpImage()
    {
        return GetImage("CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE") ??
        $"{McrRegistryUrl}/ccf/app/run-js/snp";
    }

    public static string CcfRunJsAppSnpTag()
    {
        return GetTag("CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE") ?? $"{McrTag}";
    }

    public static string CcfRecoveryAgentImage()
    {
        return GetImage("CCF_PROVIDER_RECOVERY_AGENT_IMAGE") ??
        $"{McrRegistryUrl}/ccf/ccf-recovery-agent";
    }

    public static string CcfRecoveryAgentTag()
    {
        return GetTag("CCF_PROVIDER_RECOVERY_AGENT_IMAGE") ?? $"{McrTag}";
    }

    public static string CcfRecoveryServiceImage()
    {
        return GetImage("CCF_PROVIDER_RECOVERY_SERVICE_IMAGE") ??
        $"{McrRegistryUrl}/ccf/ccf-recovery-service";
    }

    public static string CcfRecoveryServiceTag()
    {
        return GetTag("CCF_PROVIDER_RECOVERY_SERVICE_IMAGE") ?? $"{McrTag}";
    }

    public static string CcrProxyImage()
    {
        return GetImage("CCF_PROVIDER_PROXY_IMAGE") ??
        $"{McrRegistryUrl}/ccr-proxy";
    }

    public static string CcrProxyTag()
    {
        return GetTag("CCF_PROVIDER_PROXY_IMAGE") ?? $"{McrTag}";
    }

    public static string CcfNginxImage()
    {
        return GetImage("CCF_PROVIDER_NGINX_IMAGE") ?? $"{McrRegistryUrl}/ccf/ccf-nginx";
    }

    public static string CcfNginxTag()
    {
        return GetTag("CCF_PROVIDER_NGINX_IMAGE") ?? McrTag;
    }

    public static string CcrAttestationImage()
    {
        return GetImage("CCF_PROVIDER_ATTESTATION_IMAGE") ?? $"{McrRegistryUrl}/ccr-attestation";
    }

    public static string CcrAttestationTag()
    {
        return GetTag("CCF_PROVIDER_ATTESTATION_IMAGE") ?? McrTag;
    }

    public static string SkrImage()
    {
        return GetImage("CCF_PROVIDER_SKR_IMAGE") ?? $"{McrRegistryUrl}/skr";
    }

    public static string SkrTag()
    {
        return GetTag("CCF_PROVIDER_SKR_IMAGE") ?? $"{McrTag}";
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

    public static string LocalSkrImage()
    {
        // TODO (anrdesai): Move test image references to test project
        return "cleanroombuild.azurecr.io/local-skr";
    }

    public static string LocalSkrTag()
    {
        return "latest";
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
