"""Constraint enforcer interface for node validation."""

from abc import ABC, abstractmethod
from typing import TYPE_CHECKING, List

from kubernetes.client import V1Node

if TYPE_CHECKING:
    from ...clients.kubernetes_client import KubernetesClient


class ConstraintEnforcer(ABC):

    @property
    @abstractmethod
    def name(self) -> str:
        pass

    @abstractmethod
    def rank_nodes(
        self, nodes: List[V1Node], k8s_client: "KubernetesClient"
    ) -> List[V1Node]:
        """
        Filter and rank nodes based on the constraint.

        Args:
            nodes: List of V1Node objects to evaluate.
            k8s_client: KubernetesClient for interacting with the cluster.

        Returns:
            Filtered and ranked list of nodes, ordered by preference (best first).
            Returns empty list if no nodes satisfy the constraint.
        """
        pass
