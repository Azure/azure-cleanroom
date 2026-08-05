namespace Azure.Cleanroom.Governance;

/**
 * Digital signature information
 */
model Sign {
  /** Signature value */
  signature: string;

  /** PEM encoded public key */
  publicKey: string;
}

/**
 * Generate signing key response
 */
model GenerateSigningKeyResponse {
  /** Request ID */
  reqId: string;

  /** Key ID */
  kid: string;
}

/**
 * Sign payload response
 */
model SignPayloadResponse {
  /** Base64 wrapped signed value */
  value: string;
}

/**
 * Signing info
 */
model SigningInfo {
  /** Signing enabled */
  enabled: boolean;

  /** PEM encoded public key */
  publicKeyPem?: string;
}
