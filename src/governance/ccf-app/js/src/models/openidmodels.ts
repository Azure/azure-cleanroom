export interface DiscoveryResponse {
  response_types_supported: string[];
  id_token_signing_alg_values_supported: string[];
  issuer: string;
  jwks_uri: string;
  claims_supported?: string[];
}

export interface JwksResponse {
  keys: Jwk[];
}

export interface Jwk {
  alg: string;
  use: string;
  e: string;
  n: string;
  kid: string;
  kty: string;
  x5c?: string[];
  x5t?: string;
}

export interface OidcIssuerInfo {
  enabled: boolean;
  issuerUrl?: string;
  tenantData?: {
    tenantId: string;
    issuerUrl: string;
  };
}
