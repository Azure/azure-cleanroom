import "./kvstoremodels.tsp";

namespace Cleanroom.Governance;

/**
 * Create user proposal request
 */
model CreateUserProposalRequest {
  /** Proposal name */
  name: string;

  /** Approvers */
  approvers?: UserProposalApprover[];

  /** Proposal arguments */
  args: unknown;
}

/**
 * Create user proposal response
 */
model CreateUserProposalResponse {
  /** Proposal ID */
  proposalId: string;
}

/**
 * Get user proposal response
 */
model GetUserProposalResponse {
  /** Proposal ID */
  proposalId: string;

  /** Proposal name */
  name: string;

  /** Approvers */
  approvers: UserProposalApprover[];

  /** Proposal arguments */
  args: unknown;
}

/**
 * User proposal
 */
model UserProposal {
  /** Sequence number */
  seqno: int32;

  /** Proposal state */
  proposalState: string;

  /** Proposal ID */
  proposalId: string;
}

/**
 * Submit user proposal ballot request
 */
model SubmitUserProposalBallotRequest {
  /** Ballot value */
  ballot: string;
}
