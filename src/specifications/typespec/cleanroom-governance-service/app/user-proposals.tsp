import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/users/proposals/{proposalId}")
interface UserProposals {
  /** 
    getUserProposal
    endpoints/user-proposals.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  getUserProposal(@path proposalId: string): GetUserProposalResponse;

  /** 
    putUserProposal
    endpoints/user-proposals.js
    PUT
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @put
  putUserProposal(
    @path proposalId: string,
    @body request: CreateUserProposalRequest,
  ): CreateUserProposalResponse;

  /** 
    checkUserProposal
    endpoints/user-proposals.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/status")
  checkUserProposal(@path proposalId: string): UserProposalInfoItem;

  /** 
    withdrawUserProposal
    endpoints/user-proposals.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/withdraw")
  withdrawUserProposal(@path proposalId: string): EmptyResponse;

  /** 
    submitUserProposalBallot
    endpoints/user-proposals.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/ballots/submit")
  submitUserProposalBallot(
    @path proposalId: string,
    @body request: SubmitUserProposalBallotRequest,
  ): EmptyResponse;
}
