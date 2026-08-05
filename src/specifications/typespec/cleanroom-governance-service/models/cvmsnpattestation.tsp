namespace Azure.Cleanroom.Governance;

/**
 * vTPM/SNP attestation evidence collected via the vTPM path.
 */
model VtpmAttestationEvidence {
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
 * NVIDIA GPU attestation evidence for a single GPU.
 */
model GpuDeviceEvidence {
  /** Base64 encoded GPU attestation report (SPDM measurements). */
  evidence: string;

  /** Base64 encoded GPU certificate chain. */
  certificate: string;

  /** Raw GPU architecture identifier from NVAT file evidence. */
  arch: string;

  /** Hex-encoded GPU attestation nonce from NVAT file evidence. */
  nonce: string;
}

/**
 * GPU attestation evidence container.
 */
model GpuAttestationEvidence {
  /** One entry per GPU on the node. */
  evidences: GpuDeviceEvidence[];
}

/**
 * Combined CVM attestation input with vTPM and optional GPU evidence.
 */
model CvmSnpAttestationInput {
  /** vTPM/SNP attestation evidence. */
  vtpm: VtpmAttestationInput;

  /** GPU attestation evidence, present only when a GPU is available. */
  gpu?: GpuAttestationEvidence;

  /** Base64-encoded canonical JSON user data document whose SHA-256 hash is bound to user-data[0:32]. */
  userDataDocument: string;
}

/**
 * vTPM attestation input with evidence and nonce.
 */
model VtpmAttestationInput {
  /** The vTPM attestation evidence. */
  evidence: VtpmAttestationEvidence;

  /** Nonce for replay protection. */
  nonce: string;
}
