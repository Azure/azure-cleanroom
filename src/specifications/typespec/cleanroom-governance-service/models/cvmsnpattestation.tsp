namespace Cleanroom.Governance;

/**
 * CVM SNP attestation evidence collected via the vTPM path.
 */
model CvmSnpAttestationEvidence {
  /** Base64 encoded TPM quote. */
  tpmQuote: string;

  /** Base64 encoded HCL report. */
  hclReport: string;

  /** Base64 encoded SNP report. */
  snpReport: string;

  /** Base64 encoded AIK certificate. */
  aikCert: string;

  /** PCR values as key-value pairs. */
  pcrs: Record<string>;
}

/**
 * Input for CVM SNP attestation verification.
 */
model CvmSnpAttestationInput {
  /** The attestation evidence. */
  evidence: CvmSnpAttestationEvidence;

  /** Nonce for replay protection. */
  nonce: string;
}
