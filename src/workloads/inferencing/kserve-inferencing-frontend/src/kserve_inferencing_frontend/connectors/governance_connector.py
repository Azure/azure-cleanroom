import logging

import requests

logger = logging.getLogger("kserve-inferencing-frontend")


class GovernanceHttpConnector:
    """Connector for interacting with the governance sidecar."""

    port: int = 8300

    @staticmethod
    def set_port(port: int):
        """Set the port for the governance sidecar."""
        GovernanceHttpConnector.port = port

    @staticmethod
    def sign_policy(payload: str) -> str:
        """
        Sign a policy payload using the governance sidecar.

        Args:
            payload: Base64-encoded policy payload to sign.

        Returns:
            The signature as a string.

        Raises:
            requests.HTTPError: If the request to the governance sidecar fails.
        """
        url = f"http://localhost:{GovernanceHttpConnector.port}/signing/sign"
        headers = {"Content-Type": "application/json"}
        data = {"payload": payload}

        try:
            logger.info(f"Signing policy via governance sidecar at {url}")
            response = requests.post(url, json=data, headers=headers, timeout=30)
            response.raise_for_status()
            result = response.json()
            signature = result.get("value")
            if not signature:
                raise ValueError("No 'value' field in signing response")
            logger.info("Policy signed successfully")
            return signature
        except requests.HTTPError as e:
            logger.error(f"Failed to sign policy: {e}")
            raise
        except Exception as e:
            logger.error(f"Unexpected error signing policy: {e}")
            raise
