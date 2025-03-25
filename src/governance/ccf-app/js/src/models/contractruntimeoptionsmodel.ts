import { SnpEvidence } from "../attestation/snpattestation";

export interface GetRuntimeOptionResponse {
  status: string;
  reason?: {
    code: string;
    message: string;
  };
  proposalIds?: string[];
}

export interface ConsentCheckRequest {
  attestation: SnpEvidence;
}
