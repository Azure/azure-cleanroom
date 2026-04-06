import logging
from typing import TYPE_CHECKING, Optional

if TYPE_CHECKING:
    from ...clients.kubernetes_client import KubernetesClient

logger = logging.getLogger("policy_injector")


class PolicyInjector:
    def __init__(self, k8s_client: "KubernetesClient"):
        self.k8s_client = k8s_client

    def get_policy_from_config_map(
        self, config_map_name: str, namespace: str, policy_key: str
    ) -> Optional[str]:
        return self.k8s_client.get_key_from_config_map(
            config_map_name, namespace, policy_key
        )
