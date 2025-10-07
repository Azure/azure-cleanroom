import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../models/errorresponse";
import {
  UserDocumentRuntimeOptionStatusStoreItem,
  AcceptedUserDocumentStoreItem
} from "../models";
import {
  fromJson,
  getCallerId,
  toJson,
  validateCallerAuthorized
} from "../utils/utils";
import { ConsentCheckRequest } from "../models/contractruntimeoptionsmodel";
import { verifySnpAttestation } from "../attestation/snpattestation";

const acceptedUserDocumentsStore = ccfapp.typedKv(
  "public:accepted_user_documents",
  ccfapp.string,
  ccfapp.json<AcceptedUserDocumentStoreItem>()
);
const userDocumentsRuntimeOptionStatusStore = ccfapp.typedKv(
  "public:user_documents_runtime_option_status",
  ccfapp.string,
  ccfapp.json<UserDocumentRuntimeOptionStatusStoreItem>()
);

export function enableUserDocumentRuntimeOption(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  return setUserDocumentRuntimeOptionStatus(request, "enabled");
}

export function disableUserDocumentRuntimeOption(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  return setUserDocumentRuntimeOptionStatus(request, "disabled");
}

export function checkUserDocumentRuntimeOptionStatus(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const documentId = request.params.documentId;
  if (!acceptedUserDocumentsStore.has(documentId)) {
    return {
      statusCode: 200,
      body: {
        status: "disabled",
        reason: {
          code: "UserDocumentNotAccepted",
          message: "UserDocument does not exist or has not been accepted."
        }
      }
    };
  }

  const option = request.params.option;
  if (option !== "execution" && option !== "telemetry") {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "InvalidUserDocumentRuntimeOption",
        `UserDocument runtime option must be either 'execution' or 'telemetry'.`
      )
    };
  }

  return checkUserDocumentRuntimeOptionStatusInternal(request, option);
}

function checkUserDocumentRuntimeOptionStatusInternal(
  request: ccfapp.Request,
  option: string
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const documentId = request.params.documentId;
  const key = documentId + "_" + option;
  const documentStatus = userDocumentsRuntimeOptionStatusStore.get(key);
  if (!documentStatus || !documentStatus.serializedApproverToStatusMap) {
    return {
      statusCode: 200,
      body: {
        status: "enabled"
      }
    };
  }

  const disabledBy: string[] = [];
  const approverToStatusMap: Map<string, string> = fromJson(
    documentStatus.serializedApproverToStatusMap
  );
  approverToStatusMap.forEach((v, k) => {
    if (v == "disabled") {
      disabledBy.push(k);
    }
  });

  if (disabledBy.length == 0) {
    return {
      statusCode: 200,
      body: {
        status: "enabled"
      }
    };
  }

  return {
    statusCode: 200,
    body: {
      status: "disabled",
      reason: {
        code: "UserDocumentRuntimeOptionDisabled",
        message: `UserDocument runtime option '${option}' has been disabled by the following approver(s): m[${disabledBy}].`
      }
    }
  };
}

export function consentCheckUserDocumentRuntimeOption(
  request: ccfapp.Request<ConsentCheckRequest>
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const body = request.body.json();
  if (!body.attestation) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "AttestationMissing",
        "Attestation payload must be supplied."
      )
    };
  }

  // Validate attestation report.
  const contractId = request.params.contractId;
  try {
    verifySnpAttestation(contractId, body.attestation);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("VerifySnpAttestationFailed", e.message)
    };
  }

  const documentId = request.params.documentId;
  const acceptedUserDocumentItem = acceptedUserDocumentsStore.get(documentId);
  if (acceptedUserDocumentItem === undefined) {
    return {
      statusCode: 200,
      body: {
        status: "disabled",
        reason: {
          code: "UserDocumentNotAccepted",
          message: "UserDocument does not exist or has not been accepted."
        }
      }
    };
  }

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

  const option = request.params.option;
  if (option !== "execution" && option !== "telemetry") {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "InvalidUserDocumentRuntimeOption",
        `UserDocument runtime option must be either 'execution' or 'telemetry'.`
      )
    };
  }

  // A valid attestation report is sufficient to return consent status. Not seeing a requirement
  // to encrypt the response so only a clean room could decrypt it.
  return checkUserDocumentRuntimeOptionStatusInternal(request, option);
}

function setUserDocumentRuntimeOptionStatus(
  request: ccfapp.Request,
  newStatus: string
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const documentId = request.params.documentId;
  const acceptedUserDocumentItem = acceptedUserDocumentsStore.get(documentId);
  if (acceptedUserDocumentItem === undefined) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "UserDocumentNotAccepted",
        "UserDocument does not exist or has not been accepted."
      )
    };
  }

  const approvers = acceptedUserDocumentItem.approvers;
  if (approvers === undefined) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "ApproversNotFound",
        "An accepted UserDocument must have its approvers set."
      )
    };
  }

  console.log(
    `Required approvers for UserDocument are: ${JSON.stringify(approvers)}.`
  );
  // Check if the caller is an approver.
  const callerId = getCallerId(request);
  const approver = approvers.find((a) => a.approverId === callerId);
  if (approver === undefined) {
    return {
      statusCode: 403,
      body: new ErrorResponse(
        "NotUserDocumentApprover",
        "The caller is not an approver for this UserDocument."
      )
    };
  }

  const option = request.params.option;
  if (option !== "execution" && option !== "telemetry") {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "InvalidUserDocumentRuntimeOption",
        `UserDocument runtime option must be either 'execution' or 'telemetry'.`
      )
    };
  }

  const key = documentId + "_" + option;
  let UserDocumentStatus = userDocumentsRuntimeOptionStatusStore.get(key);
  let memberToStatusMap: Map<string, string>;
  if (!UserDocumentStatus) {
    UserDocumentStatus = {
      serializedApproverToStatusMap: null
    };
  }

  if (!UserDocumentStatus.serializedApproverToStatusMap) {
    memberToStatusMap = new Map<string, string>();
  } else {
    memberToStatusMap = fromJson(
      UserDocumentStatus.serializedApproverToStatusMap
    );
  }

  memberToStatusMap.set(callerId, newStatus);
  UserDocumentStatus.serializedApproverToStatusMap = toJson(memberToStatusMap);
  userDocumentsRuntimeOptionStatusStore.set(key, UserDocumentStatus);
  return { statusCode: 200 };
}
