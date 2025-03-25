import * as ccfapp from "@microsoft/ccf-app";
import { ccf } from "@microsoft/ccf-app/global";
import { SigningKeyItem } from "../../models";
import { ErrorResponse } from "../../models/errorresponse";
import { isOidcIssuerEnabled, getLastGenerateSigningKeyReqdId, getGenerateSigningKeyKid } from "./issuer";

const signingKeyStore = ccfapp.typedKv(
  "oidc_issuer.signing_keys",
  ccfapp.string,
  ccfapp.json<SigningKeyItem>()
);

const signingKeyName = "signingKey";

export function getSigningKey(): SigningKeyItem {
  if (signingKeyStore.has(signingKeyName)) {
    return signingKeyStore.get(signingKeyName);
  }

  return null;
}

export function generateSigningKey(): ccfapp.Response {
  // Generate a signing key only if feature is enabled and a request to create a signing key
  // was proposed. We only want to create a signing key when signaled by the acceptance of the
  // enable_oidc_issuer/oidc_issuer_enable_rotate_signing_key proposal. This is tracked via
  // matching the requestId value kept in the governance table with the value kept in the private
  // signing key table. Repeated invocations of this API when there is no change in the requestId
  // will be ignored.
  if (!isOidcIssuerEnabled()) {
    return {
      statusCode: 405,
      body: new ErrorResponse(
        "OidcIssuerNotEnabled",
        "Oidc issuer support has not been enabled."
      )
    };
  }

  const reqId = getLastGenerateSigningKeyReqdId();
  const kid = getGenerateSigningKeyKid();
  const existingItem = signingKeyStore.get(signingKeyName);
  if (existingItem !== undefined && existingItem.reqId == reqId) {
    return { statusCode: 200, body: { reqId: reqId, kid: existingItem.kid } };
  }

  const pair = ccf.crypto.generateRsaKeyPair(2048);
  const item: SigningKeyItem = {
    kid: `${kid}-${reqId}`,
    reqId: reqId,
    publicKey: pair.publicKey,
    privateKey: pair.privateKey
  };

  signingKeyStore.set(signingKeyName, item);
  return { statusCode: 200, body: { reqId: item.reqId, kid: item.kid } };
}
