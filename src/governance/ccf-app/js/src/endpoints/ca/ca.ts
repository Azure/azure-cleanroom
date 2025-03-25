import * as ccfapp from "@microsoft/ccf-app";
import { CAConfigItem } from "../../models";

const caGovStore = ccfapp.typedKv(
  "public:ccf.gov.ca",
  ccfapp.string,
  ccfapp.json<CAConfigItem>()
);

export function isCAEnabledInternal(contractId: string): boolean {
  const item = caGovStore.get(contractId);
  if (item !== undefined) {
    return item.enabled === "true"
  }

  return false
}

export function getLastGenerateRequestId(contractId: string): string {
  const item = caGovStore.get(contractId);
  return item.requestId;
}