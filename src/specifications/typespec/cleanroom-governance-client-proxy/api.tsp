import "@typespec/http";
import "@typespec/rest";
import "./models.tsp";

using TypeSpec.Http;
using TypeSpec.Rest;
using Cleanroom.Governance;
using Cleanroom.Governance.Client;

namespace Cleanroom.Governance.Client.Proxy;

// ============================================================================
// Unified HTTP API
// ============================================================================
// This service exposes all operations from both token and attestation modes.
// Operations unavailable in the current mode return HTTP 501 Not Implemented.
//
// Token Mode Operations: Contracts, proposals, voting, member management
// Attestation Mode Operations: Encrypted secrets, consent checks, endorsed certs
// Shared Operations: Events, member lists, runtime status checks

// ============================================================================
// Contract Management (Token Mode)
// ============================================================================

@route("/contracts")
interface Contracts {
  @get
  @doc("Lists all contracts. Token mode only.")
  listContracts(@query state?: string): ContractsListResponse;

  @route("/{contractId}")
  @get
  @doc("Gets contract details. Token mode only.")
  getContract(@path contractId: string): ContractResponse;

  @route("/{contractId}")
  @put
  @doc("Updates a contract. Token mode only.")
  updateContract(
    @path contractId: string,
    @body contract: PutContractRequest,
  ): void;

  @route("/{contractId}/propose")
  @post
  @doc("Proposes a contract change. Token mode only.")
  proposeContractChange(
    @path contractId: string,
    @body proposal: ContractProposal,
  ): ProposalResponse;

  @route("/{contractId}/vote_accept")
  @post
  @doc("Votes to accept a contract. Token mode only.")
  voteAcceptContract(
    @path contractId: string,
    @body vote: ProposalVote,
  ): VoteResponse;

  @route("/{contractId}/vote_reject")
  @post
  @doc("Votes to reject a contract. Token mode only.")
  voteRejectContract(
    @path contractId: string,
    @body vote: ProposalVote,
  ): VoteResponse;

  @route("/{contractId}/vote")
  @post
  @doc("Casts a custom ballot vote on a contract. Token mode only.")
  voteOnContract(
    @path contractId: string,
    @body vote: CustomBallotVote,
  ): VoteResponse;
}

// ============================================================================
// Certificate Authority
// ============================================================================

@route("/contracts/{contractId}/ca")
interface CertificateAuthority {
  @route("/generateSigningKey")
  @post
  @doc("Generates a signing key. Token mode only.")
  generateSigningKey(@path contractId: string): SigningKeyResponse;

  @route("/info")
  @get
  @doc("Gets CA information. Token mode only.")
  getInfo(@path contractId: string): CAInfoResponse;
}

@route("/ca")
interface CertificateAuthorityAttestation {
  @route("/generateEndorsedCert")
  @post
  @doc("Generates an endorsed certificate using attestation. Attestation mode only. Request must be signed and include SNP attestation report. Response is encrypted.")
  generateEndorsedCert(@body request: AttestedRequest): EncryptedResponse;
}

// ============================================================================
// Clean Room Policy
// ============================================================================

@route("/contracts/{contractId}/cleanroompolicy")
interface CleanRoomPolicy {
  @get
  @doc("Gets clean room policy. Token mode only.")
  getCleanRoomPolicy(@path contractId: string): CleanRoomPolicyResponse;

  @route("/propose")
  @post
  @doc("Proposes a clean room policy change. Token mode only.")
  proposeCleanRoomPolicyChange(
    @path contractId: string,
    @body proposal: CleanRoomPolicyProposal,
  ): ProposalResponse;

  @route("/delegates/{delegateType}/{delegateId}")
  @get
  @doc("Gets delegate policy. Works in both modes.")
  getDelegatePolicy(
    @path contractId: string,
    @path delegateType: string,
    @path delegateId: string,
  ): DelegatePolicyResponse;

  @route("/delegates")
  @get
  @doc("Lists delegate policies. Token mode only.")
  listDelegatePolicies(@path contractId: string): DelegatePoliciesListResponse;
}

@route("/cleanroompolicy/delegates")
interface CleanRoomPolicyDelegatesAttestation {
  @route("/{delegateType}/{delegateId}")
  @post
  @doc("Sets clean room delegate policy with attestation. Attestation mode only. Request must be signed and include SNP attestation report.")
  setDelegatePolicy(
    @path delegateType: string,
    @path delegateId: string,
    @body request: AttestedRequest,
  ): void;
}

// ============================================================================
// Contract Proposals (Token Mode)
// ============================================================================

@route("/contracts/{contractId}")
interface ContractProposals {
  @route("/{proposalType}")
  @get
  @doc("Gets deployment proposal. Token mode only.")
  getDeploymentProposal(
    @path contractId: string,
    @path proposalType: "deploymentspec" | "deploymentinfo",
  ): DeploymentProposalResponse;

  @route("/{proposalType}/propose")
  @post
  @doc("Proposes a deployment. Token mode only.")
  proposeDeployment(
    @path contractId: string,
    @path proposalType: "deploymentspec" | "deploymentinfo",
    @body proposal: unknown,
  ): ProposalResponse;
}

// ============================================================================
// Runtime Options
// ============================================================================

@route("/contracts/{contractId}")
interface ContractRuntimeOptions {
  @route("/execution/enable")
  @post
  @doc("Enables execution. Token mode only.")
  enableExecution(@path contractId: string): void;

  @route("/execution/disable")
  @post
  @doc("Disables execution. Token mode only.")
  disableExecution(@path contractId: string): void;

  @route("/logging/propose-enable")
  @post
  @doc("Proposes to enable logging. Token mode only.")
  proposeEnableLogging(@path contractId: string): ProposalResponse;

  @route("/logging/propose-disable")
  @post
  @doc("Proposes to disable logging. Token mode only.")
  proposeDisableLogging(@path contractId: string): ProposalResponse;

  @route("/telemetry/propose-enable")
  @post
  @doc("Proposes to enable telemetry. Token mode only.")
  proposeEnableTelemetry(@path contractId: string): ProposalResponse;

  @route("/telemetry/propose-disable")
  @post
  @doc("Proposes to disable telemetry. Token mode only.")
  proposeDisableTelemetry(@path contractId: string): ProposalResponse;

  @route("/checkstatus/execution")
  @post
  @doc("Checks execution status. Works in both modes.")
  checkExecutionStatus(@path contractId: string): RuntimeStatusResponse;

  @route("/checkstatus/logging")
  @post
  @doc("Checks logging status. Works in both modes.")
  checkLoggingStatus(@path contractId: string): RuntimeStatusResponse;

  @route("/checkstatus/telemetry")
  @post
  @doc("Checks telemetry status. Works in both modes.")
  checkTelemetryStatus(@path contractId: string): RuntimeStatusResponse;
}

@route("/consentcheck")
interface ConsentCheck {
  @route("/execution")
  @post
  @doc("Checks execution consent with attestation. Attestation mode only. Request must include SNP attestation report.")
  checkExecutionConsent(
    @body request: AttestationRequest,
  ): ConsentCheckResponse;

  @route("/logging")
  @post
  @doc("Checks logging consent with attestation. Attestation mode only. Request must include SNP attestation report.")
  checkLoggingConsent(@body request: AttestationRequest): ConsentCheckResponse;

  @route("/telemetry")
  @post
  @doc("Checks telemetry consent with attestation. Attestation mode only. Request must include SNP attestation report.")
  checkTelemetryConsent(
    @body request: AttestationRequest,
  ): ConsentCheckResponse;
}

// ============================================================================
// Events
// ============================================================================

@route("/contracts/{contractId}/events")
interface Events {
  @get
  @doc("Gets events. Token mode only.")
  getEvents(
    @path contractId: string,
    @query id?: string,
    @query scope?: string,
    @query from_seqno?: int64,
    @query to_seqno?: int64,
    @query max_seqno_per_page?: int32,
  ): EventsResponse;
}

@route("/events")
interface EventsAttestation {
  @put
  @doc("Stores an event with attestation. Attestation mode only. Request must be signed and include SNP attestation report.")
  storeEvent(
    @query id?: string,
    @query scope?: string,
    @body event: AttestedEventRequest,
  ): void;
}

// ============================================================================
// Member Documents
// ============================================================================

@route("/memberdocuments")
interface MemberDocuments {
  @get
  @doc("Lists member documents. Token mode only.")
  listMemberDocuments(): MemberDocumentsList;

  @route("/{documentId}")
  @get
  @doc("Gets member document. Token mode only.")
  getMemberDocument(@path documentId: string): GetMemberDocumentResponse;

  @route("/{documentId}")
  @put
  @doc("Updates member document. Token mode only.")
  updateMemberDocument(
    @path documentId: string,
    @body document: DocumentData,
  ): void;

  @route("/{documentId}/propose")
  @post
  @doc("Proposes member document change. Token mode only.")
  proposeMemberDocumentChange(
    @path documentId: string,
    @body proposal: DocumentProposal,
  ): ProposalResponse;

  @route("/{documentId}/vote_accept")
  @post
  @doc("Votes to accept member document. Token mode only.")
  voteAcceptMemberDocument(
    @path documentId: string,
    @body vote: ProposalVote,
  ): VoteResponse;

  @route("/{documentId}/vote_reject")
  @post
  @doc("Votes to reject member document. Token mode only.")
  voteRejectMemberDocument(
    @path documentId: string,
    @body vote: ProposalVote,
  ): VoteResponse;

  @route("/{documentId}/vote")
  @post
  @doc("Casts custom ballot on member document. Token mode only.")
  voteOnMemberDocument(
    @path documentId: string,
    @body vote: CustomBallotVote,
  ): VoteResponse;

  @route("/{documentId}")
  @post
  @doc("Retrieves member document with attestation. Attestation mode only. Requires public key and attestation report. Response is encrypted with provided public key.")
  getMemberDocumentWithAttestation(
    @path documentId: string,
    @body request: DocumentRetrievalRequest,
  ): EncryptedResponse;
}

// ============================================================================
// Members
// ============================================================================

@route("/members")
interface Members {
  @get
  @doc("Lists all members. Works in both modes.")
  listMembers(): MembersResponse;

  @route("/statedigests/update")
  @post
  @doc("Updates state digest. Token mode only.")
  updateStateDigest(): StateDigestResponse;

  @route("/statedigests/ack")
  @post
  @doc("Acknowledges state digest. Token mode only.")
  acknowledgeStateDigest(): string;
}

// ============================================================================
// Network & Node (Token Mode)
// ============================================================================

@route("/network")
interface Network {
  @route("/show")
  @get
  @doc("Shows network information. Token mode only.")
  showNetwork(): NetworkInfoResponse;
}

@route("/node")
interface Node {
  @route("/ready/app")
  @get
  @doc("Checks if application is ready. Token mode only.")
  checkAppReady(): void;

  @route("/ready/gov")
  @get
  @doc("Checks if governance is ready. Token mode only.")
  checkGovReady(): void;
}

// ============================================================================
// OAuth
// ============================================================================

@route("/contracts/{contractId}/oauth")
interface OAuth {
  @route("/federation/subjects/{subjectName}/cleanroompolicy")
  @get
  @doc("Gets OAuth token subject policy. Token mode only.")
  getTokenSubjectPolicy(
    @path contractId: string,
    @path subjectName: string,
  ): TokenSubjectPolicyResponse;
}

@route("/oauth")
interface OAuthAttestation {
  @route("/token")
  @post
  @doc("Gets OAuth token with attestation. Attestation mode only. Requires public key and attestation report. Response contains encrypted JWT token.")
  getToken(
    @query `sub`: string,
    @query tenantId: string,
    @query aud: string,
    @query iss?: string,
    @body request: TokenRequest,
  ): EncryptedResponse;
}

// ============================================================================
// OIDC (Token Mode)
// ============================================================================

@route("/oidc")
interface OIDC {
  @route("/generateSigningKey")
  @post
  @doc("Generates OIDC signing key. Token mode only.")
  generateIssuerSigningKey(): SigningKeyResponse;

  @route("/setIssuerUrl")
  @post
  @doc("Sets OIDC issuer URL. Token mode only.")
  setIssuerUrl(@body config: IssuerUrlConfig): void;

  @route("/issuerInfo")
  @get
  @doc("Gets OIDC issuer information. Token mode only.")
  getIssuerInfo(): IssuerInfoResponse;
}

// ============================================================================
// Proposals (Token Mode)
// ============================================================================

@route("/proposals")
interface Proposals {
  @get
  @doc("Lists all proposals. Token mode only.")
  listProposals(): ProposalsListResponse;

  @route("/{proposalId}")
  @get
  @doc("Gets proposal details. Token mode only.")
  getProposal(@path proposalId: string): ProposalDetailsResponse;

  @route("/{proposalId}/votes")
  @get
  @doc("Gets proposal votes. Token mode only.")
  getProposalVotes(@path proposalId: string): VotesListResponse;

  @route("/{proposalId}/historical")
  @get
  @doc("Gets proposal history. Token mode only.")
  getProposalHistorical(
    @path proposalId: string,
    @query queryString?: string,
  ): ProposalHistoricalResponse;

  @route("/{proposalId}/actions")
  @get
  @doc("Gets proposal actions. Token mode only.")
  getProposalActions(@path proposalId: string): ProposalActionsResponse;

  @route("/{proposalId}/withdraw")
  @post
  @doc("Withdraws a proposal. Token mode only.")
  withdrawProposal(@path proposalId: string): WithdrawResponse;

  @route("/create")
  @post
  @doc("Creates a new proposal. Token mode only.")
  createProposal(@body proposal: CreateProposalRequest): ProposalResponse;

  @route("/{proposalId}/ballots/vote_accept")
  @post
  @doc("Votes to accept proposal. Token mode only.")
  voteAcceptProposal(@path proposalId: string): VoteResponse;

  @route("/{proposalId}/ballots/vote_reject")
  @post
  @doc("Votes to reject proposal. Token mode only.")
  voteRejectProposal(@path proposalId: string): VoteResponse;

  @route("/{proposalId}/ballots/vote")
  @post
  @doc("Casts custom ballot on proposal. Token mode only.")
  voteOnProposal(
    @path proposalId: string,
    @body vote: CustomBallot,
  ): VoteResponse;
}

@route("/runtimeoptions")
interface RuntimeOptions {
  @route("/{option}/propose-{action}")
  @post
  @doc("Proposes runtime option change. Token mode only.")
  proposeRuntimeOption(
    @path option: string,
    @path action: "enable" | "disable",
  ): ProposalResponse;

  @route("/checkstatus/{option}")
  @post
  @doc("Checks runtime option status. Works in both modes.")
  checkRuntimeOptionStatus(@path option: string): RuntimeOptionStatusResponse;
}

// ============================================================================
// Secrets
// ============================================================================

@route("/contracts/{contractId}/secrets")
interface Secrets {
  @get
  @doc("Lists secrets. Token mode only.")
  listSecrets(@path contractId: string): SecretsListResponse;

  @route("/{secretName}")
  @put
  @doc("Stores a secret. Token mode only.")
  storeSecret(
    @path contractId: string,
    @path secretName: string,
    @body secret: SecretData,
  ): SecretStoreResponse;

  @route("/{secretId}/cleanroompolicy")
  @get
  @doc("Gets secret policy. Token mode only.")
  getSecretPolicy(
    @path contractId: string,
    @path secretId: string,
  ): SecretPolicyResponse;
}

@route("/secrets")
interface SecretsAttestation {
  @route("/{secretId}")
  @post
  @doc("Retrieves secret with attestation. Attestation mode only. Requires public key and attestation report. Response is encrypted with provided public key.")
  getSecret(
    @path secretId: string,
    @body request: SecretRetrievalRequest,
  ): EncryptedResponse;

  @route("/{secretName}")
  @put
  @doc("Stores a secret with attestation. Attestation mode only. Request must be signed and include SNP attestation report.")
  storeSecretWithAttestation(
    @path secretName: string,
    @body request: AttestedRequest,
  ): SecretStoreResponse;

  @route("/{secretId}/cleanroompolicy")
  @post
  @doc("Sets clean room policy for secret with attestation. Attestation mode only. Request must be signed and include SNP attestation report.")
  setSecretPolicy(@path secretId: string, @body request: AttestedRequest): void;
}

// ============================================================================
// Updates (Token Mode)
// ============================================================================

@route("/checkUpdates")
interface Updates {
  @get
  @doc("Checks for available updates. Token mode only.")
  checkUpdates(): CheckUpdateResponse;
}

// ============================================================================
// User Documents
// ============================================================================

@route("/userdocuments")
interface UserDocuments {
  @get
  @doc("Lists user documents. Token mode only.")
  listUserDocuments(@query labelSelector?: string): UserDocumentsListResponse;

  @route("/{documentId}")
  @get
  @doc("Gets user document. Token mode only.")
  getUserDocument(@path documentId: string): UserDocumentResponse;

  @route("/{documentId}")
  @put
  @doc("Updates user document. Token mode only.")
  updateUserDocument(
    @path documentId: string,
    @body document: PutUserDocumentRequest,
  ): void;

  @route("/{documentId}/propose")
  @post
  @doc("Proposes user document change. Token mode only.")
  proposeUserDocumentChange(
    @path documentId: string,
    @body proposal: UserDocumentProposal,
  ): ProposalResponse;

  @route("/{documentId}/vote_accept")
  @post
  @doc("Votes to accept user document. Token mode only.")
  voteAcceptUserDocument(
    @path documentId: string,
    @body vote: ProposalVote,
  ): VoteResponse;

  @route("/{documentId}/vote_reject")
  @post
  @doc("Votes to reject user document. Token mode only.")
  voteRejectUserDocument(
    @path documentId: string,
    @body vote: ProposalVote,
  ): VoteResponse;

  @route("/{documentId}/vote")
  @post
  @doc("Casts custom ballot on user document. Token mode only.")
  voteOnUserDocument(
    @path documentId: string,
    @body vote: CustomBallotVote,
  ): VoteResponse;

  @route("/{documentId}/runtimeoptions/{runtimeOption}/enable")
  @post
  @doc("Enables user document runtime option. Token mode only.")
  enableUserDocumentRuntimeOption(
    @path documentId: string,
    @path runtimeOption: string,
  ): void;

  @route("/{documentId}/runtimeoptions/{runtimeOption}/disable")
  @post
  @doc("Disables user document runtime option. Token mode only.")
  disableUserDocumentRuntimeOption(
    @path documentId: string,
    @path runtimeOption: string,
  ): void;

  @route("/{documentId}/checkstatus/{runtimeOption}")
  @post
  @doc("Checks user document runtime option status. Works in both modes.")
  checkUserDocumentRuntimeOptionStatus(
    @path documentId: string,
    @path runtimeOption: string,
  ): RuntimeOptionStatusResponse;

  @route("/{documentId}")
  @post
  @doc("Retrieves user document with attestation. Attestation mode only. Requires public key and attestation report. Response is encrypted with provided public key.")
  getUserDocumentWithAttestation(
    @path documentId: string,
    @body request: DocumentRetrievalRequest,
  ): EncryptedResponse;

  @route("/{documentId}/consentcheck/execution")
  @post
  @doc("Checks user document execution consent with attestation. Attestation mode only. Request must include SNP attestation report.")
  checkExecutionConsentAttestation(
    @path documentId: string,
    @body request: AttestationRequest,
  ): ConsentCheckResponse;

  @route("/{documentId}/consentcheck/telemetry")
  @post
  @doc("Checks user document telemetry consent with attestation. Attestation mode only. Request must include SNP attestation report.")
  checkTelemetryConsentAttestation(
    @path documentId: string,
    @body request: AttestationRequest,
  ): ConsentCheckResponse;
}

// ============================================================================
// User Management (Token Mode)
// ============================================================================

@route("/users/identities")
interface UserIdentities {
  @get
  @doc("Lists user identities. Token mode only.")
  listUserIdentities(): UserIdentitiesResponse;

  @route("/{identityId}")
  @get
  @doc("Gets user identity. Token mode only.")
  getUserIdentity(@path identityId: string): UserIdentityResponse;

  @route("/add")
  @post
  @doc("Adds user identity. Token mode only.")
  addUserIdentity(@body identity: AddUserIdentityRequest): ProposalResponse;
}

@route("/users/invitations")
interface UserInvitations {
  @get
  @doc("Lists user invitations. Token mode only.")
  listUserInvitations(): UserInvitationsResponse;

  @route("/{invitationId}")
  @get
  @doc("Gets user invitation. Token mode only.")
  getUserInvitation(@path invitationId: string): UserInvitationResponse;

  @route("/{invitationId}/accept")
  @post
  @doc("Accepts invitation. Token mode only.")
  acceptInvitation(@path invitationId: string): void;

  @route("/propose")
  @post
  @doc("Proposes invitation. Token mode only.")
  proposeInvitation(
    @body invitation: InvitationProposalInput,
  ): InvitationProposalResponse;
}

@route("/users/proposals")
interface UserProposals {
  @route("/create")
  @post
  @doc("Creates user proposal. Token mode only.")
  createUserProposal(@body proposal: UserProposalRequest): ProposalResponse;

  @route("/{proposalId}")
  @get
  @doc("Gets user proposal. Token mode only.")
  getUserProposal(@path proposalId: string): UserProposalResponse;

  @route("/{proposalId}/status")
  @get
  @doc("Gets user proposal status. Token mode only.")
  getUserProposalStatus(@path proposalId: string): ProposalStatusResponse;

  @route("/{proposalId}/withdraw")
  @post
  @doc("Withdraws user proposal. Token mode only.")
  withdrawUserProposal(@path proposalId: string): WithdrawResponse;

  @route("/{proposalId}/ballots/vote_accept")
  @post
  @doc("Votes to accept user proposal. Token mode only.")
  voteAcceptUserProposal(@path proposalId: string): VoteResponse;

  @route("/{proposalId}/ballots/vote_reject")
  @post
  @doc("Votes to reject user proposal. Token mode only.")
  voteRejectUserProposal(@path proposalId: string): VoteResponse;
}

@route("/users")
interface Users {
  @get
  @doc("Lists users. Token mode only.")
  listUsers(): UsersListResponse;

  @route("/{userId}")
  @get
  @doc("Gets user details. Token mode only.")
  getUser(@path userId: string): UserResponse;

  @route("/isactive")
  @post
  @doc("Checks if user is active using JWT token from Authorization header. Works in both modes.")
  isUserActive(@header Authorization: string): UserActiveResponse;
}

// ============================================================================
// Workspace Configuration
// ============================================================================

@route("/")
interface Workspace {
  @route("/ready")
  @get
  @doc("Checks service readiness. Works in both modes.")
  checkReady(): ReadyResponse;

  @route("/configure")
  @post
  @doc("Configures workspace. Token mode only.")
  configure(@body config: WorkspaceConfigurationModel): string;

  @route("/identity/accessToken")
  @get
  @doc("Gets identity access token. Token mode only.")
  getAccessToken(): AccessTokenResponse;

  @route("/show")
  @get
  @doc("Shows workspace configuration. Works in both modes (returns mode-specific config).")
  showConfiguration(
    @query signingKey?: boolean,
  ): WorkspaceConfiguration | SidecarWorkspaceConfigurationModel;

  @route("/constitution")
  @get
  @doc("Gets constitution. Token mode only.")
  getConstitution(): string;

  @route("/service/info")
  @get
  @doc("Gets service information. Token mode only.")
  getServiceInfo(): string;

  @route("/jsapp/endpoints")
  @get
  @doc("Gets JavaScript app endpoints. Token mode only.")
  getJsAppEndpoints(): string;

  @route("/jsapp/modules")
  @get
  @doc("Gets JavaScript app modules. Token mode only.")
  getJsAppModules(): unknown;

  @route("/jsapp/modules/list")
  @get
  @doc("Lists JavaScript app modules. Token mode only.")
  listJsAppModules(): unknown;

  @route("/jsapp/modules/{moduleName}")
  @get
  @doc("Gets specific JavaScript app module. Token mode only.")
  getJsAppModule(@path moduleName: string): string;

  @route("/jsapp/bundle")
  @get
  @doc("Gets JavaScript app bundle. Token mode only.")
  getJsAppBundle(): unknown;
}

// ============================================================================
// Service Interfaces
// ============================================================================
// Each interface above represents a distinct set of operations.
// TypeSpec will generate routes for all interfaces in the namespace.
// No unified service interface is needed as all operations are already
// properly routed through their respective interfaces.
