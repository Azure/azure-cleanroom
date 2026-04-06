"""Pod count constraint enforcer implementation."""

import logging
from typing import TYPE_CHECKING, List, Tuple

from kubernetes.client import V1Node

from .constraint_enforcer import ConstraintEnforcer

if TYPE_CHECKING:
    from ...clients.kubernetes_client import KubernetesClient

logger = logging.getLogger("pod_count_constraint")


class PodCountConstraint(ConstraintEnforcer):
    """
    Enforces a maximum pod count constraint on nodes and ranks them by available capacity.
    """

    def __init__(self, max_pods: int):
        """
        Initialize the pod count constraint.

        Args:
            max_pods: Maximum number of pods allowed on a node.
        """
        self.max_pods = max_pods
        logger.debug(f"Initialized PodCountConstraint with max_pods={max_pods}")

    @property
    def name(self) -> str:
        """
        Get the name of this constraint enforcer.

        Returns:
            Human-readable name.
        """
        return "PodCountConstraint"

    def rank_nodes(
        self, nodes: List[V1Node], k8s_client: "KubernetesClient"
    ) -> List[V1Node]:
        """
        Filter nodes by pod count and rank by available capacity (fewest pods first).

        Args:
            nodes: List of V1Node objects to evaluate.
            k8s_client: KubernetesClient for interacting with the cluster.

        Returns:
            Filtered and ranked list of nodes with fewer than max_pods, ordered by pod count (ascending).
        """
        node_pod_counts: List[Tuple[V1Node, int]] = []

        for node in nodes:
            node_name = node.metadata.name if node.metadata else "unknown"

            try:
                pods = k8s_client.list_pods_on_node(node_name, active_only=True)
                pod_count = len(pods)

                if pod_count < self.max_pods:
                    node_pod_counts.append((node, pod_count))
                    logger.debug(
                        f"Node {node_name} satisfies constraint: {pod_count} < {self.max_pods} pods"
                    )
                else:
                    logger.debug(
                        f"Node {node_name} filtered out: {pod_count} >= {self.max_pods} pods"
                    )
            except Exception as e:
                logger.error(f"Error checking pod count for node {node_name}: {e}")
                continue

        # Sort by pod count (ascending) - nodes with fewer pods ranked higher
        node_pod_counts.sort(key=lambda x: x[1])

        ranked_nodes = [node for node, _ in node_pod_counts]

        logger.info(
            f"PodCountConstraint: {len(ranked_nodes)} nodes satisfy max_pods={self.max_pods} constraint"
        )

        return ranked_nodes
