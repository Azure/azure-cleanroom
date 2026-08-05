import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts")
interface Contracts {
  /** 
    listContracts
    endpoints/contracts.js
    GET
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @get
  listContracts(): ListContractsResponse;

  /** 
    putContract
    endpoints/contracts.js
    PUT
    AuthPolicies: [member_cert]
  */
  @tag("auth:member_cert")
  @put
  @route("/{contractId}")
  putContract(
    @path contractId: string,
    @body request: PutContractRequest,
  ): EmptyResponse;

  /** 
    getContract
    endpoints/contracts.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/{contractId}")
  getContract(@path contractId: string): GetContractResponse;
}
