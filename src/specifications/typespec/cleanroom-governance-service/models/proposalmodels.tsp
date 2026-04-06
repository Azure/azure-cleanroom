namespace Cleanroom.Governance;

/**
 * Get proposal response
 */
model GetProposalResponse {
  /** Next link for pagination */
  nextLink?: string;

  /** Array of proposals */
  value: Proposal[];
}

/**
 * Proposal
 */
model Proposal {
  /** Sequence number */
  seqno: int32;

  /** Proposal state */
  proposalState: string;

  /** Proposal ID */
  proposalId: string;
}
