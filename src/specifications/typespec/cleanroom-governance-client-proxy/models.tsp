import "../cleanroom-governance-client-lib/models.tsp";

using Cleanroom.Governance;
using Cleanroom.Governance.Client;

namespace Cleanroom.Governance.Client.Proxy;

// ============================================================================
// Service-Specific Model Extensions
// ============================================================================
// Most models are imported from governance-client-lib.
// This file only contains service-specific extensions if needed.

/**
 * Sidecar workspace configuration
 */
model SidecarWorkspaceConfigurationModel {
  /** CCR Governance endpoint URL */
  ccrgovEndpoint: string;

  /** PEM encoded service certificate */
  serviceCert?: string;

  /** Service certificate discovery configuration */
  serviceCertDiscovery?: CcfServiceCertDiscoveryModel;
}

/**
 * CCF service certificate discovery configuration
 */
model CcfServiceCertDiscoveryModel {
  /** Certificate discovery endpoint URL */
  certificateDiscoveryEndpoint: string;

  /** Expected host data values for attestation */
  hostData: string[];

  /** Expected constitution digest */
  constitutionDigest?: string;

  /** Expected JavaScript app bundle digest */
  jsAppBundleDigest?: string;

  /** Skip digest verification (for testing only) */
  skipDigestCheck?: boolean;
}
