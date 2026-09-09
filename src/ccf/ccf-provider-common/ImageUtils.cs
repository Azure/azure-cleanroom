// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CcfProvider;

public static class ImageUtils
{
    private const string McrRegistryUrl = "mcr.microsoft.com/azurecleanroom";

    private const string DefaultVersion = "11.0.0";
    private const string DefaultReleaseChartUrl =
        "https://azure.github.io/azure-cleanroom";

    private static IReadOnlyDictionary<string, string>? catalog;

    private static SemaphoreSlim semaphore = new(1, 1);

    private static ILogger logger = NullLogger.Instance;

    public static void SetLogger(ILogger logger) => ImageUtils.logger = logger;

    public static async Task<SecurityPolicyDocument> GetNetworkSecurityPolicyDocument(
        ILogger logger)
    {
        var oras = new OrasClient(logger);
        string outDir = Path.GetTempPath();
        string documentUrl = await CcfNetworkSecurityPolicyDocumentUrl();
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
        string documentUrl = await CcfRecoveryServiceSecurityPolicyDocumentUrl();
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

    public static async Task<SecurityPolicyDocument> GetConsortiumManagerSecurityPolicyDocument(
        ILogger logger)
    {
        var oras = new OrasClient(logger);
        string outDir = Path.GetTempPath();
        string documentUrl = await CcfConsortiumManagerSecurityPolicyDocumentUrl();
        string document = Path.Combine(outDir, "ccf-consortium-manager-security-policy.yaml");

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

    public static Task<string> CcfNetworkSecurityPolicyDocumentUrl()
    {
        return ImageReference(
            ImageKeys.CcfNetworkSecurityPolicy,
            "CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL");
    }

    public static Task<string> CcfRecoveryServiceSecurityPolicyDocumentUrl()
    {
        return ImageReference(
            ImageKeys.CcfRecoveryServiceSecurityPolicy,
            "CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL");
    }

    public static Task<string> CcfConsortiumManagerSecurityPolicyDocumentUrl()
    {
        return ImageReference(
            ImageKeys.CcfConsortiumManagerSecurityPolicy,
            "CCF_PROVIDER_CONSORTIUM_MANAGER_SECURITY_POLICY_DOCUMENT_URL");
    }

    public static Task<string> CcfRunJsAppVirtualImageReference()
    {
        return ImageReference(
            ImageKeys.CcfRunJsAppVirtual,
            "CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE");
    }

    public static Task<string> CcfRunJsAppSnpImageReference()
    {
        return ImageReference(
            ImageKeys.CcfRunJsAppSnp,
            "CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE");
    }

    public static Task<string> CcfRecoveryAgentImageReference()
    {
        return ImageReference(
            ImageKeys.CcfRecoveryAgent,
            "CCF_PROVIDER_RECOVERY_AGENT_IMAGE");
    }

    public static Task<string> CvmAttestationVerifierImageReference()
    {
        return ImageReference(
            ImageKeys.CvmAttestationVerifier,
            "CCF_PROVIDER_CVM_ATTESTATION_VERIFIER_IMAGE");
    }

    public static Task<string> CcfRecoveryServiceImageReference()
    {
        return ImageReference(
            ImageKeys.CcfRecoveryService,
            "CCF_PROVIDER_RECOVERY_SERVICE_IMAGE");
    }

    public static Task<string> CcfConsortiumManagerImageReference()
    {
        return ImageReference(
            ImageKeys.CcfConsortiumManager,
            "CCF_PROVIDER_CONSORTIUM_MANAGER_IMAGE");
    }

    public static Task<string> CcrProxyImageReference()
    {
        return ImageReference(
            ImageKeys.CcrProxy,
            "CCF_PROVIDER_PROXY_IMAGE");
    }

    public static Task<string> SkrImageReference()
    {
        return ImageReference(
            ImageKeys.Skr,
            "CCF_PROVIDER_SKR_IMAGE");
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

    public static Task<string> LocalSkrImageReference()
    {
        // TODO (anrdesai): Move test image references to test project
        return ImageReference(
            ImageKeys.LocalSkr,
            "CCF_PROVIDER_LOCAL_SKR_IMAGE");
    }

    private static string ReleaseChartUrl()
    {
        var fromEnv =
            Environment.GetEnvironmentVariable("CCF_PROVIDER_RELEASE_METADATA_CHART_URL");
        return !string.IsNullOrEmpty(fromEnv) ? fromEnv : DefaultReleaseChartUrl;
    }

    private static string GetReleaseVersionFromEnv()
    {
        var v = Environment.GetEnvironmentVariable("CCF_PROVIDER_RELEASE_VERSION");
        return !string.IsNullOrEmpty(v) ? v : DefaultVersion;
    }

    // Reads the flat catalog's images + artefacts into one logical-name -> reference map.
    // The provider resolves both container images and security-policy documents (policies
    // live under artefacts) via ImageKeys; their logical names are unique across those two
    // kinds. Helm charts are excluded -- the provider does not consume them and chart names
    // deliberately reuse image names (e.g. frontend-service is both an image and a chart).
    private static Dictionary<string, string> ReadCatalog(ChartValues chartValues)
    {
        var images = new Dictionary<string, string>();
        foreach (var kind in new[] { chartValues.Images, chartValues.Artefacts })
        {
            if (kind is null)
            {
                continue;
            }

            foreach (var kvp in kind)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                {
                    images[kvp.Key] = kvp.Value;
                }
            }
        }

        return images;
    }

    // Loads the flat catalog (images + artefacts) at the environment's release version and caches
    // it for the process. The catalog is only consulted when an image has no per-image override, so
    // an override-driven deployment (onebox) never reaches here. A fetch failure is fatal -- when
    // the catalog is the resolution source, a missing/unreachable version must not be silently
    // ignored. Resolved lazily under the lock so concurrent callers share a single fetch.
    private static async Task<IReadOnlyDictionary<string, string>> GetReleaseImages()
    {
        if (catalog is not null)
        {
            return catalog;
        }

        await semaphore.WaitAsync();
        try
        {
            if (catalog is not null)
            {
                return catalog;
            }

            string valuesYaml =
                await GetChartValues(ReleaseChartUrl(), GetReleaseVersionFromEnv());
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var chartValues = deserializer.Deserialize<ChartValues>(valuesYaml)
                ?? new ChartValues();

            catalog = ReadCatalog(chartValues);
            return catalog;
        }
        finally
        {
            semaphore.Release();
        }
    }

    // Fetches the catalog values.yaml for a version. Throws (ExecuteCommandException) when the
    // catalog is unreachable or has no such version -- the caller only reaches here when the
    // catalog is the resolution source, so that is a genuine, fatal error. Called under the
    // GetReleaseImages lock, which also serializes helm's shared repository-config mutations.
    private static async Task<string> GetChartValues(string repoUrl, string releaseVersion)
    {
        var helm = new HelmImagesClient(logger);
        return await helm.ShowValues(repoUrl, releaseVersion);
    }

    private static async Task<string> ImageReference(string key, string envVar)
    {
        // Explicit per-image env-var override wins, so a dev/CI/onebox image (or one not yet
        // present in a published catalog) can be pinned regardless of release version. This is
        // checked first, so an override-driven deployment never consults the catalog. The override
        // is expected to be a fully-qualified reference (image:tag or image@digest).
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        // No per-image override: the catalog is the resolution source, addressed by the release
        // version from the environment (CCF_PROVIDER_RELEASE_VERSION). Load it (once, cached); a
        // fetch failure throws so a missing/unreachable version is not silently ignored.
        var doc = await GetReleaseImages();
        if (doc.TryGetValue(key, out var reference) && !string.IsNullOrEmpty(reference))
        {
            return reference;
        }

        // The catalog is authoritative: a missing entry means it is incomplete or out of sync with
        // the provider, so fail loudly rather than silently pulling an unrelated MCR image at the
        // version tag (which would also be wrong for onebox, where images live in a local registry).
        throw new Exception(
            $"Release version '{GetReleaseVersionFromEnv()}' catalog has no entry for image " +
            $"'{key}'.");
    }

    private sealed class ChartValues
    {
        [YamlMember(Alias = "images", ApplyNamingConventions = false)]
        public Dictionary<string, string>? Images { get; set; }

        [YamlMember(Alias = "artefacts", ApplyNamingConventions = false)]
        public Dictionary<string, string>? Artefacts { get; set; }
    }
}
