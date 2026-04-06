namespace Cleanroom.Governance;

/**
 * Get deployment info response
 */
model GetDeploymentInfoResponse {
  /** Proposal IDs */
  proposalIds: string[];

  /** Deployment info data */
  data: unknown;
}

/**
 * Set deployment info args
 */
model SetDeploymentInfoArgs {
  /** Contract ID */
  contractId: string;
}
