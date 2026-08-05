import "@typespec/http";
import "../models.tsp";

using Http;

namespace Azure.Cleanroom.Governance;

@route("/contracts/{contractId}/events")
interface Events {
  /** 
    listEvents
    endpoints/events.js
    GET
    AuthPolicies: [member_cert, user_cert, jwt]
  */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  @get
  listEvents(
    @path contractId: string,
    @query id?: string,
    @query scope?: string,
    @query from_seqno?: string,
    @query to_seqno?: string,
    @query max_seqno_per_page?: string,
  ): GetEventsResponse;

  /** 
    putEvent
    endpoints/events.js
    PUT
    AuthPolicies: [jwt, attestation]
  */
  @tag("auth:jwt")
  @tag("auth:attestation")
  @put
  putEvent(
    @path contractId: string,
    @query id?: string,
    @query scope?: string,
    @body request: PutEventRequest,
  ): void;
}
