import { Base64 } from "js-base64";
import * as ccfapp from "@microsoft/ccf-app";
import {
  SigningAlgorithm,
  AlgorithmName,
  ccf
} from "@microsoft/ccf-app/global";
import { Sign } from "../models/sign";
import { SnpAttestationResult } from "../attestation/snpattestation";
import { MemberInfo } from "../models/membermodels";
import { ProposalInfoItem, ProposalStoreItem } from "../models";
import { ICleanRoomPolicyProps } from "../attestation/ICleanRoomPolicyProps";

export function hex(buf: ArrayBuffer) {
  return Array.from(new Uint8Array(buf))
    .map((n) => n.toString(16).padStart(2, "0"))
    .join("");
}

export function b64ToBuf(b64: string): ArrayBuffer {
  return Base64.toUint8Array(b64).buffer;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function parseRequestQuery(request: ccfapp.Request<any>): any {
  const elements = request.query.split("&");
  const obj = {};
  for (const kv of elements) {
    const [k, v] = kv.split("=");
    obj[k] = v;
  }
  return obj;
}

export interface Caller {
  id: string;
}

export function getCallerId(request: ccfapp.Request): string {
  // Note that the following way of getting caller ID doesn't work for 'jwt' auth policy and
  // 'no_auth' auth policy.
  const caller = request.caller as unknown as Caller;
  return caller.id;
}

export function isMember(memberId: string): boolean {
  // Check if member exists
  // https://microsoft.github.io/CCF/main/audit/builtin_maps.html#users-info
  const membersCerts = ccfapp.typedKv(
    "public:ccf.gov.members.certs",
    ccfapp.arrayBuffer,
    ccfapp.arrayBuffer
  );
  return membersCerts.has(ccf.strToBuf(memberId));
}

export function isUser(userId: string): boolean {
  // Check if user exists
  // https://microsoft.github.io/CCF/main/audit/builtin_maps.html#users-info
  const usersCerts = ccfapp.typedKv(
    "public:ccf.gov.users.certs",
    ccfapp.arrayBuffer,
    ccfapp.arrayBuffer
  );
  return usersCerts.has(ccf.strToBuf(userId));
}

export function getTenantId(memberId: string): string {
  const info = getMemberInfo(memberId);
  return info.member_data != null ? info.member_data.tenantId : "";
}

function getMemberInfo(memberId: string): MemberInfo {
  const memberInfo = ccfapp.typedKv(
    "public:ccf.gov.members.info",
    ccfapp.arrayBuffer,
    ccfapp.arrayBuffer
  );
  const value = memberInfo.get(ccf.strToBuf(memberId));
  const info = ccf.bufToJsonCompatible(value) as MemberInfo;
  return info;
}

export function verifyReportData(
  snpAttestationResult: SnpAttestationResult,
  data: string
) {
  // The attestation report's report_data should carry sha256(data)). As sha256 returns
  // 32 bytes of data while attestation.report_data is 64 bytes (128 chars in a hex string) in size,
  // need to pad 00s at the end to compare. That is:
  // attestation.report_data = sha256(data)) + 32x(00).
  if (snpAttestationResult.attestation.report_data.length != 128) {
    throw new Error(
      "Unexpected string length of attestation.report_data: " +
        snpAttestationResult.attestation.report_data.length
    );
  }

  let expectedReportData = hex(
    ccf.crypto.digest("SHA-256", ccf.strToBuf(data))
  );
  if (expectedReportData.length != 64) {
    throw new Error(
      "Unexpected string length of expectedReportData: " +
        expectedReportData.length
    );
  }

  expectedReportData = expectedReportData.padEnd(128, "00");
  if (expectedReportData.length != 128) {
    throw new Error(
      "Unexpected string length of expectedReportData after padding with 0s: " +
        expectedReportData.length
    );
  }

  if (snpAttestationResult.attestation.report_data !== expectedReportData) {
    console.log(
      "Report data value mismatch. attestation.report_data: '" +
        snpAttestationResult.attestation.report_data +
        "', calculated report_data: '" +
        expectedReportData +
        "',"
    );
    throw new Error(
      "Attestation report_data value did not match calculated value."
    );
  }

  console.log(
    "Successfully verified expected report data value against attestation's report " +
      "data value. report_data: " +
      snpAttestationResult.attestation.report_data
  );
}

export function verifySignature(sign: Sign, data: ArrayBuffer) {
  const signature: ArrayBuffer = b64ToBuf(sign.signature);
  const publicKey: string = Base64.decode(sign.publicKey);
  const algorithmName: AlgorithmName = "RSA-PSS";
  const algorithm: SigningAlgorithm = {
    name: algorithmName,
    hash: "SHA-256",
    saltLength: 32
  };
  const result = ccf.crypto.verifySignature(
    algorithm,
    publicKey,
    signature,
    data
  );
  if (!result) {
    throw new Error("Signature verification was not successful.");
  }
}

export function checkValidUrl(url: string) {
  // From https://tools.ietf.org/html/rfc3986#appendix-B
  const re = new RegExp(
    "^(([^:/?#]+):)?(//([^/?#]*))?([^?#]*)(\\?([^#]*))?(#(.*))?"
  );
  const groups = url.match(re);
  if (!groups) {
    throw new Error(`${url} is not a valid URL.`);
  }

  const scheme = groups[2];
  if (scheme !== "http" && scheme !== "https") {
    throw new Error(
      `Url should have http or https as its scheme but scheme is ${scheme}.`
    );
  }
}

export function toJson<K, V>(map: Map<K, V>) {
  return JSON.stringify(Array.from(map.entries()));
}

export function fromJson<K, V>(jsonStr: string): Map<K, V> {
  return new Map(JSON.parse(jsonStr));
}

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

export function findOpenProposals(name: string, contractId: string): string[] {
  const proposalIds: string[] = [];
  interface ContractIdArgs {
    contractId: string;
  }
  proposalsStore.forEach((v, k) => {
    const proposal = ccf.bufToJsonCompatible(v) as ProposalStoreItem;
    proposal.actions.forEach((value) => {
      if (value.name === name) {
        const args = value.args as ContractIdArgs;
        if (args.contractId === contractId) {
          const proposalInfo = ccf.bufToJsonCompatible(
            proposalsInfoStore.get(k)
          ) as ProposalInfoItem;
          if (proposalInfo.state == "Open") {
            proposalIds.push(k);
          }
        }
      }
    });
  });

  return proposalIds;
}

export function getCleanRoomPolicyProps(
  contractId: string
): ICleanRoomPolicyProps {
  const result: ICleanRoomPolicyProps = {};
  const cleanRoomPolicyMap = ccf.kv[getCleanRoomPolicyMapName(contractId)];
  cleanRoomPolicyMap.forEach((values: ArrayBuffer, key: ArrayBuffer) => {
    const kvKey = ccf.bufToStr(key);
    const kvValue = JSON.parse(ccf.bufToStr(values));
    result[kvKey] = kvValue;
    console.log(`key policy item with key: ${kvKey} and value: ${kvValue}`);
  });
  console.log(
    `Resulting clean room policy: ${JSON.stringify(
      result
    )}, keys: ${Object.keys(result)}, keys: ${Object.keys(result).length}`
  );
  return result;
}

function getCleanRoomPolicyMapName(contractId: string): string {
  return "public:ccf.gov.policies.cleanroom-" + contractId;
}
