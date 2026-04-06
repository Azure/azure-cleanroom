import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../../utils/ErrorResponse";
import {
  getCallerTenantId,
  checkValidUrl,
  validateCallerAuthorized
} from "../../utils/utils";

const oidcIssuerGovStore = ccfapp.typedKv(
  "public:ccf.gov.oidc_issuer",
  ccfapp.string,
  ccfapp.string
);
const memberTenantIssuerUrlStore = ccfapp.typedKv(
  "public:oidc_issuer.tenantid_issuer_url",
  ccfapp.string,
  ccfapp.string
);

// Per-tenant issuer URL set by a user (JWT or user_cert auth).
const userTenantIssuerUrlStore = ccfapp.typedKv(
  "public:oidc_issuer.user_tenantid_issuer_url",
  ccfapp.string,
  ccfapp.string
);

export interface SetIssuerUrlRequest {
  url: string;
}

export function setIssuerUrl(
  request: ccfapp.Request<SetIssuerUrlRequest>
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
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

  // Get tenant ID based on auth policy.
  const tenantId = getCallerTenantId(request);

  if (!tenantId) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "NoTenantId",
        "Cannot set issuer as tenantId information was not found in registered identity."
      )
    };
  }

  // Store in appropriate table based on caller type.
  // Members write to member table, users (JWT and user_cert) write to user table.
  if (
    request.caller.policy === "jwt" ||
    request.caller.policy === "user_cert"
  ) {
    userTenantIssuerUrlStore.set(tenantId, body.url);
  } else {
    memberTenantIssuerUrlStore.set(tenantId, body.url);
  }

  return { statusCode: 200 };
}

export function getUserTenantIdIssuerUrl(tenantId: string): string {
  if (userTenantIssuerUrlStore.has(tenantId)) {
    return userTenantIssuerUrlStore.get(tenantId);
  }

  return null;
}

export function getMemberTenantIdIssuerUrl(tenantId: string): string {
  if (memberTenantIssuerUrlStore.has(tenantId)) {
    return memberTenantIssuerUrlStore.get(tenantId);
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
