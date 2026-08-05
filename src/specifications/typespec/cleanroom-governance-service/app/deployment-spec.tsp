import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/deploymentspec")
interface DeploymentSpec {
  /** 
    getDeploymentSpec
    endpoints/deployment-spec.js
    POST
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @post
  getDeploymentSpec(@path contractId: string): GetDeploymentSpecResponse;
}
