namespace Cleanroom.Governance;

/**
 * Discovery response
 */
model DiscoveryResponse {
  /** Supported response types */
  response_types_supported: string[];

  /** Supported ID token signing algorithms */
  id_token_signing_alg_values_supported: string[];

  /** Issuer URL */
  issuer: string;

  /** JWKS URI */
  jwks_uri: string;

  /** Supported claims */
  claims_supported?: string[];
}

/**
 * JWKS response
 */
model JwksResponse {
  /** Array of JWK keys */
  keys: Jwk[];
}

/**
 * JSON Web Key
 */
model Jwk {
  /** Algorithm */
  alg: string;

  /** Usage */
  use: string;

  /** Exponent */
  e: string;

  /** Modulus */
  n: string;

  /** Key ID */
  kid: string;

  /** Key type */
  kty: string;

  /** X.509 certificate chain */
  x5c?: string[];

  /** X.509 certificate SHA-1 thumbprint */
  x5t?: string;
}

/**
 * OIDC issuer info
 */
model OidcIssuerInfo {
  /** Issuer enabled */
  enabled: boolean;

  /** Issuer URL */
  issuerUrl?: string;

  /** Tenant data */
  tenantData?: TenantData;
}

/**
 * Tenant data
 */
model TenantData {
  /** Tenant ID */
  tenantId: string;

  /** Issuer URL */
  issuerUrl: string;
}
