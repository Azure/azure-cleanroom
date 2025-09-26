import * as ccfapp from "@microsoft/ccf-app";
import { ccf } from "@microsoft/ccf-app/global";
import {
  AcceptedUserInvitationStoreItem,
  UserInvitationInfoStoreItem,
  UserInvitationStoreItem
} from "../models/kvstoremodels";
import { getIssuerType } from "../utils/utils";
import { ErrorResponse } from "../models/errorresponse";
import {
  GetUserInvitationResponse,
  ListUserInvitationsResponse
} from "../models/userinvitationmodels";

const userInvitations = ccfapp.typedKv(
  "public:ccf.gov.user_invitations",
  ccfapp.string,
  ccfapp.json<UserInvitationStoreItem>()
);
const userInvitationsInfo = ccfapp.typedKv(
  "public:user_invitations_info",
  ccfapp.string,
  ccfapp.json<UserInvitationInfoStoreItem>()
);
const acceptedUserInvitations = ccfapp.typedKv(
  "public:ccf.gov.accepted_user_invitations",
  ccfapp.string,
  ccfapp.json<AcceptedUserInvitationStoreItem>()
);

export function listInvitations(): ccfapp.Response<ListUserInvitationsResponse> {
  const entries: GetUserInvitationResponse[] = [];
  userInvitations.forEach((v, k) => {
    entries.push(toGetUserInvitationResponse(k, v));
  });

  return {
    body: {
      value: entries
    }
  };
}

export function getInvitation(
  request: ccfapp.Request
): ccfapp.Response<ErrorResponse> | ccfapp.Response<GetUserInvitationResponse> {
  const invitationId = request.params.invitationId;
  const invitation = userInvitations.get(invitationId);
  if (invitation === undefined) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "InvitationNotFound",
        `Invitation with ID ${invitationId} not found.`
      )
    };
  }
  const invitationItem = toGetUserInvitationResponse(invitationId, invitation);
  return {
    statusCode: 200,
    body: invitationItem
  };
}

export function acceptInvitation(
  request: ccfapp.Request
): ccfapp.Response<ErrorResponse> {
  const invitationId = request.params.invitationId;
  const invitation = userInvitations.get(invitationId);
  if (invitation === undefined) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "InvitationNotFound",
        `Invitation with ID ${invitationId} not found.`
      )
    };
  }

  const invitationInfo = userInvitationsInfo.get(invitationId);
  if (invitationInfo !== undefined && invitationInfo.status !== "Open") {
    return {
      statusCode: 409,
      body: new ErrorResponse(
        "InvitationNotOpen",
        `Invitation with ID ${invitationId} is not open. Status is: ${invitationInfo.status}`
      )
    };
  }

  const caller = request.caller as ccfapp.JwtAuthnIdentity;
  interface Payload {
    oid?: string;
    iss: string;
    tid?: string;
    ver: string;
  }

  const jwtPayload = caller.jwt.payload as Payload;

  if (jwtPayload.oid === undefined) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "OidClaimMissing",
        `Incoming token must have oid claim.`
      )
    };
  }

  const incomingClaims = jwtPayload;
  const expectedClaims = getUserInvitationClaims(invitationId);
  if (Object.keys(expectedClaims).length === 0) {
    throw Error(
      "The invitation claims is missing. Please update the invitation with a few claims."
    );
  }

  const issuerType = getIssuerType(jwtPayload.iss, jwtPayload.tid);
  for (let inx = 0; inx < Object.keys(expectedClaims).length; inx++) {
    const key = Object.keys(expectedClaims)[inx];
    let incomingKey = key;

    // check if key is in incoming token.
    if (
      key == "preferred_username" &&
      issuerType == "EntraID" &&
      jwtPayload.ver == "1.0"
    ) {
      // Incase of v1.0 token format, preferred_username is not present. Instead using "upn" claim.
      incomingKey = "upn";
    }

    const incomingValue = incomingClaims[incomingKey];
    const expectedValue = expectedClaims[key];
    const isUndefined = typeof incomingValue === "undefined";
    console.log(
      `Checking key ${key}, typeof incomingValue: ${typeof incomingValue}, ` +
        `isUndefined: ${isUndefined}, incoming value: ${incomingValue}, expectedValue: ${expectedValue}`
    );
    if (isUndefined) {
      console.log(`Claim ${incomingKey} is missing from incoming token.`);
      return {
        statusCode: 403,
        body: new ErrorResponse(
          "TokenClaimMissing",
          `Missing claim in incoming token: ${incomingKey}`
        )
      };
    }
    if (
      expectedValue.filter((p) => {
        console.log(`Check if claim value ${p} === ${incomingValue}`);
        return p === incomingValue;
      }).length === 0
    ) {
      return {
        statusCode: 403,
        body: new ErrorResponse(
          "TokenClaimMismatch",
          `Incoming claim ${key}, value ${incomingValue} does not match expected values: ${expectedValue}`
        )
      };
    }
  }

  // If we reach here, the invitation is accepted. Set status as accepted in userInvitationsInfo.
  userInvitationsInfo.set(invitationId, {
    status: "Accepted",
    userInfo: {
      userId: jwtPayload.oid,
      data: {
        tenantId: jwtPayload.tid
      }
    }
  });
  return {};
}

function getUserInvitationClaims(invitationId: string) {
  const result = {};
  const invitationClaimsMap = ccf.kv[getInvitationClaimsMapName(invitationId)];
  invitationClaimsMap.forEach((values: ArrayBuffer, key: ArrayBuffer) => {
    const kvKey = ccf.bufToStr(key);
    const kvValue = JSON.parse(ccf.bufToStr(values));
    result[kvKey] = kvValue;
    console.log(
      `invitation claim item with key: ${kvKey} and value: ${kvValue}`
    );
  });
  console.log(
    `Resulting invitation claims: ${JSON.stringify(
      result
    )}, keys: ${Object.keys(result)}, keys: ${Object.keys(result).length}`
  );
  return result;
}

function getInvitationClaimsMapName(invitationId: string): string {
  return "public:ccf.gov.user_invitations-" + invitationId;
}

function toGetUserInvitationResponse(
  invitationId: string,
  invitation: UserInvitationStoreItem
): GetUserInvitationResponse {
  const invitationItem: GetUserInvitationResponse = {
    invitationId: invitationId,
    accountType: invitation.accountType,
    claims: getUserInvitationClaims(invitationId)
  };
  const invitationInfo = userInvitationsInfo.get(invitationId);
  if (invitationInfo !== undefined) {
    invitationItem.userInfo = invitationInfo.userInfo;
  }

  if (acceptedUserInvitations.has(invitationId)) {
    invitationItem.status = "Finalized";
  } else if (invitationInfo !== undefined) {
    invitationItem.status = invitationInfo.status;
  } else {
    invitationItem.status = "Open";
  }

  return invitationItem;
}
