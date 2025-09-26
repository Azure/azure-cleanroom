import * as ccfapp from "@microsoft/ccf-app";
import { ErrorResponse } from "../models/errorresponse";
import { RuntimeOptionStoreItem } from "../models";
import { validateCallerAuthorized } from "../utils/utils";

const runtimeOptionsStore = ccfapp.typedKv(
  "public:ccf.gov.cgs_runtime_options",
  ccfapp.string,
  ccfapp.json<RuntimeOptionStoreItem>()
);

const runtimeOptions = [
  "autoapprove-constitution-proposal",
  "autoapprove-jsapp-proposal",
  "autoapprove-deploymentspec-proposal",
  "autoapprove-cleanroompolicy-proposal"
];
export function checkRuntimeOptionStatus(
  request: ccfapp.Request
): ccfapp.Response | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }
  const option = request.params.option;
  if (!runtimeOptions.includes(option)) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "InvalidOption",
        `Option '${option}' is not a valid value.`
      )
    };
  }

  if (!runtimeOptionsStore.has(option)) {
    return {
      statusCode: 200,
      body: {
        status: "disabled"
      }
    };
  }

  const item = runtimeOptionsStore.get(option);
  return {
    statusCode: 200,
    body: {
      status: item.status
    }
  };
}
