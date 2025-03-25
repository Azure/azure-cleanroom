import { SnpEvidence } from "../attestation/snpattestation";
import { Encrypt } from "./encrypt";

export interface PutDocumentRequest {
  version: string;
  contractId: string;
  data: any;
}

export interface GetDocumentResponse {
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

export interface ListDocumentResponse {
  id: string;
}

export interface SetDocumentArgs {
  documentId: string;
  document: PutDocumentRequest;
}

export interface GetAcceptedDocumentRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
}

export interface GetAcceptedDocumentResponse {
  // Encrypted <GetDocumentResponse> content.
  value: string;
}
