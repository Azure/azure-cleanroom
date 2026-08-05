import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/userdocuments")
interface UserDocuments {
  /** 
    listUserDocuments
    endpoints/user-documents.js
    GET
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @get
  listUserDocuments(@path contractId: string): ListUserDocumentsResponse;

  /** 
    putUserDocument
    endpoints/user-documents.js
    PUT
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @put
  @route("/{documentId}")
  putUserDocument(
    @path contractId: string,
    @path documentId: string,
    @body request: PutUserDocumentRequest,
  ): EmptyResponse;

  /** 
    getUserDocument
    endpoints/user-documents.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/{documentId}")
  getUserDocument(
    @path contractId: string,
    @path documentId: string,
  ): GetUserDocumentResponse;

  /** 
    getAcceptedUserDocument
    endpoints/user-documents.js
    POST
    AuthPolicies: [attestation]
  */
  @tag("auth:attestation")
  @post
  @route("/accepted/{documentId}")
  getAcceptedUserDocument(
    @path contractId: string,
    @path documentId: string,
    @body request: GetAcceptedUserDocumentRequest,
  ): GetAcceptedUserDocumentResponse;
}
