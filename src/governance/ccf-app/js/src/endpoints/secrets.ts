import * as ccfapp from "@microsoft/ccf-app";
import { RsaOaepAesKwpParams, ccf } from "@microsoft/ccf-app/global";
import { ErrorResponse } from "../models/errorresponse";
import {
  GetSecretRequest,
  GetSecretResponse,
  PutSecretRequest,
  PutSecretResponse,
  SecretStoreItem
} from "../models";
import { getCallerId, isMember, verifyReportData } from "../utils/utils";
import {
  SnpAttestationResult,
  verifySnpAttestation
} from "../attestation/snpattestation";
import { Base64 } from "js-base64";

const MAX_SECRET_LENGTH: number = 25600;
function getSecretName(callerId: string, secretName: string): string {
  return callerId + ":" + secretName;
}

export function putSecret(
  request: ccfapp.Request<PutSecretRequest>
): ccfapp.Response<PutSecretResponse> | ccfapp.Response<ErrorResponse> {
  const contractId = request.params.contractId;
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
  if (!body.value) {
    return {
      statusCode: 400,
      body: new ErrorResponse("ValueMissing", "Value must be supplied.")
    };
  }

  if (body.value.length > MAX_SECRET_LENGTH) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "ValueTooLarge",
        "Length of the value should not exceed " +
          MAX_SECRET_LENGTH +
          " characters. Input is " + body.value.length + " characters."
      )
    };
  }

  const secretId = getSecretName(callerId, request.params.secretName);
  const secretsStore = ccfapp.typedKv(
    `secrets-${contractId}`,
    ccfapp.string,
    ccfapp.json<SecretStoreItem>()
  );
  secretsStore.set(secretId, { value: body.value });
  return {
    statusCode: 200,
    body: {
      secretId: secretId
    }
  };
}

export function getSecret(
  request: ccfapp.Request<GetSecretRequest>
): ccfapp.Response<GetSecretResponse> | ccfapp.Response<ErrorResponse> {
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

  // First validate attestation report.
  const contractId = request.params.contractId;
  let snpAttestationResult: SnpAttestationResult;
  try {
    snpAttestationResult = verifySnpAttestation(contractId, body.attestation);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("VerifySnpAttestationFailed", e.message)
    };
  }

  //  Then validate the report data value.
  try {
    verifyReportData(snpAttestationResult, body.encrypt.publicKey);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("ReportDataMismatch", e.message)
    };
  }

  // Attestation report and report data values are verified.
  // Now fetch the secret and wrap it with the encryption key before returning it.
  const secretId: string = request.params.secretName;
  const secretsStore = ccfapp.typedKv(
    `secrets-${contractId}`,
    ccfapp.string,
    ccfapp.json<SecretStoreItem>()
  );
  if (!secretsStore.has(secretId)) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "SecertNotFound",
        "A secret with the specified id was not found."
      )
    };
  }

  const secretItem: SecretStoreItem = secretsStore.get(secretId);
  const wrapAlgo = {
    name: "RSA-OAEP-AES-KWP",
    aesKeySize: 256
  } as RsaOaepAesKwpParams;
  const wrapped: ArrayBuffer = ccf.crypto.wrapKey(
    ccf.strToBuf(secretItem.value),
    ccf.strToBuf(Base64.decode(body.encrypt.publicKey)),
    wrapAlgo
  );
  const wrappedBase64 = Base64.fromUint8Array(new Uint8Array(wrapped));
  return {
    statusCode: 200,
    body: {
      value: wrappedBase64
    }
  };
}
