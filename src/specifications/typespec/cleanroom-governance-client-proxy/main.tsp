import "./api.tsp";

@service(#{ title: "Cleanroom Governance Client Proxy Service" })
@doc("""
  HTTP proxy wrapper around the Cleanroom Governance Client Library, supporting both authentication modes:
  
  **Token-Based Authentication** (member certificates, JWT):
  - Contract management, proposals, voting
  - Member and user management
  - OIDC issuer configuration
  
  **Attestation-Based Authentication** (SNP attestation):
  - Secure secret retrieval with encrypted responses
  - Runtime consent checks
  - Endorsed certificate generation
  - Event auditing
  
  The proxy initializes in either token or attestation mode based on configuration.
  Operations unavailable in the current mode return HTTP 501 Not Implemented.
  """)
namespace Cleanroom.Governance.Client.Proxy;
