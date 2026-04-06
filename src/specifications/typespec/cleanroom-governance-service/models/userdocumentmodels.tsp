import "./snpattestation.tsp";
import "./encrypt.tsp";
import "./kvstoremodels.tsp";

namespace Cleanroom.Governance;

/**
 * Put user document request
 */
model PutUserDocumentRequest {
  /** Document version */
  version: string;

  /** Contract ID */
  contractId: string;

  /** Approvers */
  approvers?: UserProposalApprover[];

  /** Document data */
  data: unknown;

  /** Labels */
  labels: UserProposalLabels;
}

/**
 * Get user document response
 */
model GetUserDocumentResponse {
  /** Document ID */
  id: string;

  /** Contract ID */
  contractId: string;

  /** Document version */
  version: string;

  /** Approvers */
  approvers?: UserProposalApprover[];

  /** Document data */
  data: unknown;

  /** Labels */
  labels: UserProposalLabels;

  /** Document state */
  state: string;

  /** Proposal ID */
  proposalId: string;

  /** Proposer ID */
  proposerId: string;

  /** Final votes */
  finalVotes?: UserVote[];
}

/**
 * List user documents response
 */
model ListUserDocumentsResponse {
  /** Array of user documents */
  value: ListUserDocumentResponse[];
}

/**
 * List user document response
 */
model ListUserDocumentResponse {
  /** Document ID */
  id: string;

  /** Labels */
  labels: UserProposalLabels;
}

/**
 * Set user document args
 */
model SetUserDocumentArgs {
  /** Document ID */
  documentId: string;

  /** Document */
  document: PutUserDocumentRequest;
}

/**
 * Get accepted user document request
 */
model GetAcceptedUserDocumentRequest {
  /** Attestation evidence */
  attestation: SnpEvidence;

  /** Encryption info */
  encrypt: Encrypt;
}

/**
 * Get accepted user document response
 */
model GetAcceptedUserDocumentResponse {
  /** Encrypted GetUserDocumentResponse content */
  value: string;
}
