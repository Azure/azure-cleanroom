import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/runtimeoptions/{option}/status")
interface RuntimeOptions {
  /** 
    checkRuntimeOption
    endpoints/runtime-options.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  checkRuntimeOption(@path option: string): GetRuntimeOptionResponse;
}
