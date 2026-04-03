// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { ccf } from "@microsoft/ccf-app/global";
import {
  AttestationResult,
  IAttestationVerifier
} from "./IAttestationVerifier";
import { cleanroom } from "../global.cleanroom";
import { CvmSnpAttestationInput } from "../models";
import { verifyPolicyClaims } from "../utils/utils";
import { SnpCvmAttestationClaims } from "./SnpCvmAttestationClaims";

// Attestation verifier for the CVM (Confidential VM) TEE platform.
// Delegates to cleanroom.attestation.verifyCvmSnpAttestation for evidence
// verification and compares runtimeClaims["user-data"] for report data.
export class CvmAttestationVerifier implements IAttestationVerifier {
  verifyAttestation(
    contractId: string,
    attestation: CvmSnpAttestationInput,
    delegatedPolicies?: string[]
  ): AttestationResult {
    const result = cleanroom.attestation.verifyCvmSnpAttestation(
      JSON.stringify(attestation)
    );

    if (!result.verified) {
      const failedChecks = Object.entries(result.checks)
        .filter(([, v]) => !v.passed)
        .map(([k, v]) => `${k}: ${v.detail}`)
        .join("; ");
      throw new Error(
        `CVM SNP attestation verification failed. Failed checks: ${failedChecks}`
      );
    }

    // The PCR values are the attestation claims for CVM. After verification
    // succeeds the PCR values from the evidence are trusted (pcrDigest check
    // confirms they match the TPM quote). Match them against the cleanroom
    // policy which contains key/value pairs like pcr0->value, pcr1->value.
    const claimsProvider = new SnpCvmAttestationClaims(attestation);
    const attestationClaims = claimsProvider.getClaims();
    verifyPolicyClaims(contractId, attestationClaims, delegatedPolicies);

    const userData = result.runtimeClaims["user-data"];
    if (typeof userData !== "string") {
      throw new Error(
        "CVM SNP attestation result is missing runtimeClaims 'user-data'."
      );
    }

    return {
      reportData: userData as string
    };
  }

  verifyReportData(attestationResult: AttestationResult, data: string): void {
    // For CVM attestation the report data carried in runtimeClaims["user-data"]
    // is compared against sha256(data) zero-padded to 128 hex chars, identical
    // to the CACI report data comparison logic.
    const reportData = attestationResult.reportData.toUpperCase();

    if (reportData.length !== 128) {
      throw new Error(
        "Unexpected string length of runtimeClaims user-data: " +
          reportData.length
      );
    }

    let expectedReportData = hex(
      ccf.crypto.digest("SHA-256", ccf.strToBuf(data))
    ).toUpperCase();

    if (expectedReportData.length !== 64) {
      throw new Error(
        "Unexpected string length of expectedReportData: " +
          expectedReportData.length
      );
    }

    expectedReportData = expectedReportData.padEnd(128, "0");

    if (reportData !== expectedReportData) {
      console.log(
        "Report data value mismatch. runtimeClaims user-data: '" +
          reportData +
          "', calculated report_data: '" +
          expectedReportData +
          "',"
      );
      throw new Error(
        "Attestation runtimeClaims user-data value did not match calculated value."
      );
    }

    console.log(
      "Successfully verified expected report data value against CVM " +
        "runtimeClaims user-data. report_data: " +
        reportData
    );
  }
}

// Helper – convert ArrayBuffer to hex string.
function hex(buf: ArrayBuffer): string {
  return Array.from(new Uint8Array(buf))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}
