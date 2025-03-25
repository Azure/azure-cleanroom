import * as ccfapp from "@microsoft/ccf-app";
import { DiscoveryResponse } from "../../models/openidmodels";
import { getGovIssuerUrl } from "./issuer";

export function getConfiguration(): ccfapp.Response<DiscoveryResponse> {
  // If no issuer url was proposed yet then return a placeholder value. Don't fail the call as
  // clients can be invoking the API endpoint to get the discovery document and set their own
  // value for issuer and host the discovery document at that url.
  const issuerUrl = getGovIssuerUrl() ?? "{placeholder}";
  return {
    body: {
      issuer: issuerUrl,
      jwks_uri: issuerUrl + "/keys",
      response_types_supported: ["id_token"],
      id_token_signing_alg_values_supported: ["RS256"]
    }
  };
}
