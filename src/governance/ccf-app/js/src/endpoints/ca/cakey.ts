import * as ccfapp from "@microsoft/ccf-app";
import { ccf, RsaOaepAesKwpParams } from "@microsoft/ccf-app/global";
import {
  GenerateEndorsedCertRequest,
  CAKeyItem,
  GenerateEndorsedCertRequestData
} from "../../models";
import { ErrorResponse } from "../../models/errorresponse";
import { getLastGenerateRequestId, isCAEnabledInternal } from "./ca";
import {
  SnpAttestationResult,
  verifySnpAttestation
} from "../../attestation/snpattestation";
import { b64ToBuf, verifyReportData, verifySignature } from "../../utils/utils";
import { Base64 } from "js-base64";
import { cleanroom } from "../../global.cleanroom";

const caSigningKeyStore = ccfapp.typedKv(
  "ca.signing_keys",
  ccfapp.string,
  ccfapp.json<CAKeyItem>()
);

export function getCASigningKey(contractId: string): CAKeyItem {
  if (caSigningKeyStore.has(contractId)) {
    return caSigningKeyStore.get(contractId);
  }

  return null;
}

export function generateEndorsedCert(
  request: ccfapp.Request<GenerateEndorsedCertRequest>
): ccfapp.Response | ErrorResponse {
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

  // All verifications pass. Genereate the endorsed certificate.
  const item = getCASigningKey(contractId);
  if (item === null) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "NoCASigningKey",
        "Cannot generate endorsed certificate as there is no CA signing key for this contract."
      )
    };
  }

  const requestData: GenerateEndorsedCertRequestData = JSON.parse(
    ccf.bufToStr(data)
  );

  const endorsed_cert = cleanroom.crypto.generateEndorsedCert(
    requestData.publicKey,
    requestData.subjectName,
    requestData.subjectAlternateNames,
    requestData.validityPeriodDays,
    item.privateKey,
    item.caCert,
    false
  );

  // Wrap the response before returning it.
  const serializedItem = JSON.stringify(endorsed_cert);
  const wrapAlgo = {
    name: "RSA-OAEP-AES-KWP",
    aesKeySize: 256
  } as RsaOaepAesKwpParams;
  const wrapped: ArrayBuffer = ccf.crypto.wrapKey(
    ccf.strToBuf(serializedItem),
    ccf.strToBuf(Base64.decode(body.encrypt.publicKey)),
    wrapAlgo
  );
  const wrappedBase64 = Base64.fromUint8Array(new Uint8Array(wrapped));
  return { body: { value: wrappedBase64 } };
}

export function generateCASigningKey(request: ccfapp.Request): ccfapp.Response {
  const contractId = request.params.contractId;
  // Generate a signing key only if feature is enabled and a request to create a signing key
  // was proposed. We only want to create a signing key when signaled by the acceptance of the
  // enable_ca/enable_rotate_ca proposal. This is tracked via
  // matching the requestId value kept in the governance table with the value kept in the private
  // signing key table. Repeated invocations of this API when there is no change in the requestId
  // will be ignored.
  if (!isCAEnabledInternal(contractId)) {
    return {
      statusCode: 405,
      body: new ErrorResponse(
        "CANotEnabled",
        "CA support has not been enabled for this contract."
      )
    };
  }

  const requestId = getLastGenerateRequestId(contractId);
  const existingItem = caSigningKeyStore.get(contractId);
  if (existingItem !== undefined && existingItem.requestId == requestId) {
    return { statusCode: 200, body: { requestId: requestId } };
  }

  const pair = ccf.crypto.generateEcdsaKeyPair("secp384r1");
  const subjectName = "CN=Azure Clean Room CA";
  const result = cleanroom.crypto.generateSelfSignedCert(
    pair.privateKey,
    subjectName,
    [],
    100,
    true,
    0
  );
  const item: CAKeyItem = {
    requestId: requestId,
    publicKey: pair.publicKey,
    privateKey: pair.privateKey,
    caCert: result.cert
  };

  caSigningKeyStore.set(contractId, item);
  return { statusCode: 200, body: { requestId: requestId } };
}
