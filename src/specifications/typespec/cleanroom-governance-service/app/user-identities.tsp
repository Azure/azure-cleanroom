import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/users/identities")
interface UserIdentities {
  /** 
    listUserIdentities
    endpoints/user-identities.js
    GET
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @get
  listUserIdentities(): ListUserIdentitiesResponse;

  /** 
    checkUserIdentity
    endpoints/user-identities.js
    POST
    AuthPolicies: [jwt]
  */
  @tag("auth:jwt")
  @post
  @route("/self/status")
  checkUserIdentity(): CheckUserIdentityResponse;

  /** 
    getUserIdentity
    endpoints/user-identities.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/{identityId}")
  getUserIdentity(@path identityId: string): GetUserIdentityResponse;
}
