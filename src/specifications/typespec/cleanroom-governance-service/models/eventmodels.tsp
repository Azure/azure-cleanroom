import "./snpattestation.tsp";
import "./sign.tsp";

using Http;

namespace Cleanroom.Governance;

/**
 * Put event request
 */
model PutEventRequest {
  /** Event timestamp */
  timestamp: string;

  /** Attestation evidence */
  attestation: SnpEvidence;

  /** Signature */
  sign: Sign;

  /** Event data */
  data: unknown;
}

/**
 * Get events response
 */
model GetEventsResponse {
  /** Next link for pagination */
  nextLink?: string;

  /** Array of events */
  value: Event[];
}

/**
 * Event
 */
model Event {
  /** Event timestamp */
  timestamp: string;

  /** Event timestamp ISO format */
  @encodedName("application/json", "timestamp_iso")
  timestampIso: string;

  /** Event scope */
  scope: string;

  /** Event ID */
  id: string;

  /** Sequence number */
  seqno: int32;

  /** Event data */
  data: unknown;
}
