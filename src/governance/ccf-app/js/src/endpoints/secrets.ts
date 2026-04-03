import * as ccfapp from "@microsoft/ccf-app";
import { RsaOaepAesKwpParams, ccf } from "@microsoft/ccf-app/global";
import { ErrorResponse } from "../utils/ErrorResponse";
import {
  GetSecretRequest,
  GetSecretResponse,
  ListSecretResponse,
  ListSecretsResponse,
  PutSecretByCleanRoomRequest,
  PutSecretByCleanRoomRequestData,
  PutSecretByMemberUserRequest,
  PutSecretResponse,
  SecretStoreItem
} from "../models";
import {
  b64ToBuf,
  getCallerId,
  toDelegatePolicyKey,
  validateCallerAuthorized,
  verifySignature
} from "../utils/utils";
import { verifyAttestationAndReportData } from "../attestation/attestationVerifierFactory";
import { Base64 } from "js-base64";

export function putSecret(
  request:
    | ccfapp.Request<PutSecretByMemberUserRequest>
    | ccfapp.Request<PutSecretByCleanRoomRequest>
): ccfapp.Response<PutSecretResponse> | ccfapp.Response<ErrorResponse> {
  if (request.caller.policy == "no_auth") {
    // If the caller is not authenticated, we expect a PutSecretByCleanRoomRequest as it should be
    // a call coming from a clean room that presents an attestation report.
    return putSecretByCleanRoom(
      request as ccfapp.Request<PutSecretByCleanRoomRequest>
    );
  } else {
    return putSecretByMemberUser(
      request as ccfapp.Request<PutSecretByMemberUserRequest>
    );
  }
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

  // First validate attestation report and report data.
  const contractId = request.params.contractId;
  const secretId: string = request.params.secretName;

  const policyKey = toSecretDelegatePolicyKey(contractId, secretId);
  const { error } = verifyAttestationAndReportData(
    contractId,
    body,
    () => Base64.decode(body.encrypt.publicKey),
    [policyKey]
  );
  if (error) {
    return {
      statusCode: 400,
      body: error
    };
  }

  // Attestation report and report data values are verified.
  // Now fetch the secret and wrap it with the encryption key before returning it.
  const secretsStore = ccfapp.typedKv(
    `secrets-${contractId}`,
    ccfapp.string,
    ccfapp.json<SecretStoreItem>()
  );
  if (!secretsStore.has(secretId)) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "SecretNotFound",
        `A secret with the specified id '${secretId}' was not found.`
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

export function listSecrets(
  request: ccfapp.Request
): ccfapp.Response<ListSecretsResponse> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const contractId = request.params.contractId;
  const secretsStore = ccfapp.typedKv(
    `secrets-${contractId}`,
    ccfapp.string,
    ccfapp.json<SecretStoreItem>()
  );
  const entries: ListSecretResponse[] = [];
  secretsStore.forEach((v, k) => {
    const item: ListSecretResponse = {
      secretId: k
    };
    entries.push(item);
  });

  return {
    body: {
      value: entries
    }
  };
}

const MAX_SECRET_LENGTH: number = 25600;
function getSecretId(callerId: string, secretName: string): string {
  return callerId + "_" + secretName;
}

function putSecretByCleanRoom(
  request: ccfapp.Request<PutSecretByCleanRoomRequest>
): ccfapp.Response<PutSecretResponse> | ccfapp.Response<ErrorResponse> {
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

  // Validate attestation report and report data.
  const { error: attestError } = verifyAttestationAndReportData(
    contractId,
    body,
    () => Base64.decode(body.encrypt.publicKey)
  );
  if (attestError) {
    return {
      statusCode: 400,
      body: attestError
    };
  }

  // Attestation report and report data values are verified. Now check the signature.
  const data: ArrayBuffer = b64ToBuf(body.data);
  try {
    verifySignature(body.sign, data);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("SignatureMismatch", e.message)
    };
  }

  // Attestation report, report data and payload signature are verified.
  // Now save the supplied value as a secret.
  const requestData: PutSecretByCleanRoomRequestData = JSON.parse(
    ccf.bufToStr(data)
  );
  const secretName = request.params.secretName;
  const secretPrefix = "cleanroom";
  return putSecretInternal(
    contractId,
    secretPrefix,
    secretName,
    requestData.value
  );
}

function putSecretByMemberUser(
  request: ccfapp.Request<PutSecretByMemberUserRequest>
): ccfapp.Response<PutSecretResponse> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const contractId = request.params.contractId;
  const secretName = request.params.secretName;
  const body = request.body.json();
  const secretPrefix = getCallerId(request);
  return putSecretInternal(contractId, secretPrefix, secretName, body.value);
}

function putSecretInternal(
  contractId: string,
  secretPrefix: string,
  secretName: string,
  value: string
): ccfapp.Response<PutSecretResponse> | ccfapp.Response<ErrorResponse> {
  if (!value) {
    return {
      statusCode: 400,
      body: new ErrorResponse("ValueMissing", "Value must be supplied.")
    };
  }

  if (value.length > MAX_SECRET_LENGTH) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "ValueTooLarge",
        "Length of the value should not exceed " +
          MAX_SECRET_LENGTH +
          " characters. Input is " +
          value.length +
          " characters."
      )
    };
  }

  const secretId = getSecretId(secretPrefix, secretName);
  const secretsStore = ccfapp.typedKv(
    `secrets-${contractId}`,
    ccfapp.string,
    ccfapp.json<SecretStoreItem>()
  );
  secretsStore.set(secretId, { value: value });
  return {
    statusCode: 200,
    body: {
      secretId: secretId
    }
  };
}

function toSecretDelegatePolicyKey(
  contractId: string,
  secretId: string
): string {
  return toDelegatePolicyKey(contractId, "secrets", secretId);
}
