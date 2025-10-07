import { UserProposalApprover } from "./kvstoremodels";

export interface CreateUserProposalRequest {
  name: string;
  approvers?: UserProposalApprover[];
  args: any;
}

export interface CreateUserProposalResponse {
  proposalId: string;
}

export interface GetUserProposalResponse {
  proposalId: string;
  name: string;
  approvers: UserProposalApprover[];
  args: any;
}

export interface UserProposal {
  seqno: number;
  proposalState: string;
  proposalId: string;
}

export interface SubmitUserProposalBallotRequest {
  ballot: string;
}
