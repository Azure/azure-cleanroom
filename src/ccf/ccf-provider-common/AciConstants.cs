// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfProvider;

public class AciConstants
{
    public const string CcfNetworkResourceNameTag = "ccf-network:resource-name";
    public const string CcfNetworkTypeTag = "ccf-network:type";
    public const string CcfNetworkNameTag = "ccf-network:network-name";
    public const string CcfNetworkInstanceIdTag = "ccf-network:instance-id";

    public const string CcfRecoveryServiceResourceNameTag = "ccf-recovery-service:resource-name";
    public const string CcfRecoveryServiceTypeTag = "ccf-recovery-service:type";
    public const string CcfRecoveryServiceNameTag = "ccf-recovery-service:recovery-service-name";

    public const string CcfConsortiumManagerResourceNameTag = "ccf-consortium-manager:resource-name";
    public const string CcfConsortiumManagerTypeTag = "ccf-consortium-manager:type";
    public const string CcfConsortiumManagerNameTag =
        "ccf-consortium-manager:consortium-manager-name";

    public const string AllowAllRegoBase64 =
        "cGFja2FnZSBwb2xpY3kKCmFwaV9zdm4gOj0gIjAuMTAuMCIKCm1vdW50X2RldmljZSA"
        + "6PSB7ImFsbG93ZWQiOiB0cnVlfQptb3VudF9vdmVybGF5IDo9IHsiYWxsb3dlZCI6I"
        + "HRydWV9CmNyZWF0ZV9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9"
        + "saXN0IjogbnVsbCwgImFsbG93X3N0ZGlvX2FjY2VzcyI6IHRydWV9CnVubW91bnRfZGV"
        + "2aWNlIDo9IHsiYWxsb3dlZCI6IHRydWV9IAp1bm1vdW50X292ZXJsYXkgOj0geyJhbGx"
        + "vd2VkIjogdHJ1ZX0KZXhlY19pbl9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSw"
        + "gImVudl9saXN0IjogbnVsbH0KZXhlY19leHRlcm5hbCA6PSB7ImFsbG93ZWQiOiB0cnV"
        + "lLCAiZW52X2xpc3QiOiBudWxsLCAiYWxsb3dfc3RkaW9fYWNjZXNzIjogdHJ1ZX0Kc2h"
        + "1dGRvd25fY29udGFpbmVyIDo9IHsiYWxsb3dlZCI6IHRydWV9CnNpZ25hbF9jb250YWl"
        + "uZXJfcHJvY2VzcyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV9tb3VudCA6PSB7ImF"
        + "sbG93ZWQiOiB0cnVlfQpwbGFuOV91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9Cmd"
        + "ldF9wcm9wZXJ0aWVzIDo9IHsiYWxsb3dlZCI6IHRydWV9CmR1bXBfc3RhY2tzIDo9IHs"
        + "iYWxsb3dlZCI6IHRydWV9CnJ1bnRpbWVfbG9nZ2luZyA6PSB7ImFsbG93ZWQiOiB0cnV"
        + "lfQpsb2FkX2ZyYWdtZW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CnNjcmF0Y2hfbW91bnQ"
        + "gOj0geyJhbGxvd2VkIjogdHJ1ZX0Kc2NyYXRjaF91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9Cg==";

    public class ContainerName
    {
        public const string CcfRecoveryAgent = "ccf-recovery-agent";
        public const string CcHost = "ccf-cchost";
        public const string CcrAttestation = "ccr-attestation";
        public const string CcrProxy = "ccr-proxy";
        public const string CcfRecoveryService = "ccf-recovery-service";
        public const string CcfConsortiumManager = "ccf-consortium-manager";
        public const string Skr = "skr";
    }
}
