import * as ccfapp from "@microsoft/ccf-app";
import { isSigningEnabled } from "./config";
import { validateCallerAuthorized } from "../../utils/utils";
import { ErrorResponse } from "../../utils/ErrorResponse";
import { getSigningKey } from "./signingkey";

export interface SigningInfo {
  enabled: boolean;
  publicKeyPem: string;
}

export function getSigningInfo(
  request: ccfapp.Request
): ccfapp.Response<SigningInfo> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const info: SigningInfo = {
    enabled: isSigningEnabled(),
    publicKeyPem: getSigningKey()?.publicKey ?? ""
  };

  return { statusCode: 200, body: info };
}
