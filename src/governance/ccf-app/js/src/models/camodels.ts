import { SnpEvidence } from "../attestation/snpattestation";
import { Encrypt } from "./encrypt";
import { Sign } from "./sign";

export interface CAConfigItem {
  enabled: string;
  requestId: string;
}

export interface CAKeyItem {
  requestId: string;
  publicKey: string;
  privateKey: string;
  caCert: string;
}

export interface CAInfo {
  enabled: boolean;
  caCert?: string;
  publicKey?: string;
  proposalIds: string[];
}

export interface GenerateEndorsedCertRequest {
  attestation: SnpEvidence;
  encrypt: Encrypt;
  sign: Sign;
  data: string;
}

export interface GenerateEndorsedCertRequestData {
  publicKey: string;
  subjectName: string;
  subjectAlternateNames?: string[];
  validityPeriodDays: number;
}

export interface isCAEnabledRequest {
  attestation: SnpEvidence;
}

export interface isCAEnabledResponse {
  enabled: boolean;
}
