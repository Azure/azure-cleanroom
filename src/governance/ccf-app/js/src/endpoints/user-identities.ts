import * as ccfapp from "@microsoft/ccf-app";
import { UserIdentityStoreItem } from "../models";
import { validateCallerAuthorized } from "../utils/utils";
import { ErrorResponse } from "../utils/ErrorResponse";
import {
  CheckUserIdentityResponse,
  GetUserIdentityResponse,
  ListUserIdentitiesResponse
} from "../models";

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
    entries.push(toGetUserIdentityResponse(k, v));
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
  const userIdentityResponse = toGetUserIdentityResponse(userIdentityId, userIdentity);
  return {
    statusCode: 200,
    body: userIdentityResponse
  };
}

export function checkUserIdentity(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const checkUserIdentityResponse: CheckUserIdentityResponse = {
    active: true
  };

  return {
    statusCode: 200,
    body: checkUserIdentityResponse
  };
}

function toGetUserIdentityResponse(
  userId: string,
  userItem: UserIdentityStoreItem
): GetUserIdentityResponse {
  const userIdentityResponse: GetUserIdentityResponse = {
    id: userId,
    accountType: userItem.accountType,
    invitationId: userItem.invitationId,
    data: userItem.data
  };

  return userIdentityResponse;
}
