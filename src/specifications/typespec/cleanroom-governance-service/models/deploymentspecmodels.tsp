namespace Cleanroom.Governance;

/**
 * Get deployment spec response
 */
model GetDeploymentSpecResponse {
  /** Proposal IDs */
  proposalIds: string[];

  /** Deployment spec data */
  data: unknown;
}

/**
 * Set deployment spec args
 */
model SetDeploymentSpecArgs {
  /** Contract ID */
  contractId: string;
}
