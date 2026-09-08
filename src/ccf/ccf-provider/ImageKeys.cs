// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfProvider;

/// <summary>Image override keys. Values are <c>image[:tag]</c> strings (or full
/// container URLs for policy documents).</summary>
public static class ImageKeys
{
    /// <summary>CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE.</summary>
    public const string CcfRunJsAppVirtual = "ccf-run-js-app-virtual";

    /// <summary>CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE.</summary>
    public const string CcfRunJsAppSnp = "ccf-run-js-app-snp";

    /// <summary>CCF_PROVIDER_RECOVERY_AGENT_IMAGE.</summary>
    public const string CcfRecoveryAgent = "ccf-recovery-agent";

    /// <summary>CCF_PROVIDER_RECOVERY_SERVICE_IMAGE.</summary>
    public const string CcfRecoveryService = "ccf-recovery-service";

    /// <summary>CCF_PROVIDER_CONSORTIUM_MANAGER_IMAGE.</summary>
    public const string CcfConsortiumManager = "ccf-consortium-manager";

    /// <summary>CCF_PROVIDER_CVM_ATTESTATION_VERIFIER_IMAGE.</summary>
    public const string CvmAttestationVerifier = "cvm-attestation-verifier";

    /// <summary>CCF_PROVIDER_PROXY_IMAGE.</summary>
    public const string CcrProxy = "ccr-proxy";

    /// <summary>CCF_PROVIDER_SKR_IMAGE.</summary>
    public const string Skr = "skr";

    /// <summary>CCF_PROVIDER_LOCAL_SKR_IMAGE (dev/test-only; onebox catalog only).</summary>
    public const string LocalSkr = "local-skr";

    /// <summary>CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL (full container URL).</summary>
    public const string CcfNetworkSecurityPolicy = "ccf-network-security-policy";

    /// <summary>CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL (full URL).</summary>
    public const string CcfRecoveryServiceSecurityPolicy = "ccf-recovery-service-security-policy";

    /// <summary>CCF_PROVIDER_CONSORTIUM_MANAGER_SECURITY_POLICY_DOCUMENT_URL (full URL).</summary>
    public const string CcfConsortiumManagerSecurityPolicy =
        "ccf-consortium-manager-security-policy";
}
