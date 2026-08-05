import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * ContractRuntimeOptions operations
 */
interface ContractRuntimeOptions {
  /** checkContractRuntimeOption (AuthPolicies: [member_cert, user_cert, jwt, attestation]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @tag("auth:attestation")
  checkContractRuntimeOption(contractId: string, option: string): GetRuntimeOptionResponse;
  /** enableContractRuntimeOption (AuthPolicies: [member_cert]) */
  @tag("auth:member_cert")
  enableContractRuntimeOption(contractId: string, option: string): EmptyResponse;
  /** disableContractRuntimeOption (AuthPolicies: [member_cert]) */
  @tag("auth:member_cert")
  disableContractRuntimeOption(contractId: string, option: string): EmptyResponse;
}
