import "./kvstoremodels.tsp";

namespace Cleanroom.Governance;

/**
 * List user invitations response
 */
model ListUserInvitationsResponse {
  /** Array of user invitations */
  value: GetUserInvitationResponse[];
}

/**
 * Get user invitation response
 */
model GetUserInvitationResponse {
  /** Invitation ID */
  invitationId: string;

  /** Account type */
  accountType: string;

  /** Tenant ID */
  tenantId?: string;

  /** Claims */
  claims: unknown;

  /** Invitation status */
  status?: string;

  /** User information */
  userInfo?: UserInfo;
}
