import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * Signing operations
 */
interface Signing {
  /** signPayload (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  signPayload(contractId: string): SignPayloadResponse;
  /** generateSigningKey (AuthPolicies: [member_cert]) */
  @tag("auth:member_cert")
  generateSigningKey(): GenerateSigningKeyResponse;
  /** getSigningInfo (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getSigningInfo(): SigningInfo;
}
