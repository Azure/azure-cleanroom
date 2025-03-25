import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../../models/errorresponse";
import {
  getCallerId,
  getTenantId,
  isMember,
  checkValidUrl
} from "../../utils/utils";

const oidcIssuerGovStore = ccfapp.typedKv(
  "public:ccf.gov.oidc_issuer",
  ccfapp.string,
  ccfapp.string
);
const tenantIdIssuerUrlStore = ccfapp.typedKv(
  "public:oidc_issuer.tenantid_issuer_url",
  ccfapp.string,
  ccfapp.string
);

export interface SetIssuerUrlRequest {
  url: string;
}

export function setIssuerUrl(
  request: ccfapp.Request<SetIssuerUrlRequest>
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const callerId = getCallerId(request);
  if (!isMember(callerId)) {
    return {
      statusCode: 403,
      body: new ErrorResponse(
        "UnauthorizedCaller",
        "Caller is not authorized to invoke this enpoint."
      )
    };
  }

  const body = request.body.json();
  try {
    checkValidUrl(body.url);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("InvalidUrl", e.message)
    };
  }

  const tenantId = getTenantId(callerId);
  if (!tenantId) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "NoTenantIdMemberData",
        "Cannot set issuer as tenantId information was not found for this member."
      )
    };
  }

  tenantIdIssuerUrlStore.set(tenantId, body.url);
  return { statusCode: 200 };
}

export function getTenantIdIssuerUrl(tenantId: string): string {
  if (tenantIdIssuerUrlStore.has(tenantId)) {
    return tenantIdIssuerUrlStore.get(tenantId);
  }

  return null;
}

export function getGovIssuerUrl(): string {
  if (oidcIssuerGovStore.has("issuerUrl")) {
    return oidcIssuerGovStore.get("issuerUrl");
  }

  return null;
}

export function isOidcIssuerEnabled(): boolean {
  return oidcIssuerGovStore.get("enabled") === "true";
}

export function getLastGenerateSigningKeyReqdId(): string {
  return oidcIssuerGovStore.get("generateSigningKeyRequestId");
}

export function getGenerateSigningKeyKid(): string {
  return oidcIssuerGovStore.get("generateSigningKeyKid");
}
