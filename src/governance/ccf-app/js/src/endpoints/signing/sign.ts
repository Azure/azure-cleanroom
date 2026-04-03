import * as ccfapp from "@microsoft/ccf-app";
import {
  SigningAlgorithm,
  AlgorithmName,
  RsaOaepAesKwpParams,
  ccf
} from "@microsoft/ccf-app/global";
import { Base64 } from "js-base64";
import { getSigningKey } from "./signingkey";
import { SnpEvidence } from "../../models";
import { ErrorResponse } from "../../utils/ErrorResponse";
import { b64ToBuf, toDelegatePolicyKey } from "../../utils/utils";
import { verifyAttestationAndReportData } from "../../attestation/attestationVerifierFactory";

export interface SigningRequest {
  attestation: SnpEvidence;
  encrypt: {
    publicKey: string;
  };
  payload: string;
}

export interface SigningResponse {
  value: string;
}

export function signPayload(
  request: ccfapp.Request<SigningRequest>
): ccfapp.Response<SigningResponse> | ccfapp.Response<ErrorResponse> {
  const contractId = request.params.contractId;
  const body = request.body.json();

  if (!body.attestation) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "AttestationMissing",
        "Attestation payload must be supplied."
      )
    };
  }

  if (!body.encrypt) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "EncryptionMissing",
        "Encrypt payload must be supplied."
      )
    };
  }

  if (!body.payload) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "PayloadMissing",
        "Payload to sign must be supplied."
      )
    };
  }

  // Validate that payload is a valid base64 string.
  let toSign: ArrayBuffer;
  try {
    toSign = b64ToBuf(body.payload);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "PayloadInvalid",
        "Payload must be a valid base64 encoded string."
      )
    };
  }

  // First validate attestation report and report data.
  const delegatePolicyKey = toPodPoliciesKey(contractId);
  const { error } = verifyAttestationAndReportData(
    contractId,
    body,
    () => Base64.decode(body.encrypt.publicKey),
    [delegatePolicyKey]
  );
  if (error) {
    return {
      statusCode: 400,
      body: error
    };
  }

  // Attestation report and report data values are verified.
  // Now sign the payload using the signing key.
  const signingKey = getSigningKey();
  if (!signingKey) {
    return {
      statusCode: 405,
      body: new ErrorResponse(
        "SigningKeyNotAvailable",
        "Propose enable_signing and generate signing key before attempting to sign."
      )
    };
  }

  // Use the same algorithm as token.ts (RSA-PSS with SHA-256).
  const algorithmName: AlgorithmName = "RSA-PSS";
  const algorithm: SigningAlgorithm = {
    name: algorithmName,
    hash: "SHA-256",
    saltLength: 32
  };

  const signature: ArrayBuffer = ccf.crypto.sign(
    algorithm,
    signingKey.privateKey,
    toSign
  );

  const signatureBase64 = Base64.fromUint8Array(new Uint8Array(signature));

  // Wrap the signature before returning it.
  const wrapAlgo = {
    name: "RSA-OAEP-AES-KWP",
    aesKeySize: 256
  } as RsaOaepAesKwpParams;
  const wrapped: ArrayBuffer = ccf.crypto.wrapKey(
    ccf.strToBuf(signatureBase64),
    ccf.strToBuf(Base64.decode(body.encrypt.publicKey)),
    wrapAlgo
  );
  const wrappedBase64 = Base64.fromUint8Array(new Uint8Array(wrapped));

  return { body: { value: wrappedBase64 } };
}

function toPodPoliciesKey(contractId: string): string {
  return toDelegatePolicyKey(contractId, "podpolicies", "admin");
}
