import { SnpEvidence } from "../attestation/snpattestation";
import { Encrypt } from "./encrypt";

export interface PutSecretRequest {
  value: string;
}

export interface PutSecretResponse {
  secretId: string;
}

export interface GetSecretRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
}

export interface GetSecretResponse {
  value: string;
}
