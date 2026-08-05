import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * Proposals operations
 */
interface Proposals {
  /** getProposalHistorical (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getProposalHistorical(proposalId: string): GetProposalResponse;
}
