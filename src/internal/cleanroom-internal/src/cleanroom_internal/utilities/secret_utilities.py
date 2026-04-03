import base64
import json
import logging

import requests
from opentelemetry import trace


def unwrap_secret(
    logger: logging.Logger,
    tracer: trace.Tracer,
    secrets_port: str,
    client_id: str,
    tenant_id: str,
    kid: str,
    akv_endpoint: str,
    kek_kid: str,
    kek_akv_endpoint: str,
    maa_endpoint: str,
) -> bytes:
    """Unwrap a secret via the secrets sidecar.

    Args:
        logger: Logger instance for logging
        tracer: OpenTelemetry tracer for tracing
        secrets_port: Port of the secrets sidecar
        client_id: Azure client ID
        tenant_id: Azure tenant ID
        kid: Key identifier for the secret
        akv_endpoint: Azure Key Vault endpoint
        kek_kid: Key encryption key identifier
        kek_akv_endpoint: Key encryption key Azure Key Vault endpoint
        maa_endpoint: Microsoft Azure Attestation endpoint

    Returns:
        The unwrapped secret as bytes

    Raises:
        Exception: If the secret unwrapping fails
    """
    with tracer.start_as_current_span("unwrap_secret") as span:
        try:
            response = requests.post(
                f"http://localhost:{secrets_port}/secrets/unwrap",
                headers={"Content-Type": "application/json"},
                data=json.dumps(
                    {
                        "clientId": client_id,
                        "tenantId": tenant_id,
                        "kid": kid,
                        "akvEndpoint": akv_endpoint,
                        "kek": {
                            "kid": kek_kid,
                            "akvEndpoint": kek_akv_endpoint,
                            "maaEndpoint": maa_endpoint,
                        },
                    }
                ),
            )
            response.raise_for_status()
        except Exception as e:
            logger.error(
                f"Failed to unwrap secret {kid} via secrets sidecar. Error: {e}"
            )
            span.set_status(
                status=trace.StatusCode.ERROR,
                description=f"Failed to unwrap secret {kid} via secrets sidecar.",
            )
            span.record_exception(e)
            raise e
        else:
            result = response.json()
            encodedSecret = result["value"]
            secret = base64.b64decode(encodedSecret)
            return secret


def get_cgs_secret(
    logger: logging.Logger,
    tracer: trace.Tracer,
    governance_port: str,
    kid: str,
) -> bytes:
    """Get a secret via the governance sidecar.

    Args:
        logger: Logger instance for logging
        tracer: OpenTelemetry tracer for tracing
        governance_port: Port of the governance sidecar
        kid: Key identifier for the secret

    Returns:
        The secret as bytes

    Raises:
        Exception: If the secret retrieval fails
    """
    with tracer.start_as_current_span("get_secret") as span:
        try:
            response = requests.post(
                f"http://localhost:{governance_port}/secrets/{kid}"
            )
            response.raise_for_status()
        except Exception as e:
            logger.error(
                f"Failed to get secret {kid} via governance sidecar. Error: {e}"
            )
            span.set_status(
                status=trace.StatusCode.ERROR,
                description=f"Failed to get secret {kid} via governance sidecar.",
            )
            span.record_exception(e)
            raise e
        else:
            result = response.json()
            encodedSecret = result["value"]
            secret = base64.b64decode(encodedSecret)
            return secret
