import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * UserIdentities operations
 */
interface UserIdentities {
  /** listUserIdentities (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  listUserIdentities(): ListUserIdentitiesResponse;
  /** checkUserIdentity (AuthPolicies: [jwt]) */
  @tag("auth:jwt")
  checkUserIdentity(): CheckUserIdentityResponse;
  /** getUserIdentity (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getUserIdentity(identityId: string): GetUserIdentityResponse;
}
