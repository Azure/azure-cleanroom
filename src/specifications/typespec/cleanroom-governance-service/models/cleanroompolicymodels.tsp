import "./snpattestation.tsp";
import "./encrypt.tsp";
import "./sign.tsp";

namespace Azure.Cleanroom.Governance;

/**
 * Get clean room policy response
 */
model GetCleanRoomPolicyResponse {
  /** Proposal IDs */
  proposalIds: string[];

  /** Policy data */
  policy: unknown;
}

/**
 * Set clean room policy args
 */
model SetCleanRoomPolicyArgs {
  /** Contract ID */
  contractId: string;
}

/**
 * Set delegate clean room policy request
 */
model SetDelegateCleanRoomPolicyRequest {
  /** Attestation evidence */
  attestation: SnpEvidence;

  /** Encryption info */
  encrypt: Encrypt;

  /** Signature */
  sign: Sign;

  /** Encrypted request data */
  data: string;
}

/**
 * Set delegate clean room policy request data
 */
model SetDelegateCleanRoomPolicyRequestData {
  /** Delegate type */
  @encodedName("application/json", "type")
  delegateType: string;

  /** Delegate claims */
  claims: unknown;

  /** Policy type that identifies the set of claims */
  policyType: string;
}

/**
 * List delegate policies response
 */
model ListDelegatePoliciesResponse {
  /** Array of delegate policies */
  value: ListDelegatePolicyResponse[];
}

/**
 * List delegate policy response
 */
model ListDelegatePolicyResponse {
  /** Delegate type */
  delegateType: string;

  /** Delegate ID */
  delegateId: string;
}

/**
 * Get delegate policy response
 */
model GetDelegatePolicyResponse {
  /** Delegate policy properties */
  claims: CleanRoomPolicyProps;
}

/**
 * Clean room policy properties for SEV-SNP attestation
 */
model CleanRoomPolicyProps {
  /** Author key digest values */
  @encodedName("application/json", "x-ms-sevsnpvm-authorkeydigest")
  authorKeyDigest?: string[];

  /** Family ID values */
  @encodedName("application/json", "x-ms-sevsnpvm-familyId")
  familyId?: string[];

  /** Guest SVN values */
  @encodedName("application/json", "x-ms-sevsnpvm-guestsvn")
  guestSvn?: int32[];

  /** Host data values */
  @encodedName("application/json", "x-ms-sevsnpvm-hostdata")
  hostData?: string[];

  /** ID key digest values */
  @encodedName("application/json", "x-ms-sevsnpvm-idkeydigest")
  idKeyDigest?: string[];

  /** Image ID values */
  @encodedName("application/json", "x-ms-sevsnpvm-imageId")
  imageId?: string[];

  /** Indicates if debugging is allowed */
  @encodedName("application/json", "x-ms-sevsnpvm-is-debuggable")
  isDebuggable?: boolean;

  /** Launch measurement values */
  @encodedName("application/json", "x-ms-sevsnpvm-launchmeasurement")
  launchMeasurement?: string[];

  /** Report data values */
  @encodedName("application/json", "x-ms-sevsnpvm-reportdata")
  reportData?: string[];

  /** Report ID values */
  @encodedName("application/json", "x-ms-sevsnpvm-reportid")
  reportId?: string[];

  /** Indicates if SMT is allowed */
  @encodedName("application/json", "x-ms-sevsnpvm-smt-allowed")
  smtAllowed?: boolean;

  /** VMPL values */
  @encodedName("application/json", "x-ms-sevsnpvm-vmpl")
  vmpl?: int32[];

  /** Version values */
  @encodedName("application/json", "x-ms-ver")
  version?: string[];
}
