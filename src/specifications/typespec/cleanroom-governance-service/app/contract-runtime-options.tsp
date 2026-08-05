import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/runtimeoptions/{option}")
interface ContractRuntimeOptions {
  /** 
    checkContractRuntimeOption
    endpoints/contract-runtime-options.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt, attestation]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @tag("auth:attestation")
  @post
  @route("/status")
  checkContractRuntimeOption(
    @path contractId: string,
    @path option: string,
  ): GetRuntimeOptionResponse;

  /** 
    enableContractRuntimeOption
    endpoints/contract-runtime-options.js
    POST
    AuthPolicies: [member_cert]
  */
  @tag("auth:member_cert")
  @post
  @route("/enable")
  enableContractRuntimeOption(
    @path contractId: string,
    @path option: string,
  ): EmptyResponse;

  /** 
    disableContractRuntimeOption
    endpoints/contract-runtime-options.js
    POST
    AuthPolicies: [member_cert]
  */
  @tag("auth:member_cert")
  @post
  @route("/disable")
  disableContractRuntimeOption(
    @path contractId: string,
    @path option: string,
  ): EmptyResponse;
}
