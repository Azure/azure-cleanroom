// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import {
  AttestationResult,
  IAttestationVerifier
} from "./IAttestationVerifier";
import { CaciAttestationVerifier } from "./CaciAttestationVerifier";
import { CvmAttestationVerifier } from "./CvmAttestationVerifier";
import { ErrorResponse } from "../utils/ErrorResponse";

export const PlatformSnpCaci = "snp-caci";
export const PlatformSnpCvm = "snp-cvm";

// Returns an IAttestationVerifier for the given TEE platform.
// Defaults to snp-caci when platform is undefined or empty.
export function getAttestationVerifier(
  platform?: string
): IAttestationVerifier {
  const resolved = platform || PlatformSnpCaci;
  switch (resolved) {
    case PlatformSnpCaci:
      return new CaciAttestationVerifier();
    case PlatformSnpCvm:
      return new CvmAttestationVerifier();
    default:
      throw new Error(
        `Unknown TEE platform '${resolved}'. ` +
          `Supported values: ${PlatformSnpCaci}, ${PlatformSnpCvm}.`
      );
  }
}

// Convenience type for the body shape expected at call sites.
export interface AttestationBody {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  attestation: any;
  platform?: string;
}

// Performs both attestation verification and report-data verification in one
// call, returning an ErrorResponse on failure or the result on success.
export function verifyAttestationAndReportData(
  contractId: string,
  body: AttestationBody,
  getReportData: () => string,
  delegatedPolicies?: string[]
): { result?: AttestationResult; error?: ErrorResponse } {
  let verifier: IAttestationVerifier;
  try {
    verifier = getAttestationVerifier(body.platform);
  } catch (e) {
    return {
      error: new ErrorResponse("UnsupportedPlatform", e.message)
    };
  }

  let attestationResult: AttestationResult;
  try {
    attestationResult = verifier.verifyAttestation(
      contractId,
      body.attestation,
      delegatedPolicies
    );
  } catch (e) {
    return {
      error: new ErrorResponse("VerifySnpAttestationFailed", e.message)
    };
  }

  try {
    verifier.verifyReportData(attestationResult, getReportData());
  } catch (e) {
    return {
      error: new ErrorResponse("ReportDataMismatch", e.message)
    };
  }

  return { result: attestationResult };
}
