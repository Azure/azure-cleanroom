import "./snpattestation.tsp";
import "./encrypt.tsp";
import "./sign.tsp";

namespace Cleanroom.Governance;

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
