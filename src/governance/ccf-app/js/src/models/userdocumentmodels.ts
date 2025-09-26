import { SnpEvidence } from "../attestation/snpattestation";
import { UserProposalApprover } from "../models";
import { Encrypt } from "./encrypt";

export interface PutUserDocumentRequest {
  version: string;
  contractId: string;
  approvers?: UserProposalApprover[];
  data: any;
}

export interface GetUserDocumentResponse {
  id: string;
  contractId: string;
  version: string;
  approvers?: UserProposalApprover[];
  data: any;
  state: string;
  proposalId: string;
  proposerId: string;
  finalVotes?: {
    approverId: string;
    ballot: string;
  }[];
}

export interface ListUserDocumentsResponse {
  value: ListUserDocumentResponse[];
}

export interface ListUserDocumentResponse {
  id: string;
}

export interface SetUserDocumentArgs {
  documentId: string;
  document: PutUserDocumentRequest;
}

export interface GetAcceptedUserDocumentRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
}

export interface GetAcceptedUserDocumentResponse {
  // Encrypted <GetUserDocumentResponse> content.
  value: string;
}
