import "./snpattestation.tsp";
import "./encrypt.tsp";
import "./sign.tsp";

namespace Cleanroom.Governance;

/**
 * Put secret by member/user request
 */
model PutSecretByMemberUserRequest {
  /** Secret value */
  value: string;
}

/**
 * Put secret by clean room request
 */
model PutSecretByCleanRoomRequest {
  /** Attestation evidence */
  attestation: SnpEvidence;

  /** Encryption info */
  encrypt: Encrypt;

  /** Signature */
  sign: Sign;

  /** Encrypted data */
  data: string;
}

/**
 * Put secret by clean room request data
 */
model PutSecretByCleanRoomRequestData {
  /** Secret value */
  value: string;
}

/**
 * Put secret response
 */
model PutSecretResponse {
  /** Secret ID */
  secretId: string;
}

/**
 * Get secret request
 */
model GetSecretRequest {
  /** Attestation evidence */
  attestation: SnpEvidence;

  /** Encryption info */
  encrypt: Encrypt;
}

/**
 * List secrets response
 */
model ListSecretsResponse {
  /** Array of secrets */
  value: ListSecretResponse[];
}

/**
 * List secret response
 */
model ListSecretResponse {
  /** Secret ID */
  secretId: string;
}

/**
 * Get secret response
 */
model GetSecretResponse {
  /** Secret value */
  value: string;
}
