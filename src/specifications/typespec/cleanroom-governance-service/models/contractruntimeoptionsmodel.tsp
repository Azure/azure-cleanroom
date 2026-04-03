import "./snpattestation.tsp";

namespace Cleanroom.Governance;

/**
 * Get runtime option response
 */
model GetRuntimeOptionResponse {
  /** Runtime option status */
  status: string;

  /** Error reason */
  reason?: ErrorReason;

  /** Proposal IDs */
  proposalIds?: string[];
}

/**
 * Error reason
 */
model ErrorReason {
  /** Error code */
  code: string;

  /** Error message */
  message: string;
}

/**
 * Consent check request
 */
model ConsentCheckRequest {
  /** Attestation evidence */
  attestation: SnpEvidence;
}
