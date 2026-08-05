import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * UserInvitations operations
 */
interface UserInvitations {
  /** listInvitations (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  listInvitations(): ListUserInvitationsResponse;
  /** getInvitation (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getInvitation(invitationId: string): GetUserInvitationResponse;
  /** acceptInvitation (AuthPolicies: [jwt]) */
  @tag("auth:jwt")
  acceptInvitation(invitationId: string): EmptyResponse;
}
