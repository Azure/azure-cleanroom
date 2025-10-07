import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../models/errorresponse";
import { GetCleanRoomPolicyResponse } from "../models/cleanroompolicymodels";
import {
  findOpenProposals,
  getContractCleanRoomPolicyProps,
  validateCallerAuthorized
} from "../utils/utils";

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
