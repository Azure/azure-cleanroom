import * as ccfapp from "@microsoft/ccf-app";
import {
  RsaOaepAesKwpParams,
  SigningAlgorithm,
  ccf
} from "@microsoft/ccf-app/global";
import { Base64 } from "js-base64";
import { getSigningKey } from "./oidc/signingkey";
import { GetTokenRequest, GetTokenResponse } from "../models/tokenmodels";
import { ErrorResponse } from "../models/errorresponse";
import { isCcfv4, parseRequestQuery, verifyReportData } from "../utils/utils";
import {
  SnpAttestationResult,
  verifySnpAttestation
} from "../attestation/snpattestation";
import { getGovIssuerUrl, getTenantIdIssuerUrl } from "./oidc/issuer";

export function getToken(
  request: ccfapp.Request<GetTokenRequest>
): ccfapp.Response<GetTokenResponse> | ccfapp.Response<ErrorResponse> {
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

  // Attestation report and report data values are verified.
  // Now generate the token and wrap it with the encryption key before returning it.
  const parsedQuery = parseRequestQuery(request);
  let params: GetTokenParams;
  try {
    params = extractParams(parsedQuery);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("InvalidInput", e.message)
    };
  }

  const { nbf, exp, iat, jti, sub, tid, aud } = params;
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

  const iss = getTenantIdIssuerUrl(tid) ?? getGovIssuerUrl();
  if (!iss) {
    return {
      statusCode: 405,
      body: new ErrorResponse(
        "IssuerUrlNotSet",
        `Issuer url has not been configured for tenant ${tid}. Propose set_oidc_issuer_url or set the issuer at the tenant level.`
      )
    };
  }

  // https://www.scottbrady91.com/jose/jwts-which-signing-algorithm-should-i-use
  // https://learn.microsoft.com/en-us/entra/identity-platform/certificate-credentials#assertion-format
  const algHeader: string = isCcfv4() ? "RS256" : "PS256";
  const header = {
    alg: algHeader,
    typ: "JWT",
    kid: signingKey.kid
  };
  const claims = {
    aud: aud,
    exp: exp,
    iss: iss,
    jti: jti,
    nbf: nbf,
    sub: sub,
    iat: iat
  };

  // https://jwt.io/introduction
  const headerBase64 = Base64.encodeURL(JSON.stringify(header));
  const claimsBase64 = Base64.encodeURL(JSON.stringify(claims));
  const toSign = headerBase64 + "." + claimsBase64;
  const algorithmName: string = isCcfv4() ? "RSASSA-PKCS1-v1_5" : "RSA-PSS";
  const algorithm: SigningAlgorithm = {
    name: algorithmName,
    hash: "SHA-256",
    saltLength: 32
  };
  const signature: ArrayBuffer = ccf.crypto.sign(
    algorithm,
    signingKey.privateKey,
    ccf.strToBuf(toSign)
  );
  const urlSafe: boolean = true;
  const signatureBase64 = Base64.fromUint8Array(
    new Uint8Array(signature),
    urlSafe
  );
  const token = headerBase64 + "." + claimsBase64 + "." + signatureBase64;

  // Wrap the token before returning it.
  const wrapAlgo = {
    name: "RSA-OAEP-AES-KWP",
    aesKeySize: 256
  } as RsaOaepAesKwpParams;
  const wrapped: ArrayBuffer = ccf.crypto.wrapKey(
    ccf.strToBuf(token),
    ccf.strToBuf(Base64.decode(body.encrypt.publicKey)),
    wrapAlgo
  );
  const wrappedBase64 = Base64.fromUint8Array(new Uint8Array(wrapped));
  return { body: { value: wrappedBase64 } };
}

interface GetTokenParams {
  nbf: string;
  exp: string;
  iat: string;
  jti: string;
  sub: string;
  tid: string;
  aud?: string;
}

function extractParams(parsedQuery): GetTokenParams {
  const {
    nbf,
    exp,
    iat,
    jti,
    sub,
    tid,
    aud
  }: {
    nbf: string;
    exp: string;
    iat: string;
    jti: string;
    sub: string;
    tid: string;
    aud: string;
  } = parsedQuery;
  if (!exp) {
    throw new Error(`Value for exp '${exp}' is invalid.`);
  }

  if (!iat) {
    throw new Error(`Value for iat '${iat}' is invalid.`);
  }

  if (!jti) {
    throw new Error(`Value for jti '${jti}' is invalid.`);
  }

  if (!sub) {
    throw new Error(`Value for sub '${sub}' is invalid.`);
  }

  if (!tid) {
    throw new Error(`Value for tid '${tid}' is invalid.`);
  }

  if (!aud) {
    throw new Error(`Value for aud '${aud}' is invalid.`);
  }

  return { nbf, exp, iat, jti, sub, tid, aud };
}
