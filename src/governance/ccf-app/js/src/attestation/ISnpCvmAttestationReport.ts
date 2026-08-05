// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Attestation claims used for cleanroom policy matching after the verifier has
// accepted the evidence. CPU PCR values come from the verified vTPM evidence;
// GPU claims are normalized and verifier-owned, not copied from raw GPU input.
export interface ISnpCvmAttestationReport {
  // CPU claims — PCR values from vTPM attestation (SHA-256 digests).
  pcr0?: string;
  pcr1?: string;
  pcr2?: string;
  pcr3?: string;
  pcr4?: string;
  pcr5?: string;
  pcr6?: string;
  pcr7?: string;
  pcr8?: string;
  pcr9?: string;
  pcr10?: string;
  pcr11?: string;
  pcr12?: string;
  pcr13?: string;
  pcr14?: string;
  pcr15?: string;
  pcr16?: string;
  pcr17?: string;
  pcr18?: string;
  pcr19?: string;
  pcr20?: string;
  pcr21?: string;
  pcr22?: string;
  pcr23?: string;

  // GPU claims — normalized verifier output.
  gpuCount?: number;
  gpuRIMAppraisal?: boolean;
  gpuSecureBoot?: boolean;
  gpuDebugDisabled?: boolean;
  gpuDriverRIM?: boolean;
  gpuVbiosRIM?: boolean;
}
