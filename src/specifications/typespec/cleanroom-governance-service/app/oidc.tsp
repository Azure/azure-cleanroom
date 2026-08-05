import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/oidc")
interface Oidc {
  /** 
    getConfiguration
    endpoints/oidc/discovery.js
    GET
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @get
  @route("/.well-known/openid-configuration")
  getConfiguration(): DiscoveryResponse;

  /** 
    getJwks
    endpoints/oidc/jwks.js
    GET
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @get
  @route("/keys")
  getJwks(): JwksResponse;

  /** 
    generateIssuerSigningKey
    endpoints/oidc/signing-key.js
    POST
    AuthPolicies: [member_cert]
  */
  @tag("auth:member_cert")
  @post
  @route("/generateSigningKey")
  generateIssuerSigningKey(): EmptyResponse;

  /** 
    setIssuerUrl
    endpoints/oidc/issuer.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/setIssuerUrl")
  setIssuerUrl(@body request: SetIssuerUrlRequest): EmptyResponse;

  /** 
    getOidcIssuerInfo
    endpoints/oidc/info.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/issuerInfo")
  getOidcIssuerInfo(): OidcIssuerInfo;
}
