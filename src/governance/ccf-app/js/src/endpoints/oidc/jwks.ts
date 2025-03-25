import * as ccfapp from "@microsoft/ccf-app";
import { JwksResponse } from "../../models/openidmodels";
import { ccf } from "@microsoft/ccf-app/global";
import { getSigningKey } from "./signingkey";
import { ErrorResponse } from "../../models/errorresponse";

export function getJwks():
  | ccfapp.Response<JwksResponse>
  | ccfapp.Response<ErrorResponse> {
  const signingKey = getSigningKey();
  if (!signingKey) {
    return {
      statusCode: 405,
      body: new ErrorResponse(
        "SigningKeyNotAvailable",
        "Propose enable_oidc_issuer and generate signing key before attempting to fetch it."
      )
    };
  }

  const jwk = ccf.crypto.pubRsaPemToJwk(signingKey.publicKey, signingKey.kid);
  return {
    body: {
      keys: [
        {
          alg: "RS256",
          use: "sig",
          kty: jwk.kty,
          kid: signingKey.kid,
          e: jwk.e,
          n: jwk.n
        }
      ]
    }
  };
}
