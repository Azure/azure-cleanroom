import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/cleanroompolicy")
interface CleanRoomPolicy {
  /** 
    getCleanRoomPolicy
    endpoints/clean-room-policy.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  getCleanRoomPolicy(@path contractId: string): GetCleanRoomPolicyResponse;

  /** 
    listDelegatePolicies
    endpoints/clean-room-policy.js
    GET
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @get
  @route("/delegates")
  listDelegatePolicies(@path contractId: string): ListDelegatePoliciesResponse;

  /** 
    getDelegatePolicy
    endpoints/clean-room-policy.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/delegates/{delegateType}/{delegateId}")
  getDelegatePolicy(
    @path contractId: string,
    @path delegateType: string,
    @path delegateId: string,
  ): GetDelegatePolicyResponse;

  /** 
    setDelegatePolicy
    endpoints/clean-room-policy.js
    PUT
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @put
  @route("/delegates/{delegateType}/{delegateId}")
  setDelegatePolicy(
    @path contractId: string,
    @path delegateType: string,
    @path delegateId: string,
    @body request: SetDelegateCleanRoomPolicyRequest,
  ): EmptyResponse;
}
