import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * Events operations
 */
interface Events {
  /** listEvents (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  listEvents(contractId: string): GetEventsResponse;
  /** putEvent (AuthPolicies: [jwt, attestation]) */
  @tag("auth:jwt")
  @tag("auth:attestation")
  putEvent(contractId: string, request: PutEventRequest): void;
}
