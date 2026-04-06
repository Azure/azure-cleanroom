import "./models.tsp";
import "./interfaces.tsp";

/**
 * Governance Client Library
 *
 * This library provides models and interfaces for interacting with the Cleanroom Governance Service.
 * It does not include HTTP routing details - those are defined in governance-client-service.
 *
 * This library can be used to:
 * - Generate C# client interfaces and models for direct .NET consumption
 * - Generate Python/TypeScript models
 * - Serve as the foundation for governance-client-service HTTP API
 *
 * Implementations:
 * - Token-based client: Uses Azure AD or other OAuth tokens for authentication
 * - Attestation-based client: Uses SNP attestation reports for secure enclave access
 */
@service(#{ title: "Cleanroom Governance Client Library" })
namespace Cleanroom.Governance.Client;
