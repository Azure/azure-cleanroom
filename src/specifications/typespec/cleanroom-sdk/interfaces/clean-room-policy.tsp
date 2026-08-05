import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * CleanRoomPolicy operations
 */
interface CleanRoomPolicy {
  /** getCleanRoomPolicy (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getCleanRoomPolicy(contractId: string): GetCleanRoomPolicyResponse;
  /** listDelegatePolicies (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  listDelegatePolicies(contractId: string): ListDelegatePoliciesResponse;
  /** getDelegatePolicy (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getDelegatePolicy(contractId: string, delegateType: string, delegateId: string): GetDelegatePolicyResponse;
  /** setDelegatePolicy (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  setDelegatePolicy(contractId: string, delegateType: string, delegateId: string, request: SetDelegateCleanRoomPolicyRequest): EmptyResponse;
}
