import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * Contracts operations
 */
interface Contracts {
  /** listContracts (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  listContracts(): ListContractsResponse;
  /** putContract (AuthPolicies: [member_cert]) */
  @tag("auth:member_cert")
  putContract(contractId: string, request: PutContractRequest): EmptyResponse;
  /** getContract (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getContract(contractId: string): GetContractResponse;
}
