import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * Secrets operations
 */
interface Secrets {
  /** listSecrets (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  listSecrets(contractId: string): ListSecretsResponse;
  /** putSecret (AuthPolicies: [member_cert, user_cert, jwt, attestation]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @tag("auth:attestation")
  putSecret(contractId: string, secretName: string, request: PutSecretByMemberUserRequest | PutSecretByCleanRoomRequest): PutSecretResponse;
  /** getSecret (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  getSecret(contractId: string, secretName: string, request: GetSecretRequest): GetSecretResponse;
}
