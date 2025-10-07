import { SnpEvidence } from "../attestation/snpattestation";
import { Encrypt } from "./encrypt";

export interface PutMemberDocumentRequest {
  version: string;
  contractId: string;
  data: any;
}

export interface GetMemberDocumentResponse {
  id: string;
  contractId: string;
  version: string;
  data: any;
  state: string;
  proposalId: string;
  finalVotes?: {
    memberId: string;
    vote: boolean;
  }[];
}

export interface ListMemberDocumentResponse {
  id: string;
}

export interface SetMemberDocumentArgs {
  documentId: string;
  document: PutMemberDocumentRequest;
}

export interface GetAcceptedMemberDocumentRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
}

export interface GetAcceptedMemberDocumentResponse {
  // Encrypted <GetDocumentResponse> content.
  value: string;
}
