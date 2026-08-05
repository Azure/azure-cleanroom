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
import { Base64 } from "js-base64";

const cvmReportDataHexLength = 128;
const cvmPayloadHashHexLength = 64;

// Attestation verifier for the CVM (Confidential VM) TEE platform.
// Delegates to cleanroom.attestation.verifyCvmSnpAttestation for all evidence
// verification (vTPM/SNP, GPU, and user data document binding). The CGS app
// only handles policy matching and payload binding.
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
      const failedChecks = result.checks
        .filter((c) => !c.result.passed)
        .map((c) => `${c.id}: ${c.result.error || c.result.detail || "unknown"}`)
        .join("; ");
      throw new Error(
        `CVM SNP attestation verification failed. Failed checks: ${failedChecks}`
      );
    }

    // The PCR values and GPU claims are the attestation claims for CVM. After
    // verification succeeds the PCR values from the evidence are trusted
    // (pcrDigest check confirms they match the TPM quote) and GPU claims are
    // verifier-owned (user data document binding + RIM appraisal).
    const claimsProvider = new SnpCvmAttestationClaims(attestation, result);
    const attestationClaims = claimsProvider.getClaims();
    verifyPolicyClaims(contractId, attestationClaims, delegatedPolicies);

    // Use the validated report data from the verifier result. The verifier
    // has already proven the user data document is bound to the hardware-
    // signed SNP report via the metadataBinding check.
    if (!result.reportData) {
      throw new Error(
        "CVM SNP attestation result is missing reportData."
      );
    }

    const reportDataBytes = Base64.toUint8Array(result.reportData);
    const reportDataHex = hex(reportDataBytes.buffer as ArrayBuffer).toUpperCase();

    return {
      reportData: reportDataHex,
      gpuCount: result.gpuClaims?.gpuCount
    };
  }

  verifyReportData(attestationResult: AttestationResult, data: string): void {
    // The report data is the payload from the /snp/attest request.
    // The first 32 bytes (64 hex chars) are the payload hash.
    const reportDataHex = attestationResult.reportData.toUpperCase();

    if (reportDataHex.length !== cvmReportDataHexLength) {
      throw new Error(
        "Unexpected report data hex length: " + reportDataHex.length
      );
    }

    const hashHex = hex(
      ccf.crypto.digest("SHA-256", ccf.strToBuf(data))
    ).toUpperCase();
    if (hashHex.length !== cvmPayloadHashHexLength) {
      throw new Error(
        "Unexpected SHA-256 digest hex length: " + hashHex.length
      );
    }

    const actualPayloadHash = reportDataHex.slice(0, cvmPayloadHashHexLength);
    if (actualPayloadHash !== hashHex) {
      console.log(
        "Payload hash mismatch. reportData[0:32]: '" +
          actualPayloadHash +
          "', SHA256(data): '" +
          hashHex +
          "'"
      );
      throw new Error(
        "Attestation report data payload hash did not match calculated value."
      );
    }

    console.log(
      "Successfully verified expected payload hash against CVM " +
        "report data. reportData: " +
        reportDataHex
    );
  }
}

// Helper – convert ArrayBuffer to hex string.
function hex(buf: ArrayBuffer): string {
  return Array.from(new Uint8Array(buf))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}
