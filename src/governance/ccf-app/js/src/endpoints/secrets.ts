import * as ccfapp from "@microsoft/ccf-app";
import { RsaOaepAesKwpParams, ccf } from "@microsoft/ccf-app/global";
import { ErrorResponse } from "../models/errorresponse";
import {
  GetSecretRequest,
  GetSecretResponse,
  ListSecretResponse,
  ListSecretsResponse,
  PutSecretByCleanRoomRequest,
  PutSecretByCleanRoomRequestData,
  PutSecretByMemberUserRequest,
  PutSecretResponse,
  SecretStoreItem,
  SetSecretPolicyRequest,
  SetSecretPolicyRequestData
} from "../models";
import {
  b64ToBuf,
  getCallerId,
  getContractCleanRoomPolicyProps,
  getCustomCleanRoomPolicyProps,
  isEmpty,
  setCustomCleanRoomPolicy,
  validateCallerAuthorized,
  verifyReportData,
  verifySignature
} from "../utils/utils";
import {
  SnpAttestationResult,
  verifySnpAttestation,
  verifySnpAttestationViaCustomPolicy
} from "../attestation/snpattestation";
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

export function setSecretCleanRoomPolicy(
  request: ccfapp.Request<SetSecretPolicyRequest>
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
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

  // First validate attestation report.
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
  // Now save the cleanroom policy under the secretId.
  const requestData: SetSecretPolicyRequestData = JSON.parse(
    ccf.bufToStr(data)
  );
  const secretId = request.params.secretName;
  const policyKey = toSecretPolicyKey(contractId, secretId);
  setCustomCleanRoomPolicy(policyKey, requestData);
  return { statusCode: 200 };
}

export function getSecretCleanRoomPolicy(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const contractId = request.params.contractId;
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
        "SecretNotFound",
        `A secret with the specified id '${secretId}' was not found.`
      )
    };
  }

  const policyKey = toSecretPolicyKey(contractId, secretId);
  const secretLevelPolicy = getCustomCleanRoomPolicyProps(policyKey);
  const isSecretPolicyPresent = !isEmpty(secretLevelPolicy);
  const effectivePolicy = isSecretPolicyPresent
    ? secretLevelPolicy
    : getContractCleanRoomPolicyProps(contractId);

  return { statusCode: 200, body: { claims: effectivePolicy } };
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
  const secretId: string = request.params.secretName;

  const policyKey = toSecretPolicyKey(contractId, secretId);
  const secretLevelPolicy = getCustomCleanRoomPolicyProps(policyKey);
  const isSecretPolicyPresent = !isEmpty(secretLevelPolicy);

  let snpAttestationResult: SnpAttestationResult;
  try {
    snpAttestationResult = isSecretPolicyPresent
      ? verifySnpAttestationViaCustomPolicy(policyKey, body.attestation)
      : verifySnpAttestation(contractId, body.attestation);
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

  // Validate attestation report.
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

function toSecretPolicyKey(contractId: string, secretId: string): string {
  if (contractId === undefined || contractId === "") {
    throw new Error("contractId must be specified.");
  }
  if (secretId === undefined || secretId === "") {
    throw new Error("SecretId must be specified.");
  }

  return `secrets_${contractId}_${secretId}`;
}
