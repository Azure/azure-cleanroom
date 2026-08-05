import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/oauth/token")
interface Token {
  /** 
    getToken
    endpoints/token.js
    POST
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @post
  getToken(
    @path contractId: string,
    @body request: GetTokenRequest,
  ): GetTokenResponse;
}
