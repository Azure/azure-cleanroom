import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * CertificateAuthority operations
 */
interface CertificateAuthority {
  /** generateCASigningKey (AuthPolicies: [member_cert]) */
  @tag("auth:member_cert")
  generateCASigningKey(contractId: string): GenerateCASigningKeyResponse;
  /** generateEndorsedCert (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  generateEndorsedCert(contractId: string, request: GenerateEndorsedCertRequest): GenerateEndorsedCertResponse;
  /** getCAInfo (AuthPolicies: [member_cert, user_cert, jwt, attestation]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @tag("auth:attestation")
  getCAInfo(contractId: string): CAInfo;
}
