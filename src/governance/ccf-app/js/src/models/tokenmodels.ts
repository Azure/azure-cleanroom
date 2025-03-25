import { SnpEvidence } from "../attestation/snpattestation";
import { Encrypt } from "./encrypt";

export interface GetTokenRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
}

export interface GetTokenResponse {
  value: string;
}
