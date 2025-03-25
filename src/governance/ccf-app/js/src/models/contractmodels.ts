export interface PutContractRequest {
  version: string;
  data: any;
}

export interface GetContractResponse {
  id: string;
  version: string;
  data: any;
  state: string;
  proposalId: string;
  finalVotes?: {
    memberId: string;
    vote: boolean;
  }[];
}

export interface ListContractResponse {
  id: string;
}

export interface SetContractArgs {
  contractId: string;
  contract: PutContractRequest;
}
