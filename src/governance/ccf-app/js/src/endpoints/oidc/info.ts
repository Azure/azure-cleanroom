import * as ccfapp from "@microsoft/ccf-app";
import { OidcIssuerInfo } from "../../models/openidmodels";
import {
  getGovIssuerUrl,
  getTenantIdIssuerUrl,
  isOidcIssuerEnabled
} from "./issuer";
import { getCallerId, getTenantId } from "../../utils/utils";

export function getOidcIssuerInfo(
  request: ccfapp.Request
): ccfapp.Response<OidcIssuerInfo> {
  const info: OidcIssuerInfo = {
    enabled: isOidcIssuerEnabled()
  };

  const issuerUrl = getGovIssuerUrl();
  if (issuerUrl) {
    info.issuerUrl = issuerUrl;
  }

  const tenantId = getTenantId(getCallerId(request));
  if (tenantId) {
    const tenantIdIssuerUrl = getTenantIdIssuerUrl(tenantId);
    if (tenantIdIssuerUrl) {
      info.tenantData = {
        tenantId: tenantId,
        issuerUrl: tenantIdIssuerUrl
      };
    }
  }
  return { statusCode: 200, body: info };
}
