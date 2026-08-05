import * as ccfapp from "@microsoft/ccf-app";
import { DiscoveryResponse } from "../../models";
import { getGovIssuerUrl } from "./issuer";

export function getConfiguration(): ccfapp.Response<DiscoveryResponse> {
  // If no issuer url was proposed yet then return a placeholder value. Don't fail the call as
  // clients can be invoking the API endpoint to get the discovery document and set their own
  // value for issuer and host the discovery document at that url.
  const issuerUrl = getGovIssuerUrl() ?? "{placeholder}";
  // The DiscoveryResponse interface generated from TypeSpec uses fields like
  // `jwks_Uri` while the OIDC discovery contract on the wire requires
  // `jwks_uri`. Emit the wire-shape literal directly; cast through unknown
  // to silence the TypeSpec-generated property name mismatch.
  const body = {
    issuer: issuerUrl,
    jwks_uri: issuerUrl + "/keys",
    response_types_supported: ["id_token"],
    id_token_signing_alg_values_supported: ["RS256"]
  } as unknown as DiscoveryResponse;
  return { body };
}
