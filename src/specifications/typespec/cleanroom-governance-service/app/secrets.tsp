import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/secrets")
interface Secrets {
  /** 
    listSecrets
    endpoints/secrets.js
    GET
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @get
  listSecrets(@path contractId: string): ListSecretsResponse;

  /** 
    putSecret
    endpoints/secrets.js
    PUT
    AuthPolicies: [member_cert, user_cert, jwt, attestation]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @tag("auth:attestation")
  @put
  @route("/{secretName}")
  putSecret(
    @path contractId: string,
    @path secretName: string,
    @body request: PutSecretByMemberUserRequest | PutSecretByCleanRoomRequest,
  ): PutSecretResponse;

  /** 
    getSecret
    endpoints/secrets.js
    POST
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @post
  @route("/{secretName}")
  getSecret(
    @path contractId: string,
    @path secretName: string,
    @body request: GetSecretRequest,
  ): GetSecretResponse;
}
