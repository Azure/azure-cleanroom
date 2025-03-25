import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../models/errorresponse";
import { GetCleanRoomPolicyResponse } from "../models/cleanroompolicymodels";
import { findOpenProposals, getCleanRoomPolicyProps } from "../utils/utils";

export function getCleanRoomPolicy(
  request: ccfapp.Request
):
  | ccfapp.Response<GetCleanRoomPolicyResponse>
  | ccfapp.Response<ErrorResponse> {
  const contractId = request.params.contractId;
  const policy = getCleanRoomPolicyProps(contractId);
  const proposalIds = findOpenProposals("set_clean_room_policy", contractId);

  return {
    statusCode: 200,
    body: {
      proposalIds: proposalIds,
      policy: policy
    }
  };
}
