import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * UserDocumentRuntimeOptions operations
 */
interface UserDocumentRuntimeOptions {
  /** checkUserDocumentRuntimeOption (AuthPolicies: [member_cert, user_cert, jwt, attestation]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @tag("auth:attestation")
  checkUserDocumentRuntimeOption(contractId: string, documentId: string, option: string): GetRuntimeOptionResponse;
  /** enableUserDocumentRuntimeOption (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  enableUserDocumentRuntimeOption(contractId: string, documentId: string, option: string): EmptyResponse;
  /** disableUserDocumentRuntimeOption (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  disableUserDocumentRuntimeOption(contractId: string, documentId: string, option: string): EmptyResponse;
}
