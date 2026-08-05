import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/users/invitations")
interface UserInvitations {
  /** 
    listInvitations
    endpoints/user-invitations.js
    GET
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @get
  listInvitations(): ListUserInvitationsResponse;

  /** 
    getInvitation
    endpoints/user-invitations.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/{invitationId}")
  getInvitation(@path invitationId: string): GetUserInvitationResponse;

  /** 
    acceptInvitation
    endpoints/user-invitations.js
    POST
    AuthPolicies: [jwt]
  */
  @tag("auth:jwt")
  @post
  @route("/{invitationId}/accept")
  acceptInvitation(@path invitationId: string): EmptyResponse;
}
