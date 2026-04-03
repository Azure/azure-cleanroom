// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Generic attestation result returned by any IAttestationVerifier.
export interface AttestationResult {
  // The report data value from the attestation (hex string).
  reportData: string;
}

// Interface that abstracts attestation verification so that different TEE
// platforms (CACI, CVM, …) can supply their own implementation.
export interface IAttestationVerifier {
  // Verify the attestation evidence and policy claims for a contract.
  // Returns the attestation result on success; throws on failure.
  verifyAttestation(
    contractId: string,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    attestation: any,
    delegatedPolicies?: string[]
  ): AttestationResult;

  // Verify that the report data in the attestation result matches the
  // expected data (e.g. a public key). Throws on mismatch.
  verifyReportData(attestationResult: AttestationResult, data: string): void;
}
