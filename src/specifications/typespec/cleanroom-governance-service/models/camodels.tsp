import "./snpattestation.tsp";
import "./encrypt.tsp";
import "./sign.tsp";

namespace Cleanroom.Governance;

/**
 * CA config item
 */
model CAConfigItem {
  /** CA enabled status */
  enabled: string;

  /** Request ID */
  requestId: string;
}

/**
 * CA key item
 */
model CAKeyItem {
  /** Request ID */
  requestId: string;

  /** Public key */
  publicKey: string;

  /** Private key */
  privateKey: string;

  /** CA certificate */
  caCert: string;
}

/**
 * CA info
 */
model CAInfo {
  /** CA enabled */
  enabled: boolean;

  /** CA certificate */
  caCert?: string;

  /** Public key */
  publicKey?: string;

  /** Proposal IDs */
  proposalIds: string[];
}

/**
 * Generate endorsed cert request
 */
model GenerateEndorsedCertRequest {
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
 * Generate endorsed cert request data
 */
model GenerateEndorsedCertRequestData {
  /** Public key */
  publicKey: string;

  /** Subject name */
  subjectName: string;

  /** Subject alternate names */
  subjectAlternateNames?: string[];

  /** Validity period in days */
  validityPeriodDays: int32;
}
