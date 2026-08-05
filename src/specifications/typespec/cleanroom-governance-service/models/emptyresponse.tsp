import "@typespec/http";

using Http;

namespace Azure.Cleanroom.Governance;

/**
 * Empty 200 OK response with no body.
 *
 * Use this for endpoints that return HTTP 200 but have no response
 * content. TypeSpec treats `void` as 204 No Content, so this model
 * explicitly declares a 200 status code.
 */
model EmptyResponse {
  @statusCode _: 200;
}
