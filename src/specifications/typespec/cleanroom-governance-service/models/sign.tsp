namespace Cleanroom.Governance;

/**
 * Digital signature information
 */
model Sign {
  /** Signature value */
  signature: string;

  /** PEM encoded public key */
  publicKey: string;
}
