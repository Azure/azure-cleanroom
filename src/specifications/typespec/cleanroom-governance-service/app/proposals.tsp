import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/proposals/{proposalId}/historical")
interface Proposals {
  /** 
    getProposalHistorical
    endpoints/proposals.js
    GET
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @get
  getProposalHistorical(@path proposalId: string): GetProposalResponse;
}
