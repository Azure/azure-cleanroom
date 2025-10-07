import { SnpEvidence } from "../attestation/snpattestation";
import { Encrypt } from "./encrypt";
import { Sign } from "./sign";

export interface GetTokenRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
}

export interface GetTokenResponse {
  value: string;
}

export interface SetSubjectPolicyRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
  sign: Sign;
  data: string;
}

export interface SetSubjectPolicyRequestData {
  type: string;
  claims: any;
}
