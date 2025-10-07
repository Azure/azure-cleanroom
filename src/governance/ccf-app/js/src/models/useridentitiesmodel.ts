export interface ListUserIdentitiesResponse {
  value: GetUserIdentityResponse[];
}

export interface GetUserIdentityResponse {
  id: string;
  accountType: string;
  invitationId: string;
  data: any;
}
