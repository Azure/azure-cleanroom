namespace Cleanroom.Governance;

/**
 * Event store item
 */
model EventStoreItem {
  /** Event timestamp */
  timestamp: string;

  /** Event data as binary */
  data: bytes;
}

/**
 * Accepted contract store item
 */
model AcceptedContractStoreItem {
  /** Contract data */
  data: unknown;

  /** Proposal ID */
  proposalId: string;

  /** Final votes */
  finalVotes: unknown;
}

/**
 * Contract store item
 */
model ContractStoreItem {
  /** Contract data */
  data: unknown;
}

/**
 * Contract execution status store item
 */
model ContractExecutionStatusStoreItem {
  /** Serialized member to status map */
  serializedMemberToStatusMap: string;
}

/**
 * Contract logging status store item
 */
model ContractLoggingStatusStoreItem {
  /** Status */
  status: string;
}

/**
 * Contract telemetry status store item
 */
model ContractTelemetryStatusStoreItem {
  /** Status */
  status: string;
}

/**
 * Accepted member document store item
 */
model AcceptedMemberDocumentStoreItem {
  /** Contract ID */
  contractId: string;

  /** Document data */
  data: unknown;

  /** Proposal ID */
  proposalId: string;

  /** Final votes */
  finalVotes: unknown;
}

/**
 * Document store item
 */
model DocumentStoreItem {
  /** Contract ID */
  contractId: string;

  /** Document data */
  data: unknown;
}

/**
 * User document runtime option status store item
 */
model UserDocumentRuntimeOptionStatusStoreItem {
  /** Serialized approver to status map */
  serializedApproverToStatusMap: string;
}

/**
 * Accepted user document store item
 */
model AcceptedUserDocumentStoreItem {
  /** Contract ID */
  contractId: string;

  /** Document data */
  data: unknown;

  /** Labels */
  labels: UserProposalLabels;

  /** Proposal ID */
  proposalId: string;

  /** Proposer ID */
  proposerId: string;

  /** Approvers */
  approvers?: UserProposalApprover[];

  /** Final votes */
  finalVotes: UserVote[];
}

/**
 * User document store item
 */
model UserDocumentStoreItem {
  /** Contract ID */
  contractId: string;

  /** Document data */
  data: unknown;

  /** Labels */
  labels: UserProposalLabels;

  /** Approvers */
  approvers?: UserProposalApprover[];
}

/**
 * Proposal store item
 */
model ProposalStoreItem {
  /** Actions */
  actions: Action[];
}

/**
 * Action
 */
model Action {
  /** Action name */
  name: string;

  /** Action arguments */
  args: unknown;
}

/**
 * Proposal info item
 */
model ProposalInfoItem {
  /** Proposal state */
  state: string;
}

/**
 * User proposal store item
 */
model UserProposalStoreItem {
  /** User actions */
  actions: UserAction[];
}

/**
 * User action
 */
model UserAction {
  /** Action name */
  name: string;

  /** Approvers */
  approvers?: UserProposalApprover[];

  /** Action arguments */
  args: unknown;
}

/**
 * User proposal approver
 */
model UserProposalApprover {
  /** Approver ID */
  approverId: string;

  /** Approver ID type */
  approverIdType: string;
}

/**
 * User proposal labels (key-value pairs)
 */
alias UserProposalLabels = Record<string>;

/**
 * User vote
 */
model UserVote {
  /** Approver ID */
  approverId: string;

  /** Ballot value */
  ballot: string;
}

/**
 * User proposal info item
 */
model UserProposalInfoItem {
  /** Proposer ID */
  proposerId: string;

  /** Proposal state */
  state: string;

  /** Ballots */
  ballots: UserVote[];
}

/**
 * Secret store item
 */
model SecretStoreItem {
  /** Secret value */
  value: string;
}

/**
 * Delegate policy info item
 */
model DelegatePolicyInfoItem {
  /** Delegate type */
  delegateType: string;

  /** Delegate ID */
  delegateId: string;
}

/**
 * Signing key item
 */
model SigningKeyItem {
  /** Key ID */
  kid: string;

  /** Request ID */
  reqId: string;

  /** Public key */
  publicKey: string;

  /** Private key */
  privateKey: string;
}

/**
 * Deployment spec item
 */
model DeploymentSpecItem {
  /** Deployment specification data */
  data: unknown;
}

/**
 * Deployment info item
 */
model DeploymentInfoItem {
  /** Deployment information data */
  data: unknown;
}

/**
 * Runtime option store item
 */
model RuntimeOptionStoreItem {
  /** Status */
  status: string;
}

/**
 * User identity store item
 */
model UserIdentityStoreItem {
  /** Account type */
  accountType: string;

  /** Invitation ID */
  invitationId: string;

  /** User data */
  data: unknown;
}

/**
 * User invitation store item
 */
model UserInvitationStoreItem {
  /** Account type */
  accountType: string;
}

/**
 * User invitation info store item
 */
model UserInvitationInfoStoreItem {
  /** Invitation status */
  status: string;

  /** User information */
  userInfo: UserInfo;
}

/**
 * User information
 */
model UserInfo {
  /** User ID */
  userId: string;

  /** User data */
  data: UserData;
}

/**
 * User data
 */
model UserData {
  /** Tenant ID */
  tenantId?: string;
}

/**
 * Accepted user invitation store item
 */
model AcceptedUserInvitationStoreItem {
  // Empty placeholder
}
