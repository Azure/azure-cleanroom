import * as ccfapp from "@microsoft/ccf-app";
import { ccf } from "@microsoft/ccf-app/global";
import { ErrorResponse } from "../models/errorresponse";
import {
  ContractStoreItem,
  PutContractRequest,
  GetContractResponse,
  ListContractResponse,
  ProposalStoreItem,
  ProposalInfoItem,
  SetContractArgs,
  AcceptedContractStoreItem
} from "../models";

const contractsStore = ccfapp.typedKv(
  "public:contracts",
  ccfapp.string,
  ccfapp.json<ContractStoreItem>()
);
const acceptedContractsStore = ccfapp.typedKv(
  "public:ccf.gov.accepted_contracts",
  ccfapp.string,
  ccfapp.json<AcceptedContractStoreItem>()
);

const proposalsStore = ccfapp.typedKv(
  "public:ccf.gov.proposals",
  ccfapp.string,
  ccfapp.arrayBuffer
);
const proposalsInfoStore = ccfapp.typedKv(
  "public:ccf.gov.proposals_info",
  ccfapp.string,
  ccfapp.arrayBuffer
);

export function putContract(request: ccfapp.Request<PutContractRequest>) {
  const id = request.params.contractId;

  // Check if the contract is already accepted.
  if (acceptedContractsStore.has(id)) {
    return {
      statusCode: 405,
      body: new ErrorResponse(
        "ContractAlreadyAccepted",
        "An accepted contract cannot be changed."
      )
    };
  }

  const data = request.body.json().data;
  if (!data) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "DataMissing",
        "data key must be present in contract payload."
      )
    };
  }

  const incomingVersion = request.body.json().version;
  if (contractsStore.has(id)) {
    const seqno = contractsStore.getVersionOfPreviousWrite(id);
    const view = ccf.consensus.getViewForSeqno(seqno);
    if (view == null) {
      return {
        statusCode: 503,
        body: new ErrorResponse(
          "ViewNotKnown",
          "View for given sequence number not known to the node at this time."
        )
      };
    }

    if (!incomingVersion) {
      return {
        statusCode: 409,
        body: new ErrorResponse(
          "ContractAlreadyExists",
          "The specified contract already exists. If the intent was to update the " +
            "existing contract then retry the " +
            "request after reading the latest version of the resource and setting the version on " +
            "the request."
        )
      };
    }

    const version = view + "." + seqno;
    if (version != incomingVersion) {
      return {
        statusCode: 412,
        body: new ErrorResponse(
          "PreconditionFailed",
          "The operation specified a version that is different from the version " +
            "available at the server, that is, an optimistic concurrency error. Retry the " +
            "request after reading the latest version of the resource and updating the version on " +
            "the request."
        )
      };
    }
  }

  contractsStore.set(id, { data: data });
  return {};
}

export function getContract(
  request: ccfapp.Request
): ccfapp.Response<GetContractResponse> | ccfapp.Response<ErrorResponse> {
  const id = request.params.contractId;

  // Check if the contract is already accepted.
  if (acceptedContractsStore.has(id)) {
    const contractItem = acceptedContractsStore.get(id);
    const seqno = acceptedContractsStore.getVersionOfPreviousWrite(id);
    const view = ccf.consensus.getViewForSeqno(seqno);
    if (view == null) {
      return {
        statusCode: 503,
        body: new ErrorResponse(
          "ViewNotKnown",
          "View for given sequence number not known to the node at this time."
        )
      };
    }
    const version = view + "." + seqno;
    const finalVotes: { memberId: string; vote: boolean }[] = [];
    if (contractItem.finalVotes !== undefined) {
      for (const [memberId, vote] of Object.entries(contractItem.finalVotes)) {
        finalVotes.push({
          memberId: memberId,
          vote: vote === true ? true : false
        });
      }
    }

    const body: GetContractResponse = {
      id: id,
      version: version,
      state: "Accepted",
      data: contractItem.data,
      proposalId: contractItem.proposalId,
      finalVotes: finalVotes
    };
    return { body };
  }

  // Check if the contract is currently associated with an open proposal.
  let proposedContract;
  proposalsStore.forEach((v, k) => {
    const proposal = ccf.bufToJsonCompatible(v) as ProposalStoreItem;
    proposal.actions.forEach((value) => {
      if (value.name === "set_contract") {
        const args = value.args as SetContractArgs;
        if (args.contractId === id) {
          const proposalInfo = ccf.bufToJsonCompatible(
            proposalsInfoStore.get(k)
          ) as ProposalInfoItem;
          if (proposalInfo.state == "Open") {
            const body: GetContractResponse = {
              id: id,
              state: "Proposed",
              data: args.contract.data,
              version: args.contract.version,
              proposalId: k
            };

            proposedContract = { body };
            return false; // break out of the loop.
          }
        }
      }
    });
  });

  if (proposedContract != null) {
    return proposedContract;
  }

  if (contractsStore.has(id)) {
    const contractItem = contractsStore.get(id);

    // Capture both seqno (version) and view to create version semantics.
    // Apart from getVersionOfPreviousWrite(key) we also want to call getViewForSeqno(seqno) to
    // get and incorporate the view into the version because the following situation could take place:
    // getVersionOfPreviousWrite(k) -> 5
    // Client goes to prepare write conditional on version being 5
    // Network rolls back to 3 after primary crashes, elects new leader
    // 4 and 5 happen, 5 unluckily writes to k also
    // Client request arrives, expects last write to be at 5, proceeds - but the value is now different
    // If you capture:
    // getVersionOfPreviousWrite(k) -> 5
    // getViewForSeqno(5) -> 2
    // And place (and check) the expectation that the last write for the key must be at 5 in view 2,
    // then this cannot happen.
    const seqno = contractsStore.getVersionOfPreviousWrite(id);
    const view = ccf.consensus.getViewForSeqno(seqno);
    if (view == null) {
      return {
        statusCode: 503,
        body: new ErrorResponse(
          "ViewNotKnown",
          "View for given sequence number not known to the node at this time."
        )
      };
    }
    const version = view + "." + seqno;
    const body: GetContractResponse = {
      id: id,
      state: "Draft",
      version: version,
      data: contractItem.data,
      proposalId: ""
    };
    return { body };
  }

  return {
    statusCode: 404,
    body: new ErrorResponse(
      "ContractNotFound",
      "A contract with the specified id was not found."
    )
  };
}

export function listContracts():
  | ccfapp.Response<ListContractResponse[]>
  | ccfapp.Response<ErrorResponse> {
  const contractSet = new Set<string>();
  contractsStore.forEach((v, k) => {
    contractSet.add(k);
  });

  acceptedContractsStore.forEach((v, k) => {
    contractSet.add(k);
  });

  const contracts: ListContractResponse[] = [];
  contractSet.forEach((v) => {
    const item = {
      id: v
    };
    contracts.push(item);
  });

  return { body: contracts };
}
