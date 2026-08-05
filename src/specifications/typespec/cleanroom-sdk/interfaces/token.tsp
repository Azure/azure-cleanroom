import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * Token operations
 */
interface Token {
  /** getToken (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  getToken(contractId: string, request: GetTokenRequest): GetTokenResponse;
}
