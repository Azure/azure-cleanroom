import "./models.tsp";

using Cleanroom.Governance;

namespace Cleanroom.Governance.Client;

// ============================================================================
// Library Interfaces (No Routes - Pure Operations)
// ============================================================================
// These interfaces define the operations without HTTP routing details.
// They can be implemented by token-based or attestation-based clients.

/**
 * Contract operations
 */
interface Contracts {
  listContracts(state?: string): ContractsListResponse;
  getContract(contractId: string): ContractResponse;
  updateContract(contractId: string, contract: PutContractRequest): void;
  proposeContractChange(
    contractId: string,
    proposal: ContractProposal,
  ): ProposalResponse;
  voteAcceptContract(contractId: string, vote: ProposalVote): VoteResponse;
  voteRejectContract(contractId: string, vote: ProposalVote): VoteResponse;
  voteOnContract(contractId: string, vote: CustomBallotVote): VoteResponse;
}

/**
 * Certificate Authority operations
 */
interface CertificateAuthority {
  generateSigningKey(contractId: string): SigningKeyResponse;
  getInfo(contractId: string): CAInfoResponse;

  // Attestation-based operation
  generateEndorsedCert(contractId: string): EncryptedResponse;
}

/**
 * Clean Room Policy operations
 */
interface CleanRoomPolicy {
  getCleanRoomPolicy(contractId: string): CleanRoomPolicyResponse;
  proposeCleanRoomPolicyChange(
    contractId: string,
    proposal: CleanRoomPolicyProposal,
  ): ProposalResponse;
  getDelegatePolicy(
    contractId: string,
    delegateType: string,
    delegateId: string,
  ): DelegatePolicyResponse;
  listDelegatePolicies(contractId: string): DelegatePoliciesListResponse;

  // Attestation-based operation
  setDelegatePolicyWithAttestation(
    delegateType: string,
    delegateId: string,
    request: AttestedRequest,
  ): void;
}

/**
 * Contract Proposal operations
 */
interface ContractProposals {
  getDeploymentProposal(
    contractId: string,
    proposalType: "deploymentspec" | "deploymentinfo",
  ): DeploymentProposalResponse;
  proposeDeployment(
    contractId: string,
    proposalType: "deploymentspec" | "deploymentinfo",
    proposal: unknown,
  ): ProposalResponse;
}

/**
 * Contract Runtime Options operations
 */
interface ContractRuntimeOptions {
  enableExecution(contractId: string): void;
  disableExecution(contractId: string): void;
  proposeEnableLogging(contractId: string): ProposalResponse;
  proposeDisableLogging(contractId: string): ProposalResponse;
  proposeEnableTelemetry(contractId: string): ProposalResponse;
  proposeDisableTelemetry(contractId: string): ProposalResponse;
  checkExecutionStatus(contractId: string): RuntimeStatusResponse;
  checkLoggingStatus(contractId: string): RuntimeStatusResponse;
  checkTelemetryStatus(contractId: string): RuntimeStatusResponse;

  // Consent check operations (attestation-based)
  checkExecutionConsent(contractId?: string): ConsentCheckResponse;
  checkLoggingConsent(contractId?: string): ConsentCheckResponse;
  checkTelemetryConsent(contractId?: string): ConsentCheckResponse;
}

/**
 * Event operations
 */
interface Events {
  getEvents(
    contractId: string,
    id?: string,
    scope?: string,
    from_seqno?: int64,
    to_seqno?: int64,
    max_seqno_per_page?: int32,
  ): EventsResponse;

  // Store event (attestation-based)
  storeEvent(id?: string, scope?: string, event: AttestedEventRequest): void;
}

/**
 * Member Document operations
 */
interface MemberDocuments {
  listMemberDocuments(): MemberDocumentsList;
  getMemberDocument(documentId: string): GetMemberDocumentResponse;
  updateMemberDocument(documentId: string, document: DocumentData): void;
  proposeMemberDocumentChange(
    documentId: string,
    proposal: DocumentProposal,
  ): ProposalResponse;
  voteAcceptMemberDocument(
    documentId: string,
    vote: ProposalVote,
  ): VoteResponse;
  voteRejectMemberDocument(
    documentId: string,
    vote: ProposalVote,
  ): VoteResponse;
  voteOnMemberDocument(
    documentId: string,
    vote: CustomBallotVote,
  ): VoteResponse;

  // Attestation-based operation
  getMemberDocumentWithAttestation(documentId: string): EncryptedResponse;
}

/**
 * Member operations
 */
interface Members {
  listMembers(): MembersResponse;
  updateStateDigest(): StateDigestResponse;
  acknowledgeStateDigest(): string;
}

/**
 * Network operations
 */
interface Network {
  showNetwork(): NetworkInfoResponse;
}

/**
 * Node operations
 */
interface Node {
  checkAppReady(): void;
  checkGovReady(): void;
}

/**
 * OAuth operations
 */
interface OAuth {
  getTokenSubjectPolicy(
    contractId: string,
    subjectName: string,
  ): TokenSubjectPolicyResponse;

  // Attestation-based operation
  getTokenWithAttestation(
    `sub`: string,
    tenantId: string,
    aud: string,
    iss?: string,
  ): EncryptedResponse;
}

/**
 * OIDC operations
 */
interface Oidc {
  generateIssuerSigningKey(): SigningKeyResponse;
  setIssuerUrl(config: IssuerUrlConfig): void;
  getIssuerInfo(): IssuerInfoResponse;
}

/**
 * Proposal operations
 */
interface Proposals {
  listProposals(): ProposalsListResponse;
  getProposal(proposalId: string): ProposalDetailsResponse;
  getProposalVotes(proposalId: string): VotesListResponse;
  getProposalHistorical(
    proposalId: string,
    queryString?: string,
  ): ProposalHistoricalResponse;
  getProposalActions(proposalId: string): ProposalActionsResponse;
  withdrawProposal(proposalId: string): WithdrawResponse;
  createProposal(proposal: CreateProposalRequest): ProposalResponse;
  voteAcceptProposal(proposalId: string): VoteResponse;
  voteRejectProposal(proposalId: string): VoteResponse;
  voteOnProposal(proposalId: string, vote: CustomBallot): VoteResponse;
}

/**
 * Runtime Options operations
 */
interface RuntimeOptions {
  proposeRuntimeOption(
    option: string,
    action: "enable" | "disable",
  ): ProposalResponse;
  checkRuntimeOptionStatus(option: string): RuntimeOptionStatusResponse;
}

/**
 * Secret operations
 */
interface Secrets {
  listSecrets(contractId: string): SecretsListResponse;
  storeSecret(
    contractId: string,
    secretName: string,
    secret: SecretData,
  ): SecretStoreResponse;
  getSecretPolicy(contractId: string, secretId: string): SecretPolicyResponse;

  // Attestation-based operations
  getSecretWithAttestation(secretId: string): EncryptedResponse;
  storeSecretWithAttestation(
    secretName: string,
    request: AttestedRequest,
  ): SecretStoreResponse;
  setSecretPolicyWithAttestation(
    secretId: string,
    request: AttestedRequest,
  ): void;
}

/**
 * Update operations
 */
interface Updates {
  checkUpdates(): CheckUpdateResponse;
}

/**
 * User Document operations
 */
interface UserDocuments {
  listUserDocuments(labelSelector?: string): UserDocumentsListResponse;
  getUserDocument(documentId: string): UserDocumentResponse;
  updateUserDocument(
    documentId: string,
    document: PutUserDocumentRequest,
  ): void;
  proposeUserDocumentChange(
    documentId: string,
    proposal: UserDocumentProposal,
  ): ProposalResponse;
  voteAcceptUserDocument(documentId: string, vote: ProposalVote): VoteResponse;
  voteRejectUserDocument(documentId: string, vote: ProposalVote): VoteResponse;
  voteOnUserDocument(documentId: string, vote: CustomBallotVote): VoteResponse;
  enableUserDocumentRuntimeOption(
    documentId: string,
    runtimeOption: string,
  ): void;
  disableUserDocumentRuntimeOption(
    documentId: string,
    runtimeOption: string,
  ): void;
  checkUserDocumentRuntimeOptionStatus(
    documentId: string,
    runtimeOption: string,
  ): RuntimeOptionStatusResponse;

  // Attestation-based operations
  getUserDocumentWithAttestation(documentId: string): EncryptedResponse;
  checkUserDocumentExecutionConsent(documentId: string): ConsentCheckResponse;
  checkUserDocumentTelemetryConsent(documentId: string): ConsentCheckResponse;
}

/**
 * User Identity operations
 */
interface UserIdentities {
  listUserIdentities(): UserIdentitiesResponse;
  getUserIdentity(identityId: string): UserIdentityResponse;
  addUserIdentity(identity: AddUserIdentityRequest): ProposalResponse;
}

/**
 * User Invitation operations
 */
interface UserInvitations {
  listUserInvitations(): UserInvitationsResponse;
  getUserInvitation(invitationId: string): UserInvitationResponse;
  acceptInvitation(invitationId: string): void;
  proposeInvitation(
    invitation: InvitationProposalInput,
  ): InvitationProposalResponse;
}

/**
 * User Proposal operations
 */
interface UserProposals {
  createUserProposal(proposal: UserProposalRequest): ProposalResponse;
  getUserProposal(proposalId: string): UserProposalResponse;
  getUserProposalStatus(proposalId: string): ProposalStatusResponse;
  withdrawUserProposal(proposalId: string): WithdrawResponse;
  voteAcceptUserProposal(proposalId: string): VoteResponse;
  voteRejectUserProposal(proposalId: string): VoteResponse;
}

/**
 * User operations
 */
interface Users {
  listUsers(): UsersListResponse;
  getUser(userId: string): UserResponse;

  // Check if user is active (token-based)
  isUserActive(authToken: string): UserActiveResponse;
}

/**
 * Workspace operations
 */
interface Workspace {
  checkReady(): ReadyResponse;
  configure(config: WorkspaceConfigurationModel): string;
  getAccessToken(): AccessTokenResponse;
  showConfiguration(signingKey?: boolean): WorkspaceConfiguration;
  getConstitution(): string;
  getServiceInfo(): string;
  getJsAppEndpoints(): string;
  getJsAppModules(): unknown;
  listJsAppModules(): unknown;
  getJsAppModule(moduleName: string): string;
  getJsAppBundle(): unknown;
}

/**
 * Complete Governance Client interface
 * Aggregates all governance operations for token-based authentication
 */
interface GovernanceClient
  extends Contracts,
    CertificateAuthority,
    CleanRoomPolicy,
    ContractProposals,
    ContractRuntimeOptions,
    Events,
    MemberDocuments,
    Members,
    Network,
    Node,
    OAuth,
    Oidc,
    Proposals,
    RuntimeOptions,
    Secrets,
    Updates,
    UserDocuments,
    UserIdentities,
    UserInvitations,
    UserProposals,
    Users,
    Workspace {}
