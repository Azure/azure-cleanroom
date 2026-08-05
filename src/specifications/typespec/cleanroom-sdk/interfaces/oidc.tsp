import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * Oidc operations
 */
interface Oidc {
  /** getConfiguration (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  getConfiguration(): DiscoveryResponse;
  /** getJwks (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  getJwks(): JwksResponse;
  /** generateIssuerSigningKey (AuthPolicies: [member_cert]) */
  @tag("auth:member_cert")
  generateIssuerSigningKey(): EmptyResponse;
  /** setIssuerUrl (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  setIssuerUrl(request: SetIssuerUrlRequest): EmptyResponse;
  /** getOidcIssuerInfo (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getOidcIssuerInfo(): OidcIssuerInfo;
}
