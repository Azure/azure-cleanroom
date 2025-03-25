export declare const cleanroom: CleanRoom;

export interface Certificate {
  /**
   * Certificate in PEM encoding.
   */
  cert: string;
}

export interface CleanRoom {
  crypto: CleanRoomCrypto;
}

export interface CleanRoomCrypto {
  /**
   * Generates a self signed certificate.
   *
   * @param privateKey A PEM-encoded private key
   * @param subjectName The subject name to set in the cert
   * @param subjectAlternateNames Any subject alternate names for the cert
   * @param validityPeriodDays The validity (expiry) to set for the cert
   * @param ca Whether generating a CA cert or not
   * @param caPathLenConstraint Optional path length constraing value to set when generating a CA cert. Defaults to 0.
   */
  generateSelfSignedCert(
    privateKey: string,
    subjectName: string,
    subjectAlternateNames: string[],
    validityPeriodDays: number,
    ca: boolean,
    caPathLenConstraint?: number
  ): Certificate;

  /**
   * Generates an endorsed certificate.
   *
   * @param publicKey A PEM-encoded public key
   * @param subjectName The subject name to set in the cert
   * @param subjectAlternateNames Any subject alternate names for the cert
   * @param validityPeriodDays The validity (expiry) to set for the cert
   * @param issuerPrivateKey The PEM-encoded issuer private key
   * @param issuerCert The PEM-encoded issuer cert
   * @param ca Whether generating a CA cert or not
   * @param caPathLenConstraint Optional path length constraing value to set when generating a CA cert. Defaults to 0.
   */
  generateEndorsedCert(
    publicKey: string,
    subjectName: string,
    subjectAlternateNames: string[],
    validityPeriodDays: number,
    issuerPrivateKey: string,
    issuerCert: string,
    ca: boolean,
    caPathLenConstraint?: number
  ): Certificate;
}
