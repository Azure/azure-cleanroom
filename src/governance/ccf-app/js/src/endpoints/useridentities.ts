import * as ccfapp from "@microsoft/ccf-app";
import { UserIdentityStoreItem } from "../models/kvstoremodels";
import { validateCallerAuthorized } from "../utils/utils";
import { ErrorResponse } from "../models/errorresponse";
import {
  GetUserIdentityResponse,
  ListUserIdentitiesResponse
} from "../models/useridentitiesmodel";

// Code adapted from https://raw.githubusercontent.com/microsoft/ccf-app-samples/main/auditable-logging-app/src/endpoints/log.ts

const userIdentityStore = ccfapp.typedKv(
  "public:ccf.gov.user_identities",
  ccfapp.string,
  ccfapp.json<UserIdentityStoreItem>()
);

export function listUserIdentities(
  request: ccfapp.Request
):
  | ccfapp.Response<ListUserIdentitiesResponse>
  | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const entries: GetUserIdentityResponse[] = [];
  userIdentityStore.forEach((v, k) => {
    entries.push(toGetUserReponse(k, v));
  });

  return {
    body: {
      value: entries
    }
  };
}

export function getUserIdentity(
  request: ccfapp.Request
): ccfapp.Response<GetUserIdentityResponse> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const userIdentityId = request.params.identityId;
  const userIdentity = userIdentityStore.get(userIdentityId);
  if (userIdentity === undefined) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "UserIdentityNotFound",
        `User identity with ID ${userIdentityId} not found.`
      )
    };
  }
  const userIdentityResonse = toGetUserReponse(userIdentityId, userIdentity);
  return {
    statusCode: 200,
    body: userIdentityResonse
  };
}

export function isActiveUser(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  return {
    statusCode: 200,
    body: {
      active: true
    }
  };
}

function toGetUserReponse(
  userId: string,
  userItem: UserIdentityStoreItem
): GetUserIdentityResponse {
  const userIdentityResonse: GetUserIdentityResponse = {
    id: userId,
    accountType: userItem.accountType,
    invitationId: userItem.invitationId,
    data: userItem.data
  };

  return userIdentityResonse;
}
