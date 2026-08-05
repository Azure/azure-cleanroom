import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * UserProposals operations
 */
interface UserProposals {
  /** getUserProposal (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getUserProposal(proposalId: string): GetUserProposalResponse;
  /** putUserProposal (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  putUserProposal(proposalId: string, request: CreateUserProposalRequest): CreateUserProposalResponse;
  /** checkUserProposal (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  checkUserProposal(proposalId: string): UserProposalInfoItem;
  /** withdrawUserProposal (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  withdrawUserProposal(proposalId: string): EmptyResponse;
  /** submitUserProposalBallot (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  submitUserProposalBallot(proposalId: string, request: SubmitUserProposalBallotRequest): EmptyResponse;
}
