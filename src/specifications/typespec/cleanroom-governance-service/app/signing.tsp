import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("")
interface Signing {
  /** 
    signPayload
    endpoints/signing/sign.js
    POST
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @post
  @route("/contracts/{contractId}/signing/sign")
  signPayload(@path contractId: string): SignPayloadResponse;

  /** 
    generateSigningKey
    endpoints/signing/signing-key.js
    POST
    AuthPolicies: [member_cert]
  */
  @tag("auth:member_cert")
  @post
  @route("/signing/generateSigningKey")
  generateSigningKey(): GenerateSigningKeyResponse;

  /** 
    getSigningInfo
    endpoints/signing/info.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/signing/info")
  getSigningInfo(): SigningInfo;
}
