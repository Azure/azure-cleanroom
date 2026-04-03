# Cleanroom Governance Client Service

Unified TypeSpec definition for the Cleanroom Governance Client Service supporting both **token-based** and **attestation-based** authentication modes.

## Architecture

This service provides a **single unified API surface** that works in both authentication modes:

```
cleanroom-governance-client-service/    ← Unified service (single OpenAPI spec)
├── models.tsp                          ← Imports from cleanroom-governance-client-lib
├── api.tsp                             ← All routes (token + attestation)
├── main.tsp                            ← Service metadata
└── tspconfig.yaml                      ← Build configuration

cleanroom-governance-client-lib/        ← Shared client models and interfaces
```

## Authentication Modes

The service initializes in one of two modes based on configuration:

### Token Mode
- **Auth**: Member certificates or JWT tokens
- **Use Case**: Governance operations from management plane
- **Operations**: Proposals, voting, contract management, member administration

### Attestation Mode
- **Auth**: SNP attestation reports
- **Use Case**: Runtime operations from confidential workloads
- **Operations**: Encrypted secret retrieval, consent checks, event auditing

## API Behavior

**All routes are exposed in both modes**, but operations return different results based on mode:

- **Mode-specific operations**: Return `501 Not Implemented` when called in wrong mode
- **Shared operations**: Work in both modes (e.g., `GET /members`, status checks)
- **Mode-aware operations**: Same endpoint, different implementation per mode

### Examples

```http
# Works in both modes
GET /members → Returns member list

# Token mode only
POST /proposals/create → Creates proposal (501 in attestation mode)

# Attestation mode only  
POST /secrets/{id} → Returns encrypted secret (501 in token mode)

# Different behavior per mode
GET /show → Returns WorkspaceConfiguration (token) or SidecarWorkspaceConfigurationModel (attestation)
```

## Benefits

✅ **Single OpenAPI spec** - Clients see complete API regardless of deployment  
✅ **Mode-agnostic client code** - No need for separate SDKs  
✅ **Clear error handling** - `501 Not Implemented` for unavailable operations  
✅ **Simpler mental model** - Authentication mode is deployment detail, not API concern  
✅ **Easier testing** - Mock either mode by returning 501 for subset of operations

## Operation Classification

### Token Mode Only
- Contract CRUD, proposals, voting
- Member and user management
- OIDC configuration
- Network information
- Proposal orchestration

### Attestation Mode Only
- Encrypted secret retrieval (`POST /secrets/{id}`)
- Consent checks (`POST /consentcheck/*`)
- Endorsed certificate generation (`POST /ca/generateEndorsedCert`)
- Attested event storage (`PUT /events`)
- Attested policy updates

### Works in Both Modes
- List members (`GET /members`)
- Runtime status checks (`POST /*/checkstatus/*`)
- Workspace readiness (`GET /ready`)
- User active check (`POST /users/isactive`)
- Configuration display (`GET /show`)

## Deployment

### Token Mode Deployment
```yaml
containers:
  - name: cleanroom-governance-client-service
    env:
      - AUTH_MODE=Token
      - MEMBER_CERT_PATH=/certs/member.pem
      - CCF_ENDPOINT=https://ccf:8080
```

### Attestation Mode Deployment
```yaml
containers:
  - name: cleanroom-governance-client-service
    env:
      - AUTH_MODE=Attestation
      - CCF_ENDPOINT=https://ccf:8080
    # Must run in confidential container for SNP attestation
```

## Implementation Pattern

The C# service implementation would look like:

```csharp
public class GovernanceService {
    private readonly IGovernanceClient client;
    private readonly AuthMode mode;
    
    public GovernanceService(AuthMode mode, IConfiguration config) {
        this.mode = mode;
        this.client = mode switch {
            AuthMode.Token => new GovernanceTokenClient(config),
            AuthMode.Attestation => new GovernanceAttestationClient(config),
            _ => throw new ArgumentException("Invalid auth mode")
        };
    }
    
    [HttpPost("proposals/create")]
    public async Task<ProposalResponse> CreateProposal(CreateProposalRequest request) {
        if (mode != AuthMode.Token) {
            return StatusCode(501, "Not available in attestation mode");
        }
        return await client.CreateProposal(request);
    }
    
    [HttpPost("secrets/{secretId}")]
    public async Task<EncryptedResponse> GetSecret(string secretId, SecretRetrievalRequest request) {
        if (mode != AuthMode.Attestation) {
            return StatusCode(501, "Not available in token mode");
        }
        return await client.GetSecretWithAttestation(secretId, request);
    }
    
    [HttpGet("members")]
    public async Task<MembersResponse> ListMembers() {
        // Works in both modes
        return await client.ListMembers();
    }
}
```

## SDK Generation

Generate SDKs for multiple languages from single spec:

```bash
# OpenAPI spec (works for both modes)
tsp compile . --emit @typespec/openapi3

# C# SDK
tsp compile . --emit @typespec/csharp

# Python SDK  
tsp compile . --emit @typespec/python

# TypeScript SDK
tsp compile . --emit @typespec/typescript
```

Clients can call any operation - the service returns `501 Not Implemented` for operations unavailable in current mode.

## Migration

This unified service replaces:
- Previous `governance-client-service/` (token-based API)
- Previous `governance-sidecar-service/` (attestation-based API)

Both authentication modes now use the same codebase, differing only in initialization mode.
