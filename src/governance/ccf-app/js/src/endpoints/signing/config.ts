import * as ccfapp from "@microsoft/ccf-app";

const signingGovStore = ccfapp.typedKv(
  "public:ccf.gov.signing",
  ccfapp.string,
  ccfapp.string
);

export function isSigningEnabled(): boolean {
  return signingGovStore.get("enabled") === "true";
}

export function getLastGenerateSigningKeyReqdId(): string {
  return signingGovStore.get("generateSigningKeyRequestId");
}

export function getGenerateSigningKeyKid(): string {
  return signingGovStore.get("generateSigningKeyKid");
}
