import * as ccfapp from "@microsoft/ccf-app";
import {
  CreateUserProposalRequest,
  CreateUserProposalResponse,
  GetUserProposalResponse,
  SubmitUserProposalBallotRequest,
  UserProposalInfoItem,
  UserProposalStoreItem
} from "../models";
import { ErrorResponse } from "../models/errorresponse";
import { getCallerId, validateCallerAuthorized } from "../utils/utils";
import { userProposalActions } from "./userproposalactions";

const userProposalsStore = ccfapp.typedKv(
  "public:user_proposals",
  ccfapp.string,
  ccfapp.json<UserProposalStoreItem>()
);
const userProposalsInfoStore = ccfapp.typedKv(
  "public:user_proposals_info",
  ccfapp.string,
  ccfapp.json<UserProposalInfoItem>()
);

export function putProposal(
  request: ccfapp.Request<CreateUserProposalRequest>
): ccfapp.Response<CreateUserProposalResponse | ErrorResponse> {
  let error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const proposalId = request.params.proposalId;
  if (userProposalsStore.has(proposalId)) {
    return {
      statusCode: 409,
      body: new ErrorResponse(
        "ProposalAlreadyExists",
        "The specified proposal already exists."
      )
    };
  }

  const body = request.body.json();

  // Note (gsinha): We require approvers to be passed in.
  // Logic to default to treating active members as approvers if none are passed in is not implemented.
  if (body.approvers === undefined) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "ApproversMissing",
        "The approvers field is required in the proposal."
      )
    };
  }

  if (body.approvers.length === 0) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "EmptyApprovers",
        "The approvers field cannot be empty."
      )
    };
  }

  const action = userProposalActions.get(body.name);
  if (action === undefined) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "InvalidProposalType",
        `A proposal of type '${body.name}' is not supported.`
      )
    };
  }

  error = action.validate(body.name, body.args);
  if (error !== undefined) {
    return error;
  }

  userProposalsStore.set(proposalId, { actions: [body] });
  const callerId = getCallerId(request);
  const proposalInfo: UserProposalInfoItem = {
    proposerId: callerId,
    state: "Open",
    ballots: []
  };
  userProposalsInfoStore.set(proposalId, proposalInfo);
  const responseBody: CreateUserProposalResponse = {
    proposalId: proposalId
  };
  return { body: responseBody };
}

export function getProposal(
  request: ccfapp.Request
): ccfapp.Response<GetUserProposalResponse> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const id = request.params.proposalId;

  if (userProposalsStore.has(id)) {
    const proposalItem = userProposalsStore.get(id);
    const body: GetUserProposalResponse = {
      proposalId: id,
      name: proposalItem.actions[0].name,
      approvers: proposalItem.actions[0].approvers,
      args: proposalItem.actions[0].args
    };
    return { body };
  }

  return {
    statusCode: 404,
    body: new ErrorResponse(
      "ProposalNotFound",
      "A proposal with the specified id was not found."
    )
  };
}

export function getProposalStatus(
  request: ccfapp.Request
): ccfapp.Response<UserProposalInfoItem> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const id = request.params.proposalId;

  if (userProposalsInfoStore.has(id)) {
    const proposalItem = userProposalsInfoStore.get(id);
    const body: UserProposalInfoItem = proposalItem;
    return { body };
  }

  return {
    statusCode: 404,
    body: new ErrorResponse(
      "ProposalNotFound",
      "A proposal with the specified id was not found."
    )
  };
}

export function withdrawProposal(
  request: ccfapp.Request
): ccfapp.Response<UserProposalInfoItem> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const id = request.params.proposalId;

  if (userProposalsStore.has(id)) {
    const proposalInfoItem = userProposalsInfoStore.get(id);
    const callerId = getCallerId(request);
    if (proposalInfoItem.proposerId !== callerId) {
      return {
        statusCode: 403,
        body: new ErrorResponse(
          "NotProposalOwner",
          "Only the proposal owner can withdraw the proposal."
        )
      };
    }

    if (proposalInfoItem.state !== "Open") {
      return {
        statusCode: 409,
        body: new ErrorResponse(
          "ProposalNotOpen",
          `The proposal is not in an open state. State is: '${proposalInfoItem.state}'.`
        )
      };
    }
    proposalInfoItem.state = "Withdrawn";
    userProposalsInfoStore.set(id, proposalInfoItem);
    const body: UserProposalInfoItem = proposalInfoItem;
    return { body };
  }

  return {
    statusCode: 404,
    body: new ErrorResponse(
      "ProposalNotFound",
      "A proposal with the specified id was not found."
    )
  };
}

export function submitBallotProposal(
  request: ccfapp.Request<SubmitUserProposalBallotRequest>
): ccfapp.Response<UserProposalInfoItem> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const id = request.params.proposalId;
  const callerId = getCallerId(request);

  const proposalInfoItem = userProposalsInfoStore.get(id);
  if (proposalInfoItem === undefined) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "ProposalNotFound",
        "A proposal with the specified id was not found."
      )
    };
  }

  const proposalItem = userProposalsStore.get(id);
  const approvers = proposalItem.actions[0].approvers;
  if (approvers !== undefined) {
    console.log(
      `Required approvers for proposalId: ${id} are: ${JSON.stringify(approvers)}.`
    );
    // Check if the caller is an approver.
    const approver = approvers.find((a) => a.approverId === callerId);
    if (approver === undefined) {
      return {
        statusCode: 403,
        body: new ErrorResponse(
          "NotProposalApprover",
          "The caller is not an approver for this proposal."
        )
      };
    }
  }

  if (proposalInfoItem.state !== "Open") {
    return {
      statusCode: 409,
      body: new ErrorResponse(
        "ProposalNotOpen",
        `The proposal is not in an open state. State is: '${proposalInfoItem.state}'.`
      )
    };
  }

  if (proposalInfoItem.ballots.find((a) => a.approverId === callerId)) {
    return {
      statusCode: 409,
      body: new ErrorResponse(
        "BallotAlreadySubmitted",
        "The ballot has already been submitted."
      )
    };
  }

  // Check if incoming ballot value is accepted or rejected.
  const ballot = request.body.json().ballot;
  if (ballot !== "accepted" && ballot !== "rejected") {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "InvalidBallotValue",
        "The ballot value must be either 'accepted' or 'rejected'."
      )
    };
  }

  console.log(
    `Incoming ballot for proposalId '${id}' by callerId '${callerId}' is '${ballot}'. Existing proposal status: ${JSON.stringify(proposalInfoItem)}.`
  );

  proposalInfoItem.ballots.push({
    approverId: callerId,
    ballot: ballot
  });

  if (approvers !== undefined) {
    // Check if incoming ballot of the approver is rejecting the proposal.
    const rejectedBallot = ballot === "rejected";
    if (rejectedBallot) {
      proposalInfoItem.state = "Rejected";
    } else {
      // Check if all approvers have accepted the proposal.
      const allBallotsSubmitted = approvers.every((a) =>
        proposalInfoItem.ballots.find(
          (b) => b.approverId == a.approverId && b.ballot == "accepted"
        )
      );
      if (allBallotsSubmitted) {
        const action = userProposalActions.get(proposalItem.actions[0].name);
        if (action === undefined) {
          throw Error(
            `Cannot location action defintion for ${proposalItem.actions[0].name}. This is unexpected.`
          );
        }
        action.apply(
          proposalItem.actions[0].name,
          proposalItem.actions[0].args,
          id,
          proposalInfoItem.proposerId,
          proposalItem.actions[0].approvers,
          proposalInfoItem.ballots
        );
        proposalInfoItem.state = "Accepted";
      }
    }
  }

  console.log(
    `Proposal status after recording the incoming ballot is: ${JSON.stringify(proposalInfoItem)}`
  );
  userProposalsInfoStore.set(id, proposalInfoItem);
  const body: UserProposalInfoItem = proposalInfoItem;
  return { body };
}
