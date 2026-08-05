namespace Azure.Cleanroom.Governance;

/**
 * List user identities response
 */
model ListUserIdentitiesResponse {
  /** Array of user identities */
  value: GetUserIdentityResponse[];
}

/**
 * Get user identity response
 */
model GetUserIdentityResponse {
  /** User ID */
  id: string;

  /** Account type */
  accountType: string;

  /** Invitation ID */
  invitationId: string;

  /** User data */
  data: unknown;
}

/**
 * Get user identity response
 */
model CheckUserIdentityResponse {
  /** User status */
  active: boolean;
}
