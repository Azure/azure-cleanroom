// From https://github.com/microsoft/CCF/blob/a4666e003e6cf2142db0d413082839501b4cca6f/tests/npm-app/src/endpoints/snp_attestation.ts
import { Base64 } from "js-base64";
import * as ccfapp from "@microsoft/ccf-app";
import {
  getCustomCleanRoomPolicyProps,
  getContractCleanRoomPolicyProps,
  hex,
  isEmpty
} from "../utils/utils";
import { SnpAttestationClaims } from "./SnpAttestationClaims";
import * as ccfsnp from "@microsoft/ccf-app/snp_attestation";

export interface SnpEvidence {
  evidence: string;
  endorsements: string;
  uvm_endorsements: string;
  endorsed_tcb?: string;
}

export interface TcbVersion {
  boot_loader: number;
  tee: number;
  snp: number;
  microcode: number;
}

export interface SnpAttestationResult {
  attestation: {
    version: number;
    guest_svn: number;
    policy: {
      abi_minor: number;
      abi_major: number;
      smt: number;
      migrate_ma: number;
      debug: number;
      single_socket: number;
    };
    family_id: string;
    image_id: string;
    vmpl: number;
    signature_algo: number;
    platform_version: TcbVersion;
    platform_info: {
      smt_en: number;
      tsme_en: number;
    };
    flags: {
      author_key_en: number;
      mask_chip_key: number;
      signing_key: number;
    };
    report_data: string;
    measurement: string;
    host_data: string;
    id_key_digest: string;
    author_key_digest: string;
    report_id: string;
    report_id_ma: string;
    reported_tcb: TcbVersion;
    chip_id: string;
    committed_tcb: TcbVersion;
    current_minor: number;
    current_build: number;
    current_major: number;
    committed_build: number;
    committed_minor: number;
    committed_major: number;
    launch_tcb: TcbVersion;
    signature: {
      r: string;
      s: string;
    };
  };
  uvm_endorsements?: {
    did: string;
    feed: string;
    svn: string;
  };
}

export function verifySnpAttestation(
  contractId: string,
  attestation: SnpEvidence
): SnpAttestationResult {
  return verifySnpAttestationInternal(contractId, attestation, true);
}

export function verifySnpAttestationViaCustomPolicy(
  key: string,
  attestation: SnpEvidence
): SnpAttestationResult {
  return verifySnpAttestationInternal(key, attestation, false);
}

function verifySnpAttestationInternal(
  policyKey: string,
  attestation: SnpEvidence,
  isContract: boolean
): SnpAttestationResult {
  const evidence = ccfapp
    .typedArray(Uint8Array)
    .encode(Base64.toUint8Array(attestation.evidence));
  const endorsements = ccfapp
    .typedArray(Uint8Array)
    .encode(Base64.toUint8Array(attestation.endorsements));
  const uvm_endorsements =
    attestation.uvm_endorsements !== undefined
      ? ccfapp
          .typedArray(Uint8Array)
          .encode(Base64.toUint8Array(attestation.uvm_endorsements))
      : undefined;

  const r = ccfsnp.verifySnpAttestation(
    evidence,
    endorsements,
    uvm_endorsements,
    attestation.endorsed_tcb
  );

  const claimsProvider = new SnpAttestationClaims(r);
  const attestationClaims = claimsProvider.getClaims();

  // Get the clean room policy.
  const cleanroomPolicy = isContract
    ? getContractCleanRoomPolicyProps(policyKey)
    : getCustomCleanRoomPolicyProps(policyKey);

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
      throw Error(`Missing claim in attestation: ${key}`);
    }
    if (
      policyValue.filter((p) => {
        console.log(`Check if policy value ${p} === ${attestationValue}`);
        return p === attestationValue;
      }).length === 0
    ) {
      throw Error(
        `Attestation claim ${key}, value ${attestationValue} does not match policy values: ${policyValue}`
      );
    }
  }

  return {
    attestation: {
      ...r.attestation,
      family_id: hex(r.attestation.family_id),
      image_id: hex(r.attestation.image_id),
      report_data: hex(r.attestation.report_data),
      measurement: hex(r.attestation.measurement),
      host_data: hex(r.attestation.host_data),
      id_key_digest: hex(r.attestation.id_key_digest),
      author_key_digest: hex(r.attestation.author_key_digest),
      report_id: hex(r.attestation.report_id),
      report_id_ma: hex(r.attestation.report_id_ma),
      chip_id: hex(r.attestation.chip_id),
      signature: {
        r: hex(r.attestation.signature.r),
        s: hex(r.attestation.signature.s)
      }
    },
    uvm_endorsements: r.uvm_endorsements
  };
}
