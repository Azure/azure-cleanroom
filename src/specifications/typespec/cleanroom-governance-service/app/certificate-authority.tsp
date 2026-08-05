import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/ca")
interface CertificateAuthority {
  /** 
    generateCASigningKey
    endpoints/certificate-authority/ca-key.js
    POST
    AuthPolicies: [member_cert]
  */
  @tag("auth:member_cert")
  @post
  @route("/generateSigningKey")
  generateCASigningKey(@path contractId: string): GenerateCASigningKeyResponse;

  /** 
    generateEndorsedCert
    endpoints/certificate-authority/ca-key.js
    POST
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @post
  @route("/generateEndorsedCert")
  generateEndorsedCert(
    @path contractId: string,
    @body request: GenerateEndorsedCertRequest,
  ): GenerateEndorsedCertResponse;

  /** 
    getCAInfo
    endpoints/certificate-authority/ca-info.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt, attestation]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @tag("auth:attestation")
  @post
  @route("/status")
  getCAInfo(@path contractId: string): CAInfo;
}
