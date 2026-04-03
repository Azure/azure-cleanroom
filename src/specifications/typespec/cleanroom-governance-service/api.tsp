import "@typespec/http";
import "./models.tsp";

using Http;
using Cleanroom.Governance;

namespace Cleanroom.Governance;

// ============================================================================
// Contract APIs
// ============================================================================

@route("/contracts")
interface Contracts {
  /** List all contracts */
  @get
  list(): ListContractsResponse;

  /** Create or update a contract */
  @put
  @route("/{contractId}")
  put(@path contractId: string, @body contract: PutContractRequest): void;

  /** Get a contract by ID */
  @get
  @route("/{contractId}")
  get(@path contractId: string): GetContractResponse;
}

// ============================================================================
// Contract Runtime Options APIs
// ============================================================================

@route("/contracts/{contractId}")
interface ContractRuntimeOptions {
  /** Check contract execution status */
  @post
  @route("/checkstatus/execution")
  checkExecutionStatus(@path contractId: string): {
    status: string;
  };

  /** Check contract logging status */
  @post
  @route("/checkstatus/logging")
  checkLoggingStatus(@path contractId: string): {
    status: string;
  };

  /** Check contract telemetry status */
  @post
  @route("/checkstatus/telemetry")
  checkTelemetryStatus(@path contractId: string): {
    status: string;
  };

  /** Consent check for execution */
  @post
  @route("/consentcheck/execution")
  consentCheckExecution(@path contractId: string): {
    status: string;
  };

  /** Consent check for logging */
  @post
  @route("/consentcheck/logging")
  consentCheckLogging(@path contractId: string): {
    status: string;
  };

  /** Consent check for telemetry */
  @post
  @route("/consentcheck/telemetry")
  consentCheckTelemetry(@path contractId: string): {
    status: string;
  };

  /** Enable a contract */
  @post
  @route("/enable")
  enable(@path contractId: string): void;

  /** Disable a contract */
  @post
  @route("/disable")
  disable(@path contractId: string): void;
}

// ============================================================================
// Event APIs
// ============================================================================

@route("/contracts/{contractId}/events")
interface Events {
  /** Get events for a contract */
  @get
  get(@path contractId: string, @query nextLink?: string): GetEventsResponse;

  /** Put an event */
  @put
  put(@path contractId: string, @body event: PutEventRequest): void;
}

// ============================================================================
// Secret APIs
// ============================================================================

@route("/contracts/{contractId}/secrets")
interface Secrets {
  /** List all secrets for a contract */
  @get
  list(@path contractId: string): ListSecretsResponse;

  /** Create or update a secret */
  @put
  @route("/{secretName}")
  put(
    @path contractId: string,
    @path secretName: string,
    @body request: PutSecretByMemberUserRequest | PutSecretByCleanRoomRequest,
  ): PutSecretResponse;

  /** Get a secret */
  @post
  @route("/{secretName}")
  get(
    @path contractId: string,
    @path secretName: string,
    @body request: GetSecretRequest,
  ): GetSecretResponse;
}

// ============================================================================
// Clean Room Policy APIs
// ============================================================================

@route("/contracts/{contractId}/cleanroompolicy")
interface CleanRoomPolicy {
  /** Get clean room policy for a contract */
  @get
  get(@path contractId: string): {
    policy: unknown;
  };

  /** List delegate policies */
  @get
  @route("/delegates")
  listDelegates(@path contractId: string): {
    delegates: unknown[];
  };

  /** Get delegate clean room policy */
  @get
  @route("/delegates/{delegateType}/{delegateId}")
  getDelegate(
    @path contractId: string,
    @path delegateType: string,
    @path delegateId: string,
  ): {
    policy: unknown;
  };

  /** Set delegate clean room policy */
  @post
  @route("/delegates/{delegateType}/{delegateId}")
  setDelegate(
    @path contractId: string,
    @path delegateType: string,
    @path delegateId: string,
    @body policy: unknown,
  ): void;
}

// ============================================================================
// Deployment APIs
// ============================================================================

@route("/contracts/{contractId}")
interface Deployment {
  /** Get deployment specification */
  @get
  @route("/deploymentspec")
  getSpec(@path contractId: string): {
    spec: unknown;
  };

  /** Get deployment information */
  @get
  @route("/deploymentinfo")
  getInfo(@path contractId: string): {
    info: unknown;
  };
}

// ============================================================================
// OAuth Token APIs
// ============================================================================

@route("/contracts/{contractId}/oauth")
interface OAuth {
  /** Get OAuth token */
  @post
  @route("/token")
  getToken(@path contractId: string, @body request: unknown): {
    token: string;
  };
}

// ============================================================================
// Certificate Authority APIs
// ============================================================================

@route("/contracts/{contractId}/ca")
interface CertificateAuthority {
  /** Generate CA signing key */
  @post
  @route("/generateSigningKey")
  generateSigningKey(@path contractId: string): void;

  /** Generate endorsed certificate */
  @post
  @route("/generateEndorsedCert")
  generateEndorsedCert(
    @path contractId: string,
    @body request: GenerateEndorsedCertRequest,
  ): {
    certificate: string;
  };

  /** Get CA information */
  @get
  @route("/info")
  getInfo(@path contractId: string): CAInfo;
}

// ============================================================================
// Member Document APIs
// ============================================================================

@route("/memberdocuments")
interface MemberDocuments {
  /** List all member documents */
  @get
  list(): {
    value: ListMemberDocumentResponse[];
  };

  /** Create or update a member document */
  @put
  @route("/{documentId}")
  put(@path documentId: string, @body document: PutMemberDocumentRequest): void;

  /** Get a member document */
  @get
  @route("/{documentId}")
  get(@path documentId: string): GetMemberDocumentResponse;
}

@route("/contracts/{contractId}/acceptedMemberDocuments/{documentId}")
interface AcceptedMemberDocuments {
  /** Get accepted member document */
  @post
  get(
    @path contractId: string,
    @path documentId: string,
    @body request: GetAcceptedMemberDocumentRequest,
  ): GetAcceptedMemberDocumentResponse;
}

// ============================================================================
// User Identity APIs
// ============================================================================

@route("/users/identities")
interface UserIdentities {
  /** List all user identities */
  @get
  list(): {
    value: unknown[];
  };

  /** Get a user identity */
  @get
  @route("/{identityId}")
  get(@path identityId: string): {
    identity: unknown;
  };
}

@route("/contracts/{contractId}/users")
interface ContractUsers {
  /** Check if user is active */
  @post
  @route("/isactive")
  isActive(@path contractId: string): {
    active: boolean;
  };
}

// ============================================================================
// User Invitation APIs
// ============================================================================

@route("/users/invitations")
interface UserInvitations {
  /** List all user invitations */
  @get
  list(): {
    value: unknown[];
  };

  /** Get a user invitation */
  @get
  @route("/{invitationId}")
  get(@path invitationId: string): {
    invitation: unknown;
  };

  /** Accept a user invitation */
  @post
  @route("/{invitationId}/accept")
  accept(@path invitationId: string): void;
}

// ============================================================================
// User Document APIs
// ============================================================================

@route("/userdocuments")
interface UserDocuments {
  /** List all user documents */
  @get
  list(@query labels?: Record<string>): ListUserDocumentsResponse;

  /** Create or update a user document */
  @put
  @route("/{documentId}")
  put(@path documentId: string, @body document: PutUserDocumentRequest): void;

  /** Get a user document */
  @get
  @route("/{documentId}")
  get(@path documentId: string): GetUserDocumentResponse;
}

@route("/contracts/{contractId}/acceptedUserDocuments/{documentId}")
interface AcceptedUserDocuments {
  /** Get accepted user document */
  @post
  get(
    @path contractId: string,
    @path documentId: string,
    @body request: GetAcceptedUserDocumentRequest,
  ): GetAcceptedUserDocumentResponse;
}

// ============================================================================
// User Document Runtime Options APIs
// ============================================================================

@route("/contracts/{contractId}/userdocuments/{documentId}")
interface UserDocumentRuntimeOptionsConsent {
  /** Consent check for user document runtime option */
  @post
  @route("/consentcheck/{option}")
  consentCheck(
    @path contractId: string,
    @path documentId: string,
    @path option: string,
  ): {
    status: string;
  };
}

@route("/userdocuments/{documentId}")
interface UserDocumentRuntimeOptions {
  /** Check user document runtime option status */
  @post
  @route("/checkstatus/{option}")
  checkStatus(@path documentId: string, @path option: string): {
    status: string;
  };

  /** Enable user document runtime option */
  @post
  @route("/runtimeoptions/{option}/enable")
  enable(@path documentId: string, @path option: string): void;

  /** Disable user document runtime option */
  @post
  @route("/runtimeoptions/{option}/disable")
  disable(@path documentId: string, @path option: string): void;
}

// ============================================================================
// Proposal APIs
// ============================================================================

@route("/proposals/{proposalId}")
interface Proposals {
  /** Get proposal historical data */
  @get
  @route("/historical")
  getHistorical(@path proposalId: string): {
    proposal: unknown;
  };
}

// ============================================================================
// User Proposal APIs
// ============================================================================

@route("/users/proposals/{proposalId}")
interface UserProposals {
  /** Get a user proposal */
  @get
  get(@path proposalId: string): GetUserProposalResponse;

  /** Create or update a user proposal */
  @put
  put(
    @path proposalId: string,
    @body proposal: CreateUserProposalRequest,
  ): CreateUserProposalResponse;

  /** Get user proposal status */
  @get
  @route("/status")
  getStatus(@path proposalId: string): {
    status: string;
    proposal: UserProposal;
  };

  /** Withdraw a user proposal */
  @post
  @route("/withdraw")
  withdraw(@path proposalId: string): void;

  /** Submit ballot for user proposal */
  @post
  @route("/ballots/submit")
  submitBallot(
    @path proposalId: string,
    @body ballot: SubmitUserProposalBallotRequest,
  ): void;
}

// ============================================================================
// OIDC APIs
// ============================================================================

@route("/oidc")
interface Oidc {
  /** Get OIDC configuration */
  @get
  @route("/.well-known/openid-configuration")
  getConfiguration(): {
    configuration: unknown;
  };

  /** Get JWKS (JSON Web Key Set) */
  @get
  @route("/keys")
  getJwks(): {
    keys: unknown[];
  };

  /** Generate OIDC signing key */
  @post
  @route("/generateSigningKey")
  generateSigningKey(): void;

  /** Set OIDC issuer URL */
  @post
  @route("/setIssuerUrl")
  setIssuerUrl(
    @body request: {
      issuerUrl: string;
    },
  ): void;

  /** Get OIDC issuer information */
  @get
  @route("/issuerInfo")
  getIssuerInfo(): {
    issuerUrl: string;
    proposalIds: string[];
  };
}

// ============================================================================
// Runtime Options APIs
// ============================================================================

@route("/runtimeoptions")
interface RuntimeOptions {
  /** Check runtime option status */
  @post
  @route("/checkstatus/{option}")
  checkStatus(@path option: string): {
    status: string;
  };
}
