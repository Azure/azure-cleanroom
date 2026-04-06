import * as ccfapp from "@microsoft/ccf-app";
import { ccf } from "@microsoft/ccf-app/global";
import {
  GetCleanRoomPolicyResponse,
  ListDelegatePoliciesResponse,
  ListDelegatePolicyResponse,
  SetDelegateCleanRoomPolicyRequest,
  SetDelegateCleanRoomPolicyRequestData
} from "../models";
import {
  findOpenProposals,
  validateCallerAuthorized,
  b64ToBuf,
  verifySignature,
  getContractCleanRoomPolicyProps,
  getDelegateCleanRoomPolicyProps,
  setDelegateCleanRoomPolicyMap,
  toDelegatePolicyKey
} from "../utils/utils";
import { verifyAttestationAndReportData } from "../attestation/attestationVerifierFactory";
import { ErrorResponse } from "../utils/ErrorResponse";
import { Base64 } from "js-base64";
import { DelegatePolicyInfoItem } from "../models";

export function getCleanRoomPolicy(
  request: ccfapp.Request
):
  | ccfapp.Response<GetCleanRoomPolicyResponse>
  | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const contractId = request.params.contractId;
  const policy = getContractCleanRoomPolicyProps(contractId);
  const proposalIds = findOpenProposals("set_clean_room_policy", contractId);

  return {
    statusCode: 200,
    body: {
      proposalIds: proposalIds,
      policy: policy
    }
  };
}

export function setDelegatePolicy(
  request: ccfapp.Request<SetDelegateCleanRoomPolicyRequest>
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
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

  // First validate attestation report and report data.
  const { error } = verifyAttestationAndReportData(contractId, body, () =>
    Base64.decode(body.encrypt.publicKey)
  );
  if (error) {
    return {
      statusCode: 400,
      body: error
    };
  }

  // Attestation report and report data values are verified. Now check the signature.
  const data: ArrayBuffer = b64ToBuf(body.data);
  try {
    verifySignature(body.sign, data);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("SignatureMismatch", e.message)
    };
  }

  // Attestation report, report data and payload signature are verified.
  const rawData = JSON.parse(ccf.bufToStr(data));
  const requestData: SetDelegateCleanRoomPolicyRequestData = {
    delegateType: rawData.type,
    claims: rawData.claims,
    policyType: rawData.policyType
  };
  const delegateType = request.params.delegateType;
  const delegateId = request.params.delegateId;
  setDelegateCleanRoomPolicyMap(
    contractId,
    delegateType,
    delegateId,
    requestData
  );
  return { statusCode: 200 };
}

export function getDelegatePolicy(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const contractId = request.params.contractId;
  const delegateType = request.params.delegateType;
  const delegateId = request.params.delegateId;
  const policyKey = toDelegatePolicyKey(contractId, delegateType, delegateId);
  const delegateCleanRoomPolicy = getDelegateCleanRoomPolicyProps(policyKey);
  return { statusCode: 200, body: { claims: delegateCleanRoomPolicy } };
}

export function listDelegatePolicies(
  request: ccfapp.Request
):
  | ccfapp.Response<ListDelegatePoliciesResponse>
  | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const contractId = request.params.contractId;
  const delegatePolicyListStore = ccfapp.typedKv(
    `public:policies.cleanroom-delegates-list-${contractId}`,
    ccfapp.string,
    ccfapp.json<DelegatePolicyInfoItem>()
  );

  let policySet: ListDelegatePolicyResponse[] = [];
  delegatePolicyListStore.forEach((v, k) => {
    const value: ListDelegatePolicyResponse = {
      delegateType: v.delegateType,
      delegateId: v.delegateId
    };
    policySet.push(value);
  });

  return {
    body: {
      value: policySet
    }
  };
}
