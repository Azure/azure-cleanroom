import "./snpattestation.tsp";
import "./encrypt.tsp";
import "./sign.tsp";

namespace Cleanroom.Governance;

/**
 * Get token request
 */
model GetTokenRequest {
  /** Attestation evidence */
  attestation: SnpEvidence;

  /** Encryption info */
  encrypt: Encrypt;
}

/**
 * Get token response
 */
model GetTokenResponse {
  /** Token value */
  value: string;
}

/**
 * Set subject policy request
 */
model SetSubjectPolicyRequest {
  /** Attestation evidence */
  attestation: SnpEvidence;

  /** Encryption info */
  encrypt: Encrypt;

  /** Signature */
  sign: Sign;

  /** Encrypted request data */
  data: string;
}

/**
 * Set subject policy request data
 */
model SetSubjectPolicyRequestData {
  /** Policy type */
  type: string;

  /** Policy claims */
  claims: unknown;
}
