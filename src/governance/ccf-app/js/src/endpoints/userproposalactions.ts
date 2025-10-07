import * as ccfapp from "@microsoft/ccf-app";
import {
  AcceptedUserDocumentStoreItem,
  SetUserDocumentArgs,
  UserProposalInfoItem,
  UserProposalStoreItem
} from "../models";
import { ErrorResponse } from "../models/errorresponse";

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
const acceptedUserDocumentsStore = ccfapp.typedKv(
  "public:accepted_user_documents",
  ccfapp.string,
  ccfapp.json<AcceptedUserDocumentStoreItem>()
);

type validateCallback = (
  name: string,
  args: any
) => ccfapp.Response<ErrorResponse>;

type applyCallback = (
  name: string,
  args: any,
  proposalId: string,
  proposerId: string,
  approvers: {
    approverId: string;
    approverIdType: string;
  }[],
  ballots: {
    approverId: string;
    ballot: string;
  }[]
) => void;

class UserProposalAction {
  validate: validateCallback;
  apply: applyCallback;
  constructor(validate: validateCallback, apply: applyCallback) {
    this.validate = validate;
    this.apply = apply;
  }
}

function findOpenUserProposals(name: string, documentId: string): string[] {
  const proposalIds: string[] = [];
  userProposalsStore.forEach((v, k) => {
    const proposal = v;
    proposal.actions.forEach((value) => {
      if (value.name === name) {
        const args = value.args as SetUserDocumentArgs;
        if (args.documentId === documentId) {
          const proposalInfo = userProposalsInfoStore.get(k);
          if (proposalInfo.state == "Open") {
            proposalIds.push(k);
          }
        }
      }
    });
  });

  return proposalIds;
}

export const userProposalActions: Map<string, UserProposalAction> = new Map([
  [
    "set_user_document",
    new UserProposalAction(
      (name, inputArgs) => {
        const args = inputArgs as SetUserDocumentArgs;
        if (args.documentId === undefined) {
          return {
            statusCode: 400,
            body: new ErrorResponse(
              "InvalidUserDocumentId",
              "The UserDocumentId is required for set_user_document action."
            )
          };
        }

        if (acceptedUserDocumentsStore.has(args.documentId)) {
          return {
            statusCode: 405,
            body: new ErrorResponse(
              "UserDocumentAlreadyAccepted",
              `The specified document has already been accepted.`
            )
          };
        }

        const openProposals = findOpenUserProposals(name, args.documentId);
        if (openProposals.length > 0) {
          return {
            statusCode: 409,
            body: new ErrorResponse(
              "UserDocumentAlreadyProposed",
              `Proposal ${JSON.stringify(openProposals)} for the specified documentId already exists.`
            )
          };
        }
      },
      (name, inputArgs, proposalId, proposerId, approvers, ballots) => {
        const args = inputArgs as SetUserDocumentArgs;
        const documentId = args.documentId;
        const acceptedUserDocumentItem: AcceptedUserDocumentStoreItem = {
          contractId: args.document.contractId,
          data: args.document.data,
          proposalId: proposalId,
          proposerId: proposerId,
          approvers: approvers,
          finalVotes: ballots
        };
        acceptedUserDocumentsStore.set(documentId, acceptedUserDocumentItem);
      }
    )
  ]
]);
