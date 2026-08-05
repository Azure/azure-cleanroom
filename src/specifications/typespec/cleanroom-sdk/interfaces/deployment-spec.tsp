import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * DeploymentSpec operations
 */
interface DeploymentSpec {
  /** getDeploymentSpec (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  getDeploymentSpec(contractId: string): GetDeploymentSpecResponse;
}
