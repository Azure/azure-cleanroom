import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * MemberDocuments operations
 */
interface MemberDocuments {
  /** listMemberDocuments (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  listMemberDocuments(contractId: string): ListMemberDocumentResponse[];
  /** putMemberDocument (AuthPolicies: [member_cert]) */
  @tag("auth:member_cert")
  putMemberDocument(contractId: string, documentId: string, request: PutMemberDocumentRequest): EmptyResponse;
  /** getMemberDocument (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  getMemberDocument(contractId: string, documentId: string): GetMemberDocumentResponse;
  /** getAcceptedMemberDocument (AuthPolicies: [attestation]) */
  @tag("auth:attestation")
  getAcceptedMemberDocument(contractId: string, documentId: string, request: GetAcceptedMemberDocumentRequest): GetAcceptedMemberDocumentResponse;
}
