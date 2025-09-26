import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../models/errorresponse";
import { DeploymentSpecItem } from "../models";
import { GetDeploymentSpecResponse } from "../models/deploymentspecmodels";
import { findOpenProposals, validateCallerAuthorized } from "../utils/utils";

const deploymentSpecsStore = ccfapp.typedKv(
  "public:ccf.gov.deployment_specs",
  ccfapp.string,
  ccfapp.json<DeploymentSpecItem>()
);

export function getDeploymentSpec(
  request: ccfapp.Request
): ccfapp.Response<GetDeploymentSpecResponse> | ccfapp.Response<ErrorResponse> {
  // No authorization check is added here as this endpoint is used by unauthenticated clients
  // to get the deployment spec and perform deployments.
  // TODO (gsinha): See if a user for fetching just the deployment spec can be created.
  // Also remember to add in app.json authn_policies = ["member_cert", "user_cert", "jwt"] if
  //  adding auth.
  // const error = validateCallerAuthorized(request);
  // if (error !== undefined) {
  //   return error;
  // }

  const contractId = request.params.contractId;
  const proposalIds = findOpenProposals("set_deployment_spec", contractId);

  if (deploymentSpecsStore.has(contractId)) {
    const specItem = deploymentSpecsStore.get(contractId);
    return {
      body: {
        proposalIds: proposalIds,
        data: specItem.data
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
