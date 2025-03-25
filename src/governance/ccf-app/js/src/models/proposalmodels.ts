export interface GetProposalResponse {
  nextLink?: string;
  value: Proposal[];
}

export interface Proposal {
  seqno: number;
  proposalState: string;
  proposalId: string;
}
