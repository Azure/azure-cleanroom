import * as ccfapp from "@microsoft/ccf-app";
import { OidcIssuerInfo } from "../../models";
import {
  getGovIssuerUrl,
  getMemberTenantIdIssuerUrl,
  getUserTenantIdIssuerUrl,
  isOidcIssuerEnabled
} from "./issuer";
import { getCallerTenantId, validateCallerAuthorized } from "../../utils/utils";
import { ErrorResponse } from "../../utils/ErrorResponse";

export function getOidcIssuerInfo(
  request: ccfapp.Request
): ccfapp.Response<OidcIssuerInfo> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const info: OidcIssuerInfo = {
    enabled: isOidcIssuerEnabled()
  };

  const issuerUrl = getGovIssuerUrl();
  if (issuerUrl) {
    info.issuerUrl = issuerUrl;
  }

  // Get tenant ID based on auth policy.
  const tenantId = getCallerTenantId(request);

  if (tenantId) {
    // Return the issuer URL matching the caller type.
    const tenantIdIssuerUrl =
      request.caller.policy === "jwt" || request.caller.policy === "user_cert"
        ? getUserTenantIdIssuerUrl(tenantId)
        : getMemberTenantIdIssuerUrl(tenantId);
    if (tenantIdIssuerUrl) {
      info.tenantData = {
        tenantId: tenantId,
        issuerUrl: tenantIdIssuerUrl
      };
    }
  }
  return { statusCode: 200, body: info };
}
