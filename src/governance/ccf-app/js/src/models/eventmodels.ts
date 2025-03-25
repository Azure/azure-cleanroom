import { SnpEvidence } from "../attestation/snpattestation";
import { Sign } from "./sign";

export interface PutEventRequest {
  timestamp: string;
  attestation: SnpEvidence;
  sign: Sign;
  data: any;
}

export interface GetEventsResponse {
  nextLink?: string;
  value: Event[];
}

export interface Event {
  timestamp: string;
  timestamp_iso: string;
  scope: string;
  id: string;
  seqno: number;
  data: any;
}
