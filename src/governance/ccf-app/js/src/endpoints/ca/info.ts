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
