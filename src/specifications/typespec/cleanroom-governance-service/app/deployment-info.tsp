import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/deploymentinfo")
interface DeploymentInfo {
  /** 
    getDeploymentInfo
    endpoints/deployment-info.js
    POST
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @post
  getDeploymentInfo(@path contractId: string): GetDeploymentInfoResponse;
}
