// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import {
  AttestationResult,
  IAttestationVerifier
} from "./IAttestationVerifier";
import { SnpEvidence, verifySnpAttestation } from "./snpattestation";
import { verifyReportData as verifyCaciReportData } from "../utils/utils";

// Attestation verifier for the CACI (Confidential ACI) TEE platform.
// Delegates to the existing verifySnpAttestation and verifyReportData helpers.
export class CaciAttestationVerifier implements IAttestationVerifier {
  verifyAttestation(
    contractId: string,
    attestation: SnpEvidence,
    delegatedPolicies?: string[]
  ): AttestationResult {
    const snpResult = verifySnpAttestation(
      contractId,
      attestation,
      delegatedPolicies
    );
    return {
      reportData: snpResult.attestation.report_data
    };
  }

  verifyReportData(attestationResult: AttestationResult, data: string): void {
    verifyCaciReportData(attestationResult.reportData, data);
  }
}
