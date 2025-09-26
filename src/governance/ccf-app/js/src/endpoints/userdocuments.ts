import * as ccfapp from "@microsoft/ccf-app";
import { RsaOaepAesKwpParams, ccf } from "@microsoft/ccf-app/global";
import { ErrorResponse } from "../models/errorresponse";
import {
  UserDocumentStoreItem,
  PutUserDocumentRequest,
  GetUserDocumentResponse,
  ListUserDocumentResponse,
  UserProposalStoreItem,
  UserProposalInfoItem,
  AcceptedUserDocumentStoreItem,
  SetUserDocumentArgs,
  ContractStoreItem,
  GetAcceptedUserDocumentRequest,
  GetAcceptedUserDocumentResponse,
  ListUserDocumentsResponse
} from "../models";
import {
  SnpAttestationResult,
  verifySnpAttestation
} from "../attestation/snpattestation";
import { validateCallerAuthorized, verifyReportData } from "../utils/utils";
import { Base64 } from "js-base64";

const userDocumentsStore = ccfapp.typedKv(
  "public:user_documents",
  ccfapp.string,
  ccfapp.json<UserDocumentStoreItem>()
);
const acceptedUserDocumentsStore = ccfapp.typedKv(
  "public:accepted_user_documents",
  ccfapp.string,
  ccfapp.json<AcceptedUserDocumentStoreItem>()
);

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

export function putUserDocument(
  request: ccfapp.Request<PutUserDocumentRequest>
) {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const id = request.params.documentId;
  // Check if the UserDocument is already accepted.
  if (acceptedUserDocumentsStore.has(id)) {
    return {
      statusCode: 405,
      body: new ErrorResponse(
        "UserDocumentAlreadyAccepted",
        `The specified document has already been accepted.`
      )
    };
  }

  const userDocumentRequest = request.body.json();
  const contractId = userDocumentRequest.contractId;
  if (!contractId) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "ContractIdMissing",
        "ContractId must be specified in UserDocument payload."
      )
    };
  }

  const data = userDocumentRequest.data;
  if (!data) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "DataMissing",
        "data key must be present in UserDocument payload."
      )
    };
  }

  // A contract must exist. We don't check the accepted contract store as the state of the contract
  // is not considered. Mere presence of a contract in the store sufficient.
  const contractsStore = ccfapp.typedKv(
    "public:contracts",
    ccfapp.string,
    ccfapp.json<ContractStoreItem>()
  );
  if (!contractsStore.has(contractId)) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "ContractNotFound",
        "A contract with the specified id was not found."
      )
    };
  }

  const incomingVersion = userDocumentRequest.version;
  if (userDocumentsStore.has(id)) {
    const seqno = userDocumentsStore.getVersionOfPreviousWrite(id);
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
          "UserDocumentAlreadyExists",
          "The specified document already exists. If the intent was to update the " +
            "existing document then retry the " +
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

  userDocumentsStore.set(id, {
    contractId: contractId,
    data: data,
    approvers: userDocumentRequest.approvers
  });
  return {};
}

export function getUserDocument(
  request: ccfapp.Request
): ccfapp.Response<GetUserDocumentResponse> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const id = request.params.documentId;

  // Check if the UserDocument is already accepted.
  if (acceptedUserDocumentsStore.has(id)) {
    const acceptedUserDocumentItem = acceptedUserDocumentsStore.get(id);
    const seqno = acceptedUserDocumentsStore.getVersionOfPreviousWrite(id);
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
    const body: GetUserDocumentResponse = {
      id: id,
      version: version,
      state: "Accepted",
      contractId: acceptedUserDocumentItem.contractId,
      approvers: acceptedUserDocumentItem.approvers,
      data: acceptedUserDocumentItem.data,
      proposalId: acceptedUserDocumentItem.proposalId,
      proposerId: acceptedUserDocumentItem.proposerId,
      finalVotes: acceptedUserDocumentItem.finalVotes
    };
    return { body };
  }

  // Check if the UserDocument is currently associated with an open proposal.
  let proposedUserDocument;
  userProposalsStore.forEach((v, k) => {
    const proposal = v;
    proposal.actions.forEach((value) => {
      if (value.name === "set_user_document") {
        const args = value.args as SetUserDocumentArgs;
        if (args.documentId === id) {
          const proposalInfo = userProposalsInfoStore.get(k);
          if (proposalInfo.state == "Open") {
            const body: GetUserDocumentResponse = {
              id: id,
              state: "Proposed",
              contractId: args.document.contractId,
              data: args.document.data,
              version: args.document.version,
              approvers: value.approvers,
              proposalId: k,
              proposerId: proposalInfo.proposerId
            };

            proposedUserDocument = { body };
            return false; // break out of the loop.
          }
        }
      }
    });
  });

  if (proposedUserDocument != null) {
    return proposedUserDocument;
  }

  if (userDocumentsStore.has(id)) {
    const userDocumentItem = userDocumentsStore.get(id);

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
    const seqno = userDocumentsStore.getVersionOfPreviousWrite(id);
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
    const body: GetUserDocumentResponse = {
      id: id,
      state: "Draft",
      version: version,
      contractId: userDocumentItem.contractId,
      data: userDocumentItem.data,
      approvers: userDocumentItem.approvers,
      proposalId: "",
      proposerId: ""
    };
    return { body };
  }

  return {
    statusCode: 404,
    body: new ErrorResponse(
      "UserDocumentNotFound",
      "A document with the specified id was not found."
    )
  };
}

export function getAcceptedUserDocument(
  request: ccfapp.Request<GetAcceptedUserDocumentRequest>
):
  | ccfapp.Response<GetAcceptedUserDocumentResponse>
  | ccfapp.Response<ErrorResponse> {
  const id = request.params.documentId;
  const requestBody = request.body.json();
  if (!requestBody.attestation) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "AttestationMissing",
        "Attestation payload must be supplied."
      )
    };
  }

  if (!requestBody.encrypt) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "EncryptionMissing",
        "Encrypt payload must be supplied."
      )
    };
  }

  // Validate attestation report.
  const contractId = request.params.contractId;
  let snpAttestationResult: SnpAttestationResult;
  try {
    snpAttestationResult = verifySnpAttestation(
      contractId,
      requestBody.attestation
    );
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("VerifySnpAttestationFailed", e.message)
    };
  }

  // Then validate the report data value.
  try {
    verifyReportData(snpAttestationResult, requestBody.encrypt.publicKey);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("ReportDataMismatch", e.message)
    };
  }

  // Only accepted UserDocuments are exposed.
  if (!acceptedUserDocumentsStore.has(id)) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "UserDocumentNotFound",
        "A document with the specified id was not found or has not been accepted."
      )
    };
  }

  const acceptedUserDocumentItem = acceptedUserDocumentsStore.get(id);
  if (contractId != acceptedUserDocumentItem.contractId) {
    // Something is amiss. The values should match.
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "ContractIdMismatch",
        `The contractId value specified in the URL ${contractId} and that in the UserDocument ${acceptedUserDocumentItem.contractId} don't match.`
      )
    };
  }

  // Attestation report and report data values are verified.
  // Wrap the UserDocument with the encryption key before returning it.
  const seqno = acceptedUserDocumentsStore.getVersionOfPreviousWrite(id);
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
  const body: GetUserDocumentResponse = {
    id: id,
    version: version,
    state: "Accepted",
    contractId: acceptedUserDocumentItem.contractId,
    approvers: acceptedUserDocumentItem.approvers,
    data: acceptedUserDocumentItem.data,
    proposalId: acceptedUserDocumentItem.proposalId,
    proposerId: acceptedUserDocumentItem.proposerId,
    finalVotes: acceptedUserDocumentItem.finalVotes
  };

  const wrapAlgo = {
    name: "RSA-OAEP-AES-KWP",
    aesKeySize: 256
  } as RsaOaepAesKwpParams;
  const wrapped: ArrayBuffer = ccf.crypto.wrapKey(
    ccf.jsonCompatibleToBuf(body),
    ccf.strToBuf(Base64.decode(requestBody.encrypt.publicKey)),
    wrapAlgo
  );
  const wrappedBase64 = Base64.fromUint8Array(new Uint8Array(wrapped));
  return {
    statusCode: 200,
    body: {
      value: wrappedBase64
    }
  };
}

export function listUserDocuments(
  request: ccfapp.Request
): ccfapp.Response<ListUserDocumentsResponse> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const userDocumentSet = new Set<string>();
  userDocumentsStore.forEach((v, k) => {
    userDocumentSet.add(k);
  });

  acceptedUserDocumentsStore.forEach((v, k) => {
    userDocumentSet.add(k);
  });

  const userDocuments: ListUserDocumentResponse[] = [];
  userDocumentSet.forEach((v) => {
    const item = {
      id: v
    };
    userDocuments.push(item);
  });

  return {
    body: {
      value: userDocuments
    }
  };
}
