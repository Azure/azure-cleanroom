// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfCommon;

public class MountPaths
{
    public const string CertsFolderMountPath = "/app/certs";

    public const string CcfNetworkServiceCertPemFile =
        $"{CertsFolderMountPath}/ccf/service_cert.pem";

    public const string RecoveryAgentServiceCertPemFile =
        $"{CertsFolderMountPath}/recovery-agent/service-cert.pem";

    public const string RecoveryServiceCertPemFile =
        $"{CertsFolderMountPath}/recovery-service/service-cert.pem";

    public const string ConsortiumManagerCertPemFile =
        $"{CertsFolderMountPath}/consortium-manager/service-cert.pem";
}