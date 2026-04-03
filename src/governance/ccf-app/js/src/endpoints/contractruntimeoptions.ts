import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../utils/ErrorResponse";
import {
  ContractStoreItem,
  ContractExecutionStatusStoreItem,
  ContractLoggingStatusStoreItem,
  ContractTelemetryStatusStoreItem
} from "../models";
import {
  findOpenProposals,
  fromJson,
  getCallerId,
  toJson,
  validateCallerAuthorized
} from "../utils/utils";
import {
  ConsentCheckRequest,
  GetRuntimeOptionResponse
} from "../models";
import { verifySnpAttestation } from "../attestation/snpattestation";

const acceptedContractsStore = ccfapp.typedKv(
  "public:ccf.gov.accepted_contracts",
  ccfapp.string,
  ccfapp.json<ContractStoreItem>()
);
const contractsExecutionStatusStore = ccfapp.typedKv(
  "public:contracts_execution_status",
  ccfapp.string,
  ccfapp.json<ContractExecutionStatusStoreItem>()
);
const contractsLoggingStatusStore = ccfapp.typedKv(
  "public:ccf.gov.contracts_logging_status",
  ccfapp.string,
  ccfapp.json<ContractLoggingStatusStoreItem>()
);
const contractsTelemetryStatusStore = ccfapp.typedKv(
  "public:ccf.gov.contracts_telemetry_status",
  ccfapp.string,
  ccfapp.json<ContractTelemetryStatusStoreItem>()
);

export function enableContractRuntimeOption(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  if (request.params.option !== "execution") {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "InvalidOption",
        `The option value ${request.params.option} is invalid. Use proposals for options other than 'execution'.`
      )
    };
  }

  return setContractExecutionStatus(request, "enabled");
}

export function disableContractRuntimeOption(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  if (request.params.option !== "execution") {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "InvalidOption",
        `The option value ${request.params.option} is invalid. Use proposals for options other than 'execution'.`
      )
    };
  }

  return setContractExecutionStatus(request, "disabled");
}

export function checkContractRuntimeOption(
  request: ccfapp.Request | ccfapp.Request<ConsentCheckRequest>
): ccfapp.Response | ccfapp.Response<GetRuntimeOptionResponse> | ccfapp.Response<ErrorResponse> {

  const contractId = request.params.contractId;

  if (request.caller.policy === "no_auth") {
    // If the caller is not authenticated, we expect a consent check coming from a clean room that
    // presents an attestation report.
    const body = (request as ccfapp.Request<ConsentCheckRequest>).body.json();
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
    try {
      verifySnpAttestation(contractId, body.attestation);
    } catch (e) {
      return {
        statusCode: 400,
        body: new ErrorResponse("VerifySnpAttestationFailed", e.message)
      };
    }
  } else {
    // For authenticated callers, validate authorization.
    const error = validateCallerAuthorized(request);
    if (error !== undefined) {
      return error;
    }
  }

  let response: ccfapp.Response | ccfapp.Response<GetRuntimeOptionResponse> | ccfapp.Response<ErrorResponse>;

  if (request.params.option === "execution") {
    response = checkContractExecutionStatusInternal(contractId);
  } else if (request.params.option === "logging") {
    response = checkContractLoggingStatusInternal(contractId);
  } else if (request.params.option === "telemetry") {
    response = checkContractTelemetryStatusInternal(contractId);
  } else {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "InvalidOption",
        `The option value ${request.params.option} is invalid. Valid values are 'execution', 'logging' and 'telemetry'.`
      )
    };
  }

  // A valid attestation report is sufficient to return consent status. Not seeing a requirement
  // to encrypt the response so only a clean room could decrypt it as we allow all users to check
  // this anyway.
  return response;
}

function checkContractExecutionStatusInternal(
  contractId: string
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  if (!acceptedContractsStore.has(contractId)) {
    return {
      statusCode: 200,
      body: {
        status: "disabled",
        reason: {
          code: "ContractNotAccepted",
          message: "Contract does not exist or has not been accepted."
        }
      }
    };
  }

  const contractStatus = contractsExecutionStatusStore.get(contractId);
  if (!contractStatus || !contractStatus.serializedMemberToStatusMap) {
    return {
      statusCode: 200,
      body: {
        status: "enabled"
      }
    };
  }

  const disabledBy: string[] = [];
  const memberToStatusMap: Map<string, string> = fromJson(
    contractStatus.serializedMemberToStatusMap
  );
  memberToStatusMap.forEach((v, k) => {
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
        code: "ContractDisabled",
        message: `Contract has been disabled by the following member(s): m[${disabledBy}].`
      }
    }
  };
}

function checkContractLoggingStatusInternal(
  contractId: string
): ccfapp.Response<GetRuntimeOptionResponse> | ccfapp.Response<ErrorResponse> {
  if (!acceptedContractsStore.has(contractId)) {
    return {
      statusCode: 200,
      body: {
        status: "disabled",
        reason: {
          code: "ContractNotAccepted",
          message: "Contract does not exist or has not been accepted."
        }
      }
    };
  }

  const proposalIds = findOpenProposals(
    "set_contract_runtime_options_enable_logging",
    contractId
  );
  proposalIds.concat(
    findOpenProposals(
      "set_contract_runtime_options_disable_logging",
      contractId
    )
  );
  const loggingStatus = contractsLoggingStatusStore.get(contractId);
  const status =
    loggingStatus && loggingStatus.status === "enabled"
      ? "enabled"
      : "disabled";
  return {
    statusCode: 200,
    body: {
      status: status,
      proposalIds: proposalIds
    }
  };
}

function checkContractTelemetryStatusInternal(
  contractId: string
): ccfapp.Response<GetRuntimeOptionResponse> | ccfapp.Response<ErrorResponse> {
  if (!acceptedContractsStore.has(contractId)) {
    return {
      statusCode: 200,
      body: {
        status: "disabled",
        reason: {
          code: "ContractNotAccepted",
          message: "Contract does not exist or has not been accepted."
        }
      }
    };
  }

  const proposalIds = findOpenProposals(
    "set_contract_runtime_options_enable_telemetry",
    contractId
  );
  proposalIds.concat(
    findOpenProposals(
      "set_contract_runtime_options_disable_telemetry",
      contractId
    )
  );
  const telemetryStatus = contractsTelemetryStatusStore.get(contractId);
  const status =
    telemetryStatus && telemetryStatus.status === "enabled"
      ? "enabled"
      : "disabled";
  return {
    statusCode: 200,
    body: {
      status: status,
      proposalIds: proposalIds
    }
  };
}

function setContractExecutionStatus(
  request: ccfapp.Request,
  newStatus: string
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const contractId = request.params.contractId;

  if (!acceptedContractsStore.has(contractId)) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "ContractNotAccepted",
        "Contract does not exist or has not been accepted."
      )
    };
  }

  const callerId = getCallerId(request);
  let contractStatus = contractsExecutionStatusStore.get(contractId);
  let memberToStatusMap: Map<string, string>;
  if (!contractStatus) {
    contractStatus = {
      serializedMemberToStatusMap: null
    };
  }

  if (!contractStatus.serializedMemberToStatusMap) {
    memberToStatusMap = new Map<string, string>();
  } else {
    memberToStatusMap = fromJson(contractStatus.serializedMemberToStatusMap);
  }

  memberToStatusMap.set(callerId, newStatus);
  contractStatus.serializedMemberToStatusMap = toJson(memberToStatusMap);
  contractsExecutionStatusStore.set(contractId, contractStatus);
  return { statusCode: 200 };
}
