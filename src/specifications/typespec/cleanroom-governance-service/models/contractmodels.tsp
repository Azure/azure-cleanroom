namespace Cleanroom.Governance;

/**
 * Put contract request
 */
model PutContractRequest {
  /** Contract version */
  version: string;

  /** Contract data */
  data: unknown;
}

/**
 * Get contract response
 */
model GetContractResponse {
  /** Contract ID */
  id: string;

  /** Contract version */
  version: string;

  /** Contract data */
  data: unknown;

  /** Contract state */
  state: string;

  /** Proposal ID */
  proposalId: string;

  /** Final votes */
  finalVotes?: MemberVote[];
}

/**
 * Member vote
 */
model MemberVote {
  /** Member ID */
  memberId: string;

  /** Vote value */
  vote: boolean;
}

/**
 * List contracts response
 */
model ListContractsResponse {
  /** Array of contracts */
  value: ListContractResponse[];
}

/**
 * List contract response
 */
model ListContractResponse {
  /** Contract ID */
  id: string;
}

/**
 * Set contract args
 */
model SetContractArgs {
  /** Contract ID */
  contractId: string;

  /** Contract */
  contract: PutContractRequest;
}
