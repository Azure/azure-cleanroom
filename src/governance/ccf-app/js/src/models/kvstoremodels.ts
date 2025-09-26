export interface EventStoreItem {
  timestamp: string;
  data: ArrayBuffer;
}

export interface AcceptedContractStoreItem {
  data: any;
  proposalId: string;
  finalVotes: any;
}
export interface ContractStoreItem {
  data: any;
}

export interface ContractExecutionStatusStoreItem {
  serializedMemberToStatusMap: string;
}

export interface ContractLoggingStatusStoreItem {
  status: string;
}

export interface ContractTelemetryStatusStoreItem {
  status: string;
}

export interface AcceptedMemberDocumentStoreItem {
  contractId: string;
  data: any;
  proposalId: string;
  finalVotes: any;
}
export interface DocumentStoreItem {
  contractId: string;
  data: any;
}

export interface UserDocumentRuntimeOptionStatusStoreItem {
  serializedApproverToStatusMap: string;
}

export interface AcceptedUserDocumentStoreItem {
  contractId: string;
  data: any;
  proposalId: string;
  proposerId: string;
  approvers?: UserProposalApprover[];
  finalVotes: {
    approverId: string;
    ballot: string;
  }[];
}
export interface UserDocumentStoreItem {
  contractId: string;
  data: any;
  approvers?: UserProposalApprover[];
}

export interface ProposalStoreItem {
  actions: Action[];
}

export interface Action {
  name: string;
  args: any;
}

export interface ProposalInfoItem {
  state: string;
}

export interface UserProposalStoreItem {
  actions: UserAction[];
}

export interface UserAction {
  name: string;
  approvers?: UserProposalApprover[];
  args: any;
}

export interface UserProposalApprover {
  approverId: string;
  approverIdType: string;
}

export interface UserProposalInfoItem {
  proposerId: string;
  state: string;
  ballots: {
    approverId: string;
    ballot: string;
  }[];
}

export interface SecretStoreItem {
  value: string;
}

export interface SigningKeyItem {
  kid: string;
  reqId: string;
  publicKey: string;
  privateKey: string;
}

export interface DeploymentSpecItem {
  data: any;
}

export interface RuntimeOptionStoreItem {
  status: string;
}

export interface UserIdentityStoreItem {
  accountType: string;
  invitationId: string;
  data: any;
}

export interface UserInvitationStoreItem {
  accountType: string;
}

export interface UserInvitationInfoStoreItem {
  status: string;
  userInfo: {
    userId: string;
    data: {
      tenantId?: string;
    };
  };
}

export interface AcceptedUserInvitationStoreItem {}
