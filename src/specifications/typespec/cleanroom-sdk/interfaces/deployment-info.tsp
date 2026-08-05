import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * DeploymentInfo operations
 */
interface DeploymentInfo {
  /** getDeploymentInfo (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  getDeploymentInfo(contractId: string): GetDeploymentInfoResponse;
}
