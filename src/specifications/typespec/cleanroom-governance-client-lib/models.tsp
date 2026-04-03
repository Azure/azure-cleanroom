import "../cleanroom-governance-service/models/snpattestation.tsp";
import "../cleanroom-governance-service/models/encrypt.tsp";
import "../cleanroom-governance-service/models/sign.tsp";
import "../cleanroom-governance-service/models/errorresponse.tsp";
import "../cleanroom-governance-service/models/contractmodels.tsp";
import "../cleanroom-governance-service/models/memberdocumentmodels.tsp";
import "../cleanroom-governance-service/models/userdocumentmodels.tsp";
import "../cleanroom-governance-service/models/secretmodels.tsp";

using Cleanroom.Governance;

namespace Cleanroom.Governance.Client;

// ============================================================================
// CGS Model Imports and Aliases
// ============================================================================
// The following models are imported from cleanroom-governance-service:
// - SnpEvidence (from snpattestation.tsp)
// - Encrypt (from encrypt.tsp) - aliased as EncryptInfo below
// - Sign (from sign.tsp) - aliased as SignInfo below
// - ErrorResponse, ErrorDetails (from errorresponse.tsp)
// - PutContractRequest, GetContractResponse, MemberVote, ListSecretResponse (from contractmodels.tsp)
// - PutMemberDocumentRequest, GetMemberDocumentResponse (from memberdocumentmodels.tsp)
// - PutUserDocumentRequest, GetUserDocumentResponse (from userdocumentmodels.tsp)
// - PutSecretByMemberUserRequest, ListSecretResponse (from secretmodels.tsp)

/**
 * Encryption information (alias for CGS Encrypt model)
 */
alias EncryptInfo = Encrypt;

/**
 * Digital signature information (alias for CGS Sign model)
 */
alias SignInfo = Sign;

// ============================================================================
// Shared Base Models
// ============================================================================

/**
 * Error reason with code and message
 */
model ErrorReason {
  code: string;
  message: string;
}

/**
 * Member information
 */
model Member {
  /** Member identifier */
  memberId: string;

  /** Member status */
  status?: string;

  /** Additional member data */
  memberData?: unknown;
}

/**
 * Members list response
 */
model MembersResponse {
  /** List of members */
  value: Member[];
}

/**
 * Document data
 */
model DocumentData {
  version: string;
  contractId: string;
  data: unknown;
}

/**
 * Document proposal
 */
model DocumentProposal {
  version: string;
}

/**
 * Secret data
 */
model SecretData {
  value: string;
}

/**
 * User vote
 */
model UserVote {
  approverId: string;
  ballot: string;
}

/**
 * User identity data
 */
model UserIdentityData {
  tenantId: string;
  identifier: string;
}

/**
 * User information
 */
model UserInfo {
  userId: string;
  data: UserData;
}

/**
 * User data
 */
model UserData {
  tenantId?: string;
}

/**
 * User
 */
model User {
  userId: string;
  data?: unknown;
}

/**
 * Event
 */
model Event {
  timestamp: string;
  timestampIso?: string;
  scope: string;
  id: string;
  seqno: int64;
  data: unknown;
}

/**
 * Workspace configuration
 */
model WorkspaceConfigurationModel {
  CcfEndpoint?: string;
  SigningCertId?: string;
  AuthMode?: string;
  ServiceCertDiscovery?: string;
}

// ============================================================================
// Library Models
// ============================================================================
// Models for the governance client library (used by both token and attestation clients)

// ============================================================================
// Attestation Models
// ============================================================================
// Models for attestation-based operations

/**
 * Base attestation request
 */
model AttestationRequest {
  /** SNP attestation evidence */
  attestation: SnpEvidence;
}

/**
 * Attested request with encryption and signature
 */
model AttestedRequest {
  /** SNP attestation evidence */
  attestation: SnpEvidence;

  /** Public key for encrypting response */
  encrypt: EncryptInfo;

  /** Request signature */
  sign: SignInfo;

  /** Encrypted request data */
  data: string;
}

/**
 * Document retrieval request with attestation
 */
model DocumentRetrievalRequest {
  /** SNP attestation evidence */
  attestation: SnpEvidence;

  /** Public key for encrypting response */
  encrypt: EncryptInfo;
}

/**
 * Secret retrieval request with attestation
 */
model SecretRetrievalRequest {
  /** SNP attestation evidence */
  attestation: SnpEvidence;

  /** Public key for encrypting response */
  encrypt: EncryptInfo;
}

/**
 * Token request with attestation
 */
model TokenRequest {
  /** SNP attestation evidence */
  attestation: SnpEvidence;

  /** Public key for encrypting response */
  encrypt: EncryptInfo;
}

/**
 * Attested event request
 */
model AttestedEventRequest {
  /** Event timestamp */
  timestamp: string;

  /** SNP attestation evidence */
  attestation: SnpEvidence;

  /** Request signature */
  sign: SignInfo;

  /** Event data */
  data: unknown;
}

/**
 * Encrypted response wrapper
 */
model EncryptedResponse {
  /** RSA-OAEP-AES-KWP encrypted payload */
  value: string;
}

/**
 * Consent check response
 */
model ConsentCheckResponse {
  /** Consent check result */
  result: "allowed" | "denied" | "pending";

  /** Optional reason for denial */
  reason?: string;
}

// ============================================================================
// Contract Models
// ============================================================================

/**
 * Base proposal response
 */
model ProposalResponse {
  proposalId: string;
  ballotCount?: int32;
  state?: string;
}

/**
 * Vote response
 */
model VoteResponse {
  proposalId: string;
  state?: string;
  ballotCount?: int32;
}

/**
 * Proposal vote input
 */
model ProposalVote {
  proposalId: string;
}

/**
 * Withdraw response
 */
model WithdrawResponse {
  proposalId: string;
  state: string;
}

model ContractResponse {
  id: string;
  version?: string;
  data: unknown;
  state?: string;
  proposalId?: string;
  finalVotes?: unknown[];
}

model ContractProposal {
  version: string;
  data?: unknown;
}

model ContractsListResponse {
  value: ContractResponse[];
}

model CustomBallotVote {
  proposalId: string;
  ballot: unknown;
}

// ============================================================================
// Clean Room Policy Models
// ============================================================================

model CleanRoomPolicyResponse {
  proposalIds: string[];
  policy: unknown;
}

model CleanRoomPolicyProposal {
  type: string;
  claims: unknown;
  contractId?: string;
}

model DelegatePolicyResponse {
  claims: unknown;
}

model DelegatePoliciesListResponse {
  value: DelegatePolicyItem[];
}

model DelegatePolicyItem {
  delegateType: string;
  delegateId: string;
}

// ============================================================================
// Deployment Models
// ============================================================================

model DeploymentProposalResponse {
  proposalIds: string[];
  data: unknown;
}

// ============================================================================
// Event Models
// ============================================================================

model EventsResponse {
  nextLink?: string;
  value: Event[];
}

// ============================================================================
// Member Document Models
// ============================================================================

model MemberDocumentsList {
  value: MemberDocumentItem[];
}

model MemberDocumentItem {
  id: string;
}

// ============================================================================
// Network Models
// ============================================================================

model StateDigestResponse {
  stateDigest: string;
}

model NetworkInfoResponse {
  service: unknown;
}

// ============================================================================
// OAuth Models
// ============================================================================

model TokenSubjectPolicyResponse {
  policy: unknown;
}

// ============================================================================
// OIDC Models
// ============================================================================

model IssuerUrlConfig {
  issuerUrl: string;
}

model IssuerInfoResponse {
  enabled: boolean;
  issuerUrl?: string;
  tenantData?: unknown;
}

// ============================================================================
// Proposal Models
// ============================================================================

model ProposalsListResponse {
  value: ProposalItem[];
}

model ProposalItem {
  proposalId: string;
  proposalState: string;
  seqno?: int64;
}

model ProposalDetailsResponse {
  proposalId: string;
  proposalState: string;
  ballotCount?: int32;
  actions?: unknown[];
}

model VotesListResponse {
  value: Vote[];
}

model Vote {
  memberId: string;
  vote: boolean;
}

model ProposalHistoricalResponse {
  nextLink?: string;
  value: unknown[];
}

model ProposalActionsResponse {
  actions: unknown[];
}

model CreateProposalRequest {
  actions: unknown[];
}

model CustomBallot {
  ballot: unknown;
}

// ============================================================================
// Certificate Authority Models
// ============================================================================

/**
 * Signing key response
 */
model SigningKeyResponse {
  kid?: string;
  publicKey?: string;
}

/**
 * CA information response
 */
model CAInfoResponse {
  enabled: boolean;
  caCert?: string;
  publicKey?: string;
  proposalIds?: string[];
}

model EndorsedCertResponse {
  cert: string;
}

// ============================================================================
// Runtime Models
// ============================================================================

/**
 * Runtime status response
 */
model RuntimeStatusResponse {
  status: string;
  reason?: ErrorReason;
  proposalIds?: string[];
}

/**
 * Runtime option status response
 */
model RuntimeOptionStatusResponse {
  status: string;
  reason?: ErrorReason;
}

/**
 * Access token response
 */
model AccessTokenResponse {
  accessToken: string;
}

// ============================================================================
// Secret Models
// ============================================================================

/**
 * Secret store response
 */
model SecretStoreResponse {
  secretId: string;
}

model SecretsListResponse {
  value: ListSecretResponse[];
}

model SecretPolicyResponse {
  policy: unknown;
}

// ============================================================================
// Update Models
// ============================================================================

model CheckUpdateResponse {
  Proposals: PendingProposal[];
}

model PendingProposal {
  ProposalId: string;
  ActionName: string;
}

// ============================================================================
// User Document Models
// ============================================================================

model UserDocumentResponse {
  id: string;
  contractId: string;
  version: string;
  approvers?: UserProposalApprover[];
  data: unknown;
  labels: Record<string>;
  state?: string;
  proposalId?: string;
  proposerId?: string;
  finalVotes?: UserVote[];
}

model UserDocumentProposal {
  version: string;
}

model UserDocumentsListResponse {
  value: UserDocumentItem[];
}

model UserDocumentItem {
  id: string;
  labels: Record<string>;
}

// ============================================================================
// User Identity Models
// ============================================================================

model UserIdentitiesResponse {
  value: UserIdentity[];
}

model UserIdentity {
  id: string;
  accountType: string;
  invitationId?: string;
  data: UserIdentityData;
}

model UserIdentityResponse {
  id: string;
  accountType: string;
  invitationId?: string;
  data: UserIdentityData;
}

model AddUserIdentityRequest {
  objectId?: string;
  accountType?: string;
  tenantId?: string;
  identifier?: string;
  acceptedInvitationId?: string;
}

// ============================================================================
// User Invitation Models
// ============================================================================

model UserInvitationsResponse {
  value: UserInvitation[];
}

model UserInvitation {
  invitationId: string;
  accountType: string;
  tenantId?: string;
  claims?: unknown;
  status?: string;
  userInfo?: UserInfo;
}

model UserInvitationResponse extends UserInvitation {}

model InvitationProposalInput {
  InvitationId?: string;
  UserName: string;
  TenantId?: string;
  IdentityType: "User" | "ServicePrincipal";
  AccountType: "microsoft";
}

model InvitationProposalResponse {
  proposalId: string;
  invitationId: string;
}

// ============================================================================
// User Proposal Models
// ============================================================================

model UserProposalRequest {
  actions: unknown[];
}

model UserProposalResponse {
  proposalId: string;
  name: string;
  approvers: UserProposalApprover[];
  args: unknown;
}

model ProposalStatusResponse {
  proposalId: string;
  state: string;
}

// ============================================================================
// User Models
// ============================================================================

model UsersListResponse {
  value: User[];
}

model UserResponse extends User {}

/**
 * User active status response
 */
model UserActiveResponse {
  /** Whether user is active */
  isActive: boolean;

  /** User identifier if active */
  userId?: string;
}

// ============================================================================
// Workspace Models
// ============================================================================

model ReadyResponse {
  status: "up";
}

model WorkspaceConfiguration {
  ccfEndpoint: string;
  signingCert?: string;
  signingKey?: string;
  serviceCert?: string;
}
