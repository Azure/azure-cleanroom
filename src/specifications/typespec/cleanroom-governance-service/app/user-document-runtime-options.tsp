import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/userdocuments/{documentId}/runtimeoptions/{option}")
interface UserDocumentRuntimeOptions {
  /** 
    checkUserDocumentRuntimeOption
    endpoints/user-document-runtime-options.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt, attestation]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @tag("auth:attestation")
  @post
  @route("/status")
  checkUserDocumentRuntimeOption(
    @path contractId: string,
    @path documentId: string,
    @path option: string,
  ): GetRuntimeOptionResponse;

  /** 
    enableUserDocumentRuntimeOption
    endpoints/user-document-runtime-options.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/enable")
  enableUserDocumentRuntimeOption(
    @path contractId: string,
    @path documentId: string,
    @path option: string,
  ): EmptyResponse;

  /** 
    disableUserDocumentRuntimeOption
    endpoints/user-document-runtime-options.js
    POST
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @post
  @route("/disable")
  disableUserDocumentRuntimeOption(
    @path contractId: string,
    @path documentId: string,
    @path option: string,
  ): EmptyResponse;
}
