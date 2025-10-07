export interface ListUserInvitationsResponse {
  value: GetUserInvitationResponse[];
}

export interface GetUserInvitationResponse {
  invitationId: string;
  accountType: string;
  tenantId?: string;
  claims: any;
  status?: string;
  userInfo?: {
    userId: string;
    data: {
      tenantId?: string;
    };
  };
}
