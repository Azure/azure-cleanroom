import "./snpattestation.tsp";
import "./encrypt.tsp";
import "./contractmodels.tsp";

namespace Cleanroom.Governance;

/**
 * Put member document request
 */
model PutMemberDocumentRequest {
  /** Document version */
  version: string;

  /** Contract ID */
  contractId: string;

  /** Document data */
  data: unknown;
}

/**
 * Get member document response
 */
model GetMemberDocumentResponse {
  /** Document ID */
  id: string;

  /** Contract ID */
  contractId: string;

  /** Document version */
  version: string;

  /** Document data */
  data: unknown;

  /** Document state */
  state: string;

  /** Proposal ID */
  proposalId: string;

  /** Final votes */
  finalVotes?: MemberVote[];
}

/**
 * List member document response
 */
model ListMemberDocumentResponse {
  /** Document ID */
  id: string;
}

/**
 * Set member document args
 */
model SetMemberDocumentArgs {
  /** Document ID */
  documentId: string;

  /** Document */
  document: PutMemberDocumentRequest;
}

/**
 * Get accepted member document request
 */
model GetAcceptedMemberDocumentRequest {
  /** Attestation evidence */
  attestation: SnpEvidence;

  /** Encryption info */
  encrypt: Encrypt;
}

/**
 * Get accepted member document response
 */
model GetAcceptedMemberDocumentResponse {
  /** Encrypted GetDocumentResponse content */
  value: string;
}
