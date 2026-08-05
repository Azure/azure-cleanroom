import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * UserDocuments operations
 */
interface UserDocuments {
  /** listUserDocuments (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  listUserDocuments(contractId: string): ListUserDocumentsResponse;
  /** putUserDocument (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  putUserDocument(contractId: string, documentId: string, request: PutUserDocumentRequest): EmptyResponse;
  /** getUserDocument (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getUserDocument(contractId: string, documentId: string): GetUserDocumentResponse;
  /** getAcceptedUserDocument (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  getAcceptedUserDocument(contractId: string, documentId: string, request: GetAcceptedUserDocumentRequest): GetAcceptedUserDocumentResponse;
}
