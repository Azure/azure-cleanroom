import * as ccfapp from "@microsoft/ccf-app";
import { isCAEnabledInternal } from "./ca";
import { CAInfo, isCAEnabledRequest, isCAEnabledResponse } from "../../models";
import { getCASigningKey } from "./cakey";
import { findOpenProposals } from "../../utils/utils";
import { ErrorResponse } from "../../models/errorresponse";
import { verifySnpAttestation } from "../../attestation/snpattestation";

export function getCAInfo(request: ccfapp.Request): ccfapp.Response<CAInfo> {
  const contractId = request.params.contractId;
  const proposalIds = findOpenProposals("enable_ca", contractId);
  const info: CAInfo = {
    enabled: isCAEnabledInternal(contractId),
    proposalIds: proposalIds
  };

  if (info.enabled) {
    const item = getCASigningKey(contractId);
    if (item != null) {
      info.caCert = item.caCert;
      info.publicKey = item.publicKey;
    }
  }

  return { statusCode: 200, body: info };
}

export function isCAEnabled(
  request: ccfapp.Request<isCAEnabledRequest>
): ccfapp.Response<isCAEnabledResponse> | ccfapp.Response<ErrorResponse> {
  const contractId = request.params.contractId;
  const body = request.body.json();
  if (!body.attestation) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "AttestationMissing",
        "Attestation payload must be supplied."
      )
    };
  }

  // Validate attestation report.
  try {
    verifySnpAttestation(contractId, body.attestation);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("VerifySnpAttestationFailed", e.message)
    };
  }

  const info = {
    enabled: isCAEnabledInternal(contractId)
  }

  return { statusCode: 200, body: info };

}
