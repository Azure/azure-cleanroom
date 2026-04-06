import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../utils/ErrorResponse";
import { DeploymentInfoItem } from "../models";
import { GetDeploymentInfoResponse } from "../models";
import { findOpenProposals, validateCallerAuthorized } from "../utils/utils";

const deploymentInfoStore = ccfapp.typedKv(
  "public:ccf.gov.deployment_info",
  ccfapp.string,
  ccfapp.json<DeploymentInfoItem>()
);

export function getDeploymentInfo(
  request: ccfapp.Request
): ccfapp.Response<GetDeploymentInfoResponse> | ccfapp.Response<ErrorResponse> {
  // No authorization check is added here as this endpoint is used by unauthenticated clients
  // to get the deployment information and consume deployments.
  // TODO (gsinha): See if a user for fetching just the deployment info can be created.
  // Also remember to add in app.json authn_policies = ["member_cert", "user_cert", "jwt"] if
  //  adding auth.
  // const error = validateCallerAuthorized(request);
  // if (error !== undefined) {
  //   return error;
  // }

  const contractId = request.params.contractId;
  const proposalIds = findOpenProposals("set_deployment_info", contractId);

  if (deploymentInfoStore.has(contractId)) {
    const infoItem = deploymentInfoStore.get(contractId);
    return {
      body: {
        proposalIds: proposalIds,
        data: infoItem.data
      }
    };
  }

  return {
    body: {
      proposalIds: proposalIds,
      data: {}
    }
  };
}
