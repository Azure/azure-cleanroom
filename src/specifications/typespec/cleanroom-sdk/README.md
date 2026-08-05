# Cleanroom Governance Client Library

This TypeSpec library provides **models and interfaces** for interacting with the Cleanroom Governance Service (CGS). It supports both **token-based** and **attestation-based** authentication patterns through a unified interface.

## Architecture

```
cleanroom-governance-client-lib/    ← Unified interfaces & models (all-in-one)
├── models.tsp                      ← CGS imports + shared base + business + attestation
├── interfaces.tsp                  ← IGovernanceClient (supports both auth types)
├── main.tsp                        ← Entry point
└── tspconfig.yaml

cleanroom-governance-client-service/  ← Unified HTTP API (both auth modes)
├── models.tsp                        ← Imports from lib
├── api.tsp                           ← All HTTP routes (token + attestation)
└── main.tsp                          ← Service metadata
```

## Purpose

### What This Library Provides

1. **Unified Interface**: Single `IGovernanceClient` interface for both authentication patterns
2. **Type Definitions**: All models for requests, responses, and domain objects
3. **Attestation Support**: Models for SNP attestation, encryption, and consent checks
4. **Cross-Language Support**: Can generate C#, Python, TypeScript SDKs

### Authentication Patterns Supported

#### Token-Based Authentication
- Uses: JWT tokens or CCF member certificates
- Operations: Full CRUD, proposals, voting, orchestration
- Request: Standard HTTP with Authorization header
- Response: Plain JSON

#### Attestation-Based Authentication
- Uses: SNP attestation reports
- Operations: Secure operations (secrets, certs, consent checks)
- Request: Wrapped with attestation evidence + optional signature
- Response: Encrypted with caller's public key

## Interface Design

Each interface in the library supports **both** authentication patterns where applicable:

```typespec
interface ISecrets {
  // Token-based operations
  listSecrets(contractId: string): SecretsListResponse;
  storeSecret(contractId: string, secretName: string, secret: SecretData): SecretStoreResponse;
  
  // Attestation-based operations (encrypted responses)
  getSecretWithAttestation(secretId: string): EncryptedResponse;
  storeSecretWithAttestation(secretName: string, request: AttestedRequest): SecretStoreResponse;
}
```

### Operations by Authentication Type

| Operation Type | Token Client | Attestation Client |
|----------------|--------------|-------------------|
| Read operations (list, get) | ✅ Standard | ✅ With encryption |
| Write operations (create, update) | ✅ With proposals | ✅ With attestation |
| Consent checks | ❌ Not applicable | ✅ Required |
| Proposal/voting | ✅ Full support | ❌ Not applicable |

## Usage

### For C# SDK Generation

Generate C# interfaces and models:

```bash
cd governance-client-lib
tsp compile . --emit @typespec/csharp
```

This generates:
- `IGovernanceClient` interface
- `IContracts`, `ISecrets`, etc. sub-interfaces
- All model classes: `ContractResponse`, `AttestedRequest`, `EncryptedResponse`, etc.

### C# Implementation Pattern

```csharp
// Generated from library
public interface ISecrets
{
    Task<SecretsListResponse> ListSecrets(string contractId);
    Task<EncryptedResponse> GetSecretWithAttestation(string secretId);
}

// Token-based implementation
public class GovernanceTokenClient : IGovernanceClient
{
    public async Task<EncryptedResponse> GetSecretWithAttestation(string secretId)
    {
        throw new NotSupportedException("Token client doesn't support attestation operations");
    }
    
    public async Task<SecretsListResponse> ListSecrets(string contractId)
    {
        return await httpClient.GetAsync($"/contracts/{contractId}/secrets");
    }
}

// Attestation-based implementation  
public class GovernanceAttestationClient : IGovernanceClient
{
    public async Task<EncryptedResponse> GetSecretWithAttestation(string secretId)
    {
        var request = new DocumentRetrievalRequest {
            Attestation = await attestationProvider.GetEvidence(),
            Encrypt = encryptionProvider.GetPublicKey()
        };
        
        var encrypted = await httpClient.PostAsync($"/secrets/{secretId}", request);
        return encrypted; // Client decrypts locally
    }
    
    public async Task<SecretsListResponse> ListSecrets(string contractId)
    {
        throw new NotSupportedException("Attestation client doesn't support list operations");
    }
}
```

## Interface Structure

### Main Interface

**`IGovernanceClient`** - Aggregates all sub-interfaces for token-based clients

### Sub-Interfaces

- `IContracts` - Contract CRUD and voting
- `ICertificateAuthority` - Certificate operations
- `ICleanRoomPolicy` - Policy management
- `IContractProposals` - Deployment proposals
- `IContractRuntimeOptions` - Runtime configuration
- `IEvents` - Event queries
- `IMemberDocuments` - Member document operations
- `IMembers` - Member management
- `INetwork` - Network information
- `INode` - Node health checks
- `IOAuth` - OAuth token operations
- `IOidc` - OIDC configuration
- `IProposals` - Generic proposal operations
- `IRuntimeOptions` - Runtime option proposals
- `ISecrets` - Secret management
- `IUpdates` - Update checking
- `IUserDocuments` - User document operations
- `IUserIdentities` - User identity management
- `IUserInvitations` - User invitation workflows
- `IUserProposals` - User-specific proposals
- `IUsers` - User operations
- `IWorkspace` - Workspace configuration

## Model Categories

### Imported from CGS

Via `governance-client-common`:
- `SnpEvidence`, `EncryptInfo`, `SignInfo` (attestation)
- `PutContractRequest`, `GetMemberDocumentResponse` (core CGS models)
- `ErrorResponse`, `ErrorDetails` (errors)
- `Member`, `MembersResponse` (member models)
- `ProposalResponse`, `VoteResponse` (proposal models)

### Library-Specific Models

Defined in this library:
- Contract models: `ContractResponse`, `ContractProposal`, `ContractsListResponse`
- Policy models: `CleanRoomPolicyResponse`, `DelegatePolicyResponse`
- Proposal models: `ProposalsListResponse`, `ProposalDetailsResponse`
- User models: `UserDocumentResponse`, `UserIdentity`, `UserInvitation`
- Configuration models: `WorkspaceConfiguration`, `ReadyResponse`

## Dependencies

```
governance-client-lib
  └── governance-client-common
       └── cleanroom-governance-service/models/*
```

## Generated Artifacts

### C# Package (Planned)

```
NuGet: Microsoft.Azure.Cleanroom.Governance.Client
├── Cleanroom.Governance.Client.IGovernanceClient
├── Cleanroom.Governance.Client.IContracts
├── Cleanroom.Governance.Client.Models.ContractResponse
└── ... (all interfaces and models)
```

### Python Package (Planned)

```
PyPI: azure-cleanroom-governance-client
├── azure.cleanroom.governance.client.models
└── azure.cleanroom.governance.client.operations
```

## Migration Path

### Before (Separate Services)

```
governance-client-service/      ← Token-based HTTP service
├── models.tsp
└── api.tsp

governance-sidecar-service/     ← Attestation-based HTTP service
├── models.tsp
└── api.tsp
```

### After (Unified Architecture)

```
cleanroom-governance-client-lib/    ← Shared library
├── models.tsp (pure business models)
└── interfaces.tsp (pure operations, no @route)

cleanroom-governance-client-service/  ← Unified HTTP service
├── models.tsp (imports lib, minimal extensions)
└── api.tsp (all routes, both modes)
```

## Benefits

1. **Single API Surface**: All operations in one OpenAPI spec
2. **Cleaner SDK Generation**: Library without routes generates better client SDKs
3. **Code Reuse**: Single C# service codebase for both auth modes
4. **Mode Flexibility**: Same endpoints, different behavior per deployment mode
5. **Simpler Deployment**: One container image, mode controlled by env variable
6. **Better Testing**: Mock mode by returning 501 for subset of operations

## Related Files

- `/src/specifications/typespec/governance-service/` - Unified HTTP service
- `/src/specifications/typespec/cleanroom-governance-service/` - Backend CGS service

## Next Steps

1. ✅ Create library with interfaces and models (no routes)
2. ✅ Create unified service with all routes
3. ✅ Remove duplicate client/sidecar services
4. ⏳ Generate C# SDK from library
4. ⏳ Implement `GovernanceClientBase` abstract class
5. ⏳ Implement `GovernanceTokenClient` and `GovernanceAttestationClient`
6. ⏳ Update HTTP service to consume library implementation
7. ⏳ Package library as NuGet
