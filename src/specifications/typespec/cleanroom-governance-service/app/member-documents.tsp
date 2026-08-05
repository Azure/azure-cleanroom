import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/memberdocuments")
interface MemberDocuments {
  /** 
    listMemberDocuments
    endpoints/member-documents.js
    GET
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @get
  listMemberDocuments(@path contractId: string): ListMemberDocumentResponse[];

  /** 
    putMemberDocument
    endpoints/member-documents.js
    PUT
    AuthPolicies: [member_cert]
  */
  @tag("auth:member_cert")
  @put
  @route("/{documentId}")
  putMemberDocument(
    @path contractId: string,
    @path documentId: string,
    @body request: PutMemberDocumentRequest,
  ): EmptyResponse;

  /** 
    getMemberDocument
    endpoints/member-documents.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/{documentId}")
  getMemberDocument(
    @path contractId: string,
    @path documentId: string,
  ): GetMemberDocumentResponse;

  /** 
    getAcceptedMemberDocument
    endpoints/member-documents.js
    POST
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @post
  @route("/accepted/{documentId}")
  getAcceptedMemberDocument(
    @path contractId: string,
    @path documentId: string,
    @body request: GetAcceptedMemberDocumentRequest,
  ): GetAcceptedMemberDocumentResponse;
}
