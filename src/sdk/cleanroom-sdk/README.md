# Azure Cleanroom SDK

A type-safe .NET client library for interacting with the Azure Cleanroom
Governance Service (CGS) and the CCF governance layer. The SDK handles
attestation injection, COSE signing, mTLS, and request preparation.

## Features

- 🔒 **Attestation-at-Init** — TEE report generation happens once at
  build time; all subsequent calls auto-inject it.
- ✨ **Automatic COSE Signing** — `gov/*` proposals and ballots signed
  transparently via member credentials.
- 🎯 **Clean Interface** — `ICleanroomClient` provides a single
  facade covering CGS app endpoints and CCF gov endpoints.
- 📦 **Fluent Builder** — `CleanroomClientBuilder` with auth modes
  for SNP attestation, file-based attestation, JWT, and member certs.

## Quick Start

### JWT / Azure Login

```csharp
using Azure.Cleanroom.Sdk;

ICleanroomClient client = new CleanroomClientBuilder()
    .WithBaseAddress("https://governance.example.com:8300")
    .WithServiceCertificate(serviceCertPem)
    .WithJwtCredentials(tokenCredential, scope)
    .Build();

var contracts = await client.ListContractsAsync();
```

### SNP Attestation (TEE)

```csharp
ICleanroomClient client = new CleanroomClientBuilder()
    .WithBaseAddress("https://governance.example.com:8300")
    .WithServiceCertificate(serviceCertPem)
    .WithSnpAttestation()
    .Build();

// Attestation auto-injected, response auto-decrypted.
var secret = await client.GetSecretAsync("contract-1", "my-secret");
```

### Member Governance (COSE Signing)

```csharp
ICleanroomClient client = new CleanroomClientBuilder()
    .WithBaseAddress("https://governance.example.com:8300")
    .WithServiceCertificate(serviceCertPem)
    .WithMemberCredentials(memberCertPem, memberKeyPem)
    .Build();

var result = await client.CreateProposalAsync(proposal);
await client.VoteAcceptProposalAsync(result["proposalId"]!.ToString());
```

## Architecture

```
ICleanroomClient          ← caller-facing facade (~50 methods)
  ↑ implements
CleanroomClient           ← partial class (4 files)
  ├── uses ──→ Azure.Cleanroom.Governance.Client  (TypeSpec-generated CGS client)
  └── uses ──→ Microsoft.Ccf.Client               (TypeSpec-generated CCF client)
```

| Partial File | Responsibility |
|---|---|
| `CleanroomClient.cs` | Constructor, fields, auth guards, Dispose |
| `CleanroomClient.Governance.Cgs.cs` | CGS `app/*` endpoint implementations |
| `CleanroomClient.Governance.Ccf.cs` | CCF `gov/*` COSE-signed proposals and ballots |
| `CleanroomClient.Facade.cs` | Attestation-simplified facade methods |

## License

MIT License — Copyright (c) Microsoft Corporation
