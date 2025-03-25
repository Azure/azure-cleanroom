import * as ccfapp from "@microsoft/ccf-app";
import { RsaOaepAesKwpParams, ccf } from "@microsoft/ccf-app/global";
import { ErrorResponse } from "../models/errorresponse";
import {
  DocumentStoreItem,
  PutDocumentRequest,
  GetDocumentResponse,
  ListDocumentResponse,
  ProposalStoreItem,
  ProposalInfoItem,
  AcceptedDocumentStoreItem,
  SetDocumentArgs,
  ContractStoreItem,
  GetAcceptedDocumentRequest,
  GetAcceptedDocumentResponse
} from "../models";
import {
  SnpAttestationResult,
  verifySnpAttestation
} from "../attestation/snpattestation";
import { verifyReportData } from "../utils/utils";
import { Base64 } from "js-base64";

const documentsStore = ccfapp.typedKv(
  "public:documents",
  ccfapp.string,
  ccfapp.json<DocumentStoreItem>()
);
const acceptedDocumentsStore = ccfapp.typedKv(
  "public:ccf.gov.accepted_documents",
  ccfapp.string,
  ccfapp.json<AcceptedDocumentStoreItem>()
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

export function putDocument(request: ccfapp.Request<PutDocumentRequest>) {
  const id = request.params.documentId;
  // Check if the document is already accepted.
  if (acceptedDocumentsStore.has(id)) {
    return {
      statusCode: 405,
      body: new ErrorResponse(
        "DocumentAlreadyAccepted",
        "An accepted document cannot be changed."
      )
    };
  }

  const contractId = request.body.json().contractId;
  if (!contractId) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "ContractIdMissing",
        "ContractId must be specified in document payload."
      )
    };
  }

  const data = request.body.json().data;
  if (!data) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "DataMissing",
        "data key must be present in document payload."
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

  const incomingVersion = request.body.json().version;
  if (documentsStore.has(id)) {
    const seqno = documentsStore.getVersionOfPreviousWrite(id);
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
          "DocumentAlreadyExists",
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

  documentsStore.set(id, { contractId: contractId, data: data });
  return {};
}

export function getDocument(
  request: ccfapp.Request
): ccfapp.Response<GetDocumentResponse> | ccfapp.Response<ErrorResponse> {
  const id = request.params.documentId;

  // Check if the document is already accepted.
  if (acceptedDocumentsStore.has(id)) {
    const documentItem = acceptedDocumentsStore.get(id);
    const seqno = acceptedDocumentsStore.getVersionOfPreviousWrite(id);
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
    if (documentItem.finalVotes !== undefined) {
      for (const [memberId, vote] of Object.entries(documentItem.finalVotes)) {
        finalVotes.push({
          memberId: memberId,
          vote: vote === true ? true : false
        });
      }
    }

    const body: GetDocumentResponse = {
      id: id,
      version: version,
      state: "Accepted",
      contractId: documentItem.contractId,
      data: documentItem.data,
      proposalId: documentItem.proposalId,
      finalVotes: finalVotes
    };
    return { body };
  }

  // Check if the document is currently associated with an open proposal.
  let proposedDocument;
  proposalsStore.forEach((v, k) => {
    const proposal = ccf.bufToJsonCompatible(v) as ProposalStoreItem;
    proposal.actions.forEach((value) => {
      if (value.name === "set_document") {
        const args = value.args as SetDocumentArgs;
        if (args.documentId === id) {
          const proposalInfo = ccf.bufToJsonCompatible(
            proposalsInfoStore.get(k)
          ) as ProposalInfoItem;
          if (proposalInfo.state == "Open") {
            const body: GetDocumentResponse = {
              id: id,
              state: "Proposed",
              contractId: args.document.contractId,
              data: args.document.data,
              version: args.document.version,
              proposalId: k
            };

            proposedDocument = { body };
            return false; // break out of the loop.
          }
        }
      }
    });
  });

  if (proposedDocument != null) {
    return proposedDocument;
  }

  if (documentsStore.has(id)) {
    const documentItem = documentsStore.get(id);

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
    const seqno = documentsStore.getVersionOfPreviousWrite(id);
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
    const body: GetDocumentResponse = {
      id: id,
      state: "Draft",
      version: version,
      contractId: documentItem.contractId,
      data: documentItem.data,
      proposalId: ""
    };
    return { body };
  }

  return {
    statusCode: 404,
    body: new ErrorResponse(
      "DocumentNotFound",
      "A document with the specified id was not found."
    )
  };
}

export function getAcceptedDocument(
  request: ccfapp.Request<GetAcceptedDocumentRequest>
):
  | ccfapp.Response<GetAcceptedDocumentResponse>
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

  // Only accepted documents are exposed.
  if (!acceptedDocumentsStore.has(id)) {
    return {
      statusCode: 404,
      body: new ErrorResponse(
        "DocumentNotFound",
        "A document with the specified id was not found or has not been accepted."
      )
    };
  }

  const documentItem = acceptedDocumentsStore.get(id);
  if (contractId != documentItem.contractId) {
    // Something is amiss. The values should match.
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "ContractIdMismatch",
        `The contractId value specified in the URL ${contractId} and that in the document ${documentItem.contractId} don't match.`
      )
    };
  }

  // Attestation report and report data values are verified.
  // Wrap the document with the encryption key before returning it.
  const seqno = acceptedDocumentsStore.getVersionOfPreviousWrite(id);
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
  if (documentItem.finalVotes !== undefined) {
    for (const [memberId, vote] of Object.entries(documentItem.finalVotes)) {
      finalVotes.push({
        memberId: memberId,
        vote: vote === true ? true : false
      });
    }
  }

  const body: GetDocumentResponse = {
    id: id,
    version: version,
    state: "Accepted",
    contractId: documentItem.contractId,
    data: documentItem.data,
    proposalId: documentItem.proposalId,
    finalVotes: finalVotes
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

export function listDocuments():
  | ccfapp.Response<ListDocumentResponse[]>
  | ccfapp.Response<ErrorResponse> {
  const documentSet = new Set<string>();
  documentsStore.forEach((v, k) => {
    documentSet.add(k);
  });

  acceptedDocumentsStore.forEach((v, k) => {
    documentSet.add(k);
  });

  const documents: ListDocumentResponse[] = [];
  documentSet.forEach((v) => {
    const item = {
      id: v
    };
    documents.push(item);
  });

  return { body: documents };
}
