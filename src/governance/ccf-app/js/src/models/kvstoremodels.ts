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

export interface AcceptedDocumentStoreItem {
  contractId: string;
  data: any;
  proposalId: string;
  finalVotes: any;
}
export interface DocumentStoreItem {
  contractId: string;
  data: any;
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
