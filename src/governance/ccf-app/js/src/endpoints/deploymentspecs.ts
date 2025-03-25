import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../models/errorresponse";
import { DeploymentSpecItem } from "../models";
import { GetDeploymentSpecResponse } from "../models/deploymentspecmodels";
import { findOpenProposals } from "../utils/utils";

const deploymentSpecsStore = ccfapp.typedKv(
  "public:ccf.gov.deployment_specs",
  ccfapp.string,
  ccfapp.json<DeploymentSpecItem>()
);

export function getDeploymentSpec(
  request: ccfapp.Request
): ccfapp.Response<GetDeploymentSpecResponse> | ccfapp.Response<ErrorResponse> {
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
