import "./models.tsp";

import "./interfaces/certificate-authority.tsp";
import "./interfaces/clean-room-policy.tsp";
import "./interfaces/contract-runtime-options.tsp";
import "./interfaces/contracts.tsp";
import "./interfaces/deployment-info.tsp";
import "./interfaces/deployment-spec.tsp";
import "./interfaces/events.tsp";
import "./interfaces/member-documents.tsp";
import "./interfaces/oidc.tsp";
import "./interfaces/proposals.tsp";
import "./interfaces/runtime-options.tsp";
import "./interfaces/secrets.tsp";
import "./interfaces/signing.tsp";
import "./interfaces/token.tsp";
import "./interfaces/user-document-runtime-options.tsp";
import "./interfaces/user-documents.tsp";
import "./interfaces/user-identities.tsp";
import "./interfaces/user-invitations.tsp";
import "./interfaces/user-proposals.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * Complete Governance Client interface
 * Aggregates all governance operations across all authentication modes
 */
interface GovernanceClient
  extends CertificateAuthority,
    CleanRoomPolicy,
    ContractRuntimeOptions,
    Contracts,
    DeploymentInfo,
    DeploymentSpec,
    Events,
    MemberDocuments,
    Oidc,
    Proposals,
    RuntimeOptions,
    Secrets,
    Signing,
    Token,
    UserDocumentRuntimeOptions,
    UserDocuments,
    UserIdentities,
    UserInvitations,
    UserProposals {
}
