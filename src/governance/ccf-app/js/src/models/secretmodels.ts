import { SnpEvidence } from "../attestation/snpattestation";
import { Encrypt } from "./encrypt";
import { Sign } from "./sign";

export interface PutSecretByMemberUserRequest {
  value: string;
}

export interface PutSecretByCleanRoomRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
  sign: Sign;
  data: string;
}

export interface PutSecretByCleanRoomRequestData {
  value: string;
}

export interface PutSecretResponse {
  secretId: string;
}

export interface GetSecretRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
}

export interface ListSecretsResponse {
  value: ListSecretResponse[];
}

export interface ListSecretResponse {
  secretId: string;
}

export interface GetSecretResponse {
  value: string;
}

export interface SetSecretPolicyRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
  sign: Sign;
  data: string;
}

export interface SetSecretPolicyRequestData {
  type: string;
  claims: any;
}
