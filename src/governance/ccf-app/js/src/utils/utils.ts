import { Base64 } from "js-base64";
import * as ccfapp from "@microsoft/ccf-app";
import {
  SigningAlgorithm,
  AlgorithmName,
  ccf
} from "@microsoft/ccf-app/global";
import { Sign } from "../models";

import { MemberInfo, UserData } from "../models";
import {
  DelegatePolicyInfoItem,
  ProposalInfoItem,
  ProposalStoreItem,
  UserIdentityStoreItem
} from "../models";
import { ErrorResponse } from "./ErrorResponse";
import { SetDelegateCleanRoomPolicyRequestData } from "../models";
import { ICleanRoomPolicyProps } from "../attestation/ICleanRoomPolicyProps";
import { verifyJwtClaims } from "../attestation/jwtclaims";

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
  if (
    request.caller.policy === "member_cert" ||
    request.caller.policy == "user_cert"
  ) {
    const caller = request.caller as unknown as Caller;
    return caller.id;
  } else if (request.caller.policy === "jwt") {
    const caller = request.caller as ccfapp.JwtAuthnIdentity;
    interface Payload {
      oid: string;
    }
    const jwtPayload = caller.jwt.payload as Payload;
    if (jwtPayload.oid === undefined) {
      throw new Error(
        "OID claim is not present in the JWT token. Cannot get caller ID."
      );
    }

    // The caller ID is the OID claim in the JWT token.
    return caller.jwt.payload.oid;
  } else {
    throw new Error(
      `Unknown authentication policy: ${request.caller.policy}. Cannot get caller ID.`
    );
  }
}

export function validateCallerAuthorized(
  request: ccfapp.Request
): ccfapp.Response<ErrorResponse> {
  // For member_cert and user_cert auth policies, no explicit authorization is needed.
  if (
    request.caller.policy === "member_cert" ||
    request.caller.policy == "user_cert"
  ) {
    return;
  }

  if (request.caller.policy === "jwt") {
    const callerId = getCallerId(request);
    const userIdentityStore = ccfapp.typedKv(
      "public:ccf.gov.user_identities",
      ccfapp.string,
      ccfapp.json<UserIdentityStoreItem>()
    );

    const user = userIdentityStore.get(callerId);
    if (user === undefined) {
      return {
        statusCode: 403,
        body: new ErrorResponse(
          "CallerNotAuthorized",
          `User ${callerId} is not authorized to invoke this endpoint.`
        )
      };
    }

    const tenantId = user.data.tenantId;
    const incomingTenantId = request.caller.jwt.payload.tid;
    if (tenantId != incomingTenantId) {
      return {
        statusCode: 403,
        body: new ErrorResponse(
          "CallerTenantNotAuthorized",
          `Tenant ${incomingTenantId} is not authorized to invoke this endpoint. Expected tenant ID is ${tenantId}.`
        )
      };
    }

    return;
  }

  return {
    statusCode: 400,
    body: new ErrorResponse(
      "UnknownAuthenticationPolicy",
      `Validation for ${request.caller.policy} is not supported.`
    )
  };
}

export function getIssuerType(issuer: string, tid: string): string {
  if (
    issuer ===
    "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0"
  ) {
    return "MsPersonal";
  } else if (issuer === "https://login.microsoftonline.com/consumers/v2.0") {
    return "MsPersonal";
  } else if (
    issuer === "https://login.microsoftonline.com/organizations/v2.0"
  ) {
    return "EntraID";
  } else if (issuer === "https://login.microsoftonline.com/common/v2.0") {
    return "EntraID";
  } else if (
    tid !== undefined &&
    issuer == `https://login.microsoftonline.com/${tid}/v2.0`
  ) {
    return "EntraID";
  } else if (tid !== undefined && issuer == `https://sts.windows.net/${tid}/`) {
    return "EntraID";
  } else {
    return "GenericOpenID";
  }
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
  return info && info.memberData != null ? info.memberData.tenantId : "";
}

export function getCallerTenantId(request: ccfapp.Request): string {
  const callerId = getCallerId(request);
  if (request.caller.policy === "member_cert") {
    // For member_cert, get tenant ID from member data.
    return getTenantId(callerId);
  } else if (request.caller.policy === "user_cert") {
    // For user_cert, get tenant ID from CCF user data.
    return getUserTenantId(callerId);
  } else if (request.caller.policy === "jwt") {
    // For JWT users, return the tid from the JWT. This is safe because
    // validateCallerAuthorized already verified it matches the registered tenant ID.
    return request.caller.jwt.payload.tid;
  } else {
    throw new Error(
      `Unknown authentication policy: ${request.caller.policy}. Cannot get caller tenant ID.`
    );
  }
}

function getMemberInfo(memberId: string): MemberInfo {
  const memberInfo = ccfapp.typedKv(
    "public:ccf.gov.members.info",
    ccfapp.arrayBuffer,
    ccfapp.arrayBuffer
  );
  const value = memberInfo.get(ccf.strToBuf(memberId));
  if (value !== undefined) {
    const rawInfo = ccf.bufToJsonCompatible(value);
    // Map JSON member_data to TypeScript memberData
    return {
      memberData: rawInfo.member_data
    };
  }
}

// CCF user info structure as stored in public:ccf.gov.users.info table.
interface CcfUserInfo {
  userData?: UserData;
}

export function getUserTenantId(userId: string): string {
  const info = getUserInfo(userId);
  return info && info.userData != null ? info.userData.tenantId : "";
}

function getUserInfo(userId: string): CcfUserInfo {
  const userInfo = ccfapp.typedKv(
    "public:ccf.gov.users.info",
    ccfapp.arrayBuffer,
    ccfapp.arrayBuffer
  );
  const value = userInfo.get(ccf.strToBuf(userId));
  if (value !== undefined) {
    const rawInfo = ccf.bufToJsonCompatible(value);
    // Map JSON user_data to TypeScript userData.
    return {
      userData: rawInfo.user_data
    };
  }
}

export function verifyReportData(reportData: string, data: string) {
  // The attestation report's report_data should carry sha256(data)). As sha256 returns
  // 32 bytes of data while attestation.report_data is 64 bytes (128 chars in a hex string) in size,
  // need to pad 00s at the end to compare. That is:
  // attestation.report_data = sha256(data)) + 32x(00).
  if (reportData.length != 128) {
    throw new Error(
      "Unexpected string length of attestation.report_data: " +
        reportData.length
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

  if (reportData !== expectedReportData) {
    console.log(
      "Report data value mismatch. attestation.report_data: '" +
        reportData +
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
      reportData
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

export function matchPolicyClaims(
  cleanroomPolicy: object,
  attestationClaims: object
): boolean {
  for (let inx = 0; inx < Object.keys(cleanroomPolicy).length; inx++) {
    const key = Object.keys(cleanroomPolicy)[inx];

    // check if key is in attestation
    const attestationValue = attestationClaims[key];
    const policyValue = cleanroomPolicy[key];
    const isUndefined = typeof attestationValue === "undefined";
    console.log(
      `Checking key ${key}, typeof attestationValue: ${typeof attestationValue}, ` +
        `isUndefined: ${isUndefined}, attestation value: ${attestationValue}, policyValue: ${policyValue}`
    );
    if (isUndefined) {
      console.log(`Policy claim ${key} is missing from attestation`);
      return false;
    }
    if (
      policyValue.filter((p) => {
        console.log(`Check if policy value ${p} === ${attestationValue}`);
        return p === attestationValue;
      }).length === 0
    ) {
      return false;
    }
  }

  return true;
}

// Shared helper that verifies attestation claims against the cleanroom policy
// (and optionally delegated policies) for a given contract. Throws if no match
// is found. Used by both CACI and CVM attestation verifiers.
export function verifyPolicyClaims(
  contractId: string,
  attestationClaims: object,
  delegatedPolicies?: string[]
): void {
  // Get the clean room policy.
  const cleanroomPolicy = getContractCleanRoomPolicyProps(contractId);

  console.log(
    `Clean room policy: ${JSON.stringify(cleanroomPolicy)}, keys: ${Object.keys(
      cleanroomPolicy
    )}, keys: ${Object.keys(cleanroomPolicy).length}`
  );

  if (isEmpty(cleanroomPolicy)) {
    throw Error(
      "The clean room policy is missing. Please propose a new clean room policy."
    );
  }

  // First, try to match with the main cleanroom policy.
  let matchFound: boolean = matchPolicyClaims(
    cleanroomPolicy,
    attestationClaims
  );

  // If no match found and we have delegated policies, iterate through them to find a match.
  if (!matchFound && delegatedPolicies && delegatedPolicies.length > 0) {
    for (const delegatedPolicyId of delegatedPolicies) {
      const delegatedPolicy =
        getDelegateCleanRoomPolicyProps(delegatedPolicyId);
      if (!isEmpty(delegatedPolicy)) {
        console.log(`Checking delegated policy: ${delegatedPolicyId}`);
        matchFound = matchPolicyClaims(delegatedPolicy, attestationClaims);
        if (matchFound) {
          console.log(
            `Match found with delegated policy: ${delegatedPolicyId}`
          );
          break;
        }
      }
    }
  }

  // If still no match found, throw an error.
  if (!matchFound) {
    throw Error(
      `Attestation claims do not match the contract policy${
        delegatedPolicies ? `, or the delegated policies` : ""
      }.`
    );
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

export function getContractCleanRoomPolicyProps(
  contractId: string
): ICleanRoomPolicyProps {
  return getCleanRoomPolicyProps(getContractCleanRoomPolicyMapName(contractId));
}

export function getDelegateCleanRoomPolicyProps(
  key: string
): ICleanRoomPolicyProps {
  return getCleanRoomPolicyProps(getDelegateCleanRoomPolicyMapName(key));
}

export function isEmpty(cleanroomPolicy: ICleanRoomPolicyProps): boolean {
  return Object.keys(cleanroomPolicy).length === 0;
}

export function toDelegatePolicyKey(
  contractId: string,
  delegateType: string,
  delegateId: string
): string {
  if (contractId === undefined || contractId === "") {
    throw new Error("contractId must be specified.");
  }
  if (delegateType === undefined || delegateType === "") {
    throw new Error("delegateType must be specified.");
  }
  if (delegateId === undefined || delegateId === "") {
    throw new Error("delegateId must be specified.");
  }

  return `${contractId}_${delegateType}_${delegateId}`;
}

export function setDelegateCleanRoomPolicyMap(
  contractId: string,
  delegateType: string,
  delegateId: string,
  args: SetDelegateCleanRoomPolicyRequestData
) {
  const key = toDelegatePolicyKey(contractId, delegateType, delegateId);
  const mapName = getDelegateCleanRoomPolicyMapName(key);
  const delegatePolicyListStore = ccfapp.typedKv(
    `public:policies.cleanroom-delegates-list-${contractId}`,
    ccfapp.string,
    ccfapp.json<DelegatePolicyInfoItem>()
  );

  delegatePolicyListStore.set(key, {
    delegateType: delegateType,
    delegateId: delegateId
  });

  // Function to add policy claims
  const add = (claims) => {
    let items = [];
    console.log(
      `Add claims to clean room policy under map (${mapName}): ${JSON.stringify(claims)}`
    );
    Object.keys(claims).forEach((key) => {
      let itemToAdd = claims[key];
      // Make sure itemToAdd is always an array
      if (!Array.isArray(itemToAdd)) {
        itemToAdd = [itemToAdd];
      }

      const keyBuf = ccf.strToBuf(key);
      if (ccf.kv[mapName].has(keyBuf)) {
        // Key is already available
        const itemsBuf = ccf.kv[mapName].get(keyBuf);
        const existingItemStr = ccf.bufToStr(itemsBuf);
        console.log(`key: ${key} already exist: ${existingItemStr}`);
        items = JSON.parse(existingItemStr);
        if (typeof itemToAdd[0] === "boolean") {
          // booleans are single value arrays
          items = itemToAdd;
        } else {
          // loop through the input and add it to the existing set
          itemToAdd.forEach((i) => {
            if (items.filter((ii) => ii === i).length === 0) {
              // Element does not exist in items, add it.
              items.push(i);
            }
          });
        }
      } else {
        // set single value
        // Make sure that the itemToAdd doesn't have duplicates.
        items = Array.from(new Set(itemToAdd));
      }

      // prepare and store items
      const jsonItems = JSON.stringify(items);
      const jsonItemsBuf = ccf.strToBuf(jsonItems);
      console.log(
        `Accepted clean room policy item. Key: ${key}, value: ${jsonItems}`
      );
      ccf.kv[mapName].set(keyBuf, jsonItemsBuf);
    });
  };

  // Function to remove clean room policy claims
  const remove = (claims) => {
    let items = [];
    console.log(
      `Remove claims to clean room policy under map (${mapName}): ${JSON.stringify(claims)}`
    );
    Object.keys(claims).forEach((key) => {
      let itemToRemove = claims[key];
      // Make sure itemToRemove is always an array
      if (!Array.isArray(itemToRemove)) {
        itemToRemove = [itemToRemove];
      }

      const keyBuf = ccf.strToBuf(key);
      if (ccf.kv[mapName].has(keyBuf)) {
        // Key must be available
        const itemsBuf = ccf.kv[mapName].get(keyBuf);
        const existingItemStr = ccf.bufToStr(itemsBuf);
        console.log(`key: ${key} exist: ${existingItemStr}`);
        items = JSON.parse(existingItemStr);
        if (typeof itemToRemove[0] === "boolean") {
          // booleans are single value arrays, removing will remove the whole key
          ccf.kv[mapName].delete(keyBuf);
        } else {
          // loop through the input and delete it from the existing set
          itemToRemove.forEach((i) => {
            if (items.filter((ii) => ii === i).length === 0) {
              throw new Error(
                `Trying to remove value '${i}' from ${items} and it does not exist`
              );
            }
            // Remove value from list
            const index = items.indexOf(i);
            if (index > -1) {
              items.splice(index, 1);
            }
          });
          // update items
          if (items.length === 0) {
            ccf.kv[mapName].delete(keyBuf);
          } else {
            const jsonItems = JSON.stringify(items);
            const jsonItemsBuf = ccf.strToBuf(jsonItems);
            ccf.kv[mapName].set(keyBuf, jsonItemsBuf);
          }
        }
      } else {
        throw new Error(
          `Cannot remove values of ${key} because the key does not exist in the clean room policy claims`
        );
      }
    });
  };

  // Function to validate the input before doing add/remove processing
  const validate = (args) => {
    if (args.delegateType !== "add" && args.delegateType !== "remove") {
      throw new Error(
        `Clean Room Policy with type '${args.delegateType}' is not supported`
      );
    }

    const CACI_SNP_POLICY_CLAIMS = {
      "x-ms-attestation-type": "string",
      "x-ms-compliance-status": "string",
      "x-ms-policy-hash": "string",
      "vm-configuration-secure-boot": "boolean",
      "vm-configuration-secure-boot-template-id": "string",
      "vm-configuration-tpm-enabled": "boolean",
      "vm-configuration-vmUniqueId": "string",
      "x-ms-sevsnpvm-authorkeydigest": "string",
      "x-ms-sevsnpvm-bootloader-svn": "number",
      "x-ms-sevsnpvm-familyId": "string",
      "x-ms-sevsnpvm-guestsvn": "number",
      "x-ms-sevsnpvm-hostdata": "string",
      "x-ms-sevsnpvm-idkeydigest": "string",
      "x-ms-sevsnpvm-imageId": "string",
      "x-ms-sevsnpvm-is-debuggable": "boolean",
      "x-ms-sevsnpvm-launchmeasurement": "string",
      "x-ms-sevsnpvm-microcode-svn": "number",
      "x-ms-sevsnpvm-migration-allowed": "boolean",
      "x-ms-sevsnpvm-reportdata": "string",
      "x-ms-sevsnpvm-reportid": "string",
      "x-ms-sevsnpvm-smt-allowed": "boolean",
      "x-ms-sevsnpvm-snpfw-svn": "number",
      "x-ms-sevsnpvm-tee-svn": "number",
      "x-ms-sevsnpvm-vmpl": "number",
      "x-ms-ver": "string"
    };

    const CVM_SNP_POLICY_CLAIMS = {
      pcr0: "string",
      pcr1: "string",
      pcr2: "string",
      pcr3: "string",
      pcr4: "string",
      pcr5: "string",
      pcr6: "string",
      pcr7: "string",
      pcr8: "string",
      pcr9: "string",
      pcr10: "string",
      pcr11: "string",
      pcr12: "string",
      pcr13: "string",
      pcr14: "string",
      pcr15: "string",
      pcr16: "string",
      pcr17: "string",
      pcr18: "string",
      pcr19: "string",
      pcr20: "string",
      pcr21: "string",
      pcr22: "string",
      pcr23: "string"
    };

    if (args.policyType === undefined || args.policyType === "snp-caci") {
      Object.keys(args.claims).forEach((key) => {
        if (CACI_SNP_POLICY_CLAIMS[key] === undefined) {
          throw new Error(
            `The claim '${key}' is not an allowed snp-caci claim`
          );
        }
      });
    } else if (args.policyType === "snp-cvm") {
      Object.keys(args.claims).forEach((key) => {
        if (CVM_SNP_POLICY_CLAIMS[key] === undefined) {
          throw new Error(`The claim '${key}' is not an allowed snp-cvm claim`);
        }
      });
    } else {
      throw new Error(`Unknown policyType '${args.policyType}'`);
    }
  };

  validate(args);

  const type = args.delegateType;
  switch (type) {
    case "add":
      add(args.claims);
      break;
    case "remove":
      remove(args.claims);
      break;
    default:
      throw new Error(`Clean Room Policy with type '${type}' is not supported`);
  }
}

function getContractCleanRoomPolicyMapName(contractId: string): string {
  return "public:ccf.gov.policies.cleanroom-" + contractId;
}

export function getDelegateCleanRoomPolicyMapName(key: string): string {
  return "public:policies.cleanroom-delegates-" + key;
}

function getCleanRoomPolicyProps(mapName: string): ICleanRoomPolicyProps {
  const result: ICleanRoomPolicyProps = {};
  const cleanRoomPolicyMap = ccf.kv[mapName];
  cleanRoomPolicyMap.forEach((values: ArrayBuffer, key: ArrayBuffer) => {
    const kvKey = ccf.bufToStr(key);
    const kvValue = JSON.parse(ccf.bufToStr(values));
    result[kvKey] = kvValue;
    console.log(`key policy item with key: ${kvKey} and value: ${kvValue}`);
  });
  console.log(
    `Resulting clean room policy under map (${mapName}): ${JSON.stringify(
      result
    )}, keys: ${Object.keys(result)}, keys: ${Object.keys(result).length}`
  );
  return result;
}
