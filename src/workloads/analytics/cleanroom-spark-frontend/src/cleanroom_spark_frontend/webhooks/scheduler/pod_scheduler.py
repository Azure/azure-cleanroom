"""Pod scheduler for managing pod placement on Kubernetes nodes."""

import logging
from typing import TYPE_CHECKING, Dict, List, Optional

from cleanroom_spark_frontend.config.configuration import SchedulerSettings
from kubernetes.client import V1Node, V1Pod

from .constraint_enforcer_factory import ConstraintEnforcerFactory

if TYPE_CHECKING:
    from ...clients.kubernetes_client import KubernetesClient

logger = logging.getLogger("pod_scheduler")


class PodScheduler:
    """
    A scheduler that distributes pods across nodes with constraint enforcement.
    """

    def __init__(self, k8s_client: "KubernetesClient", config: SchedulerSettings):
        """
        Initialize the pod scheduler.

        Args:
            k8s_client: KubernetesClient for interacting with the cluster.
        """
        self.k8s_client = k8s_client
        self.config = config
        self.enforcers = ConstraintEnforcerFactory.create_enforcers(
            self.config.constraints
        )

    def get_all_nodes(self) -> List[V1Node]:
        """
        Get all nodes in the cluster.

        Returns:
            List of V1Node objects.
        """
        nodes = self.k8s_client.list_nodes(label_selector=self.config.node_selector)
        logger.info(f"Found {len(nodes)} nodes in the cluster")
        return nodes

    def get_pods_on_node(self, node_name: str) -> List[V1Pod]:
        """
        Get all pods running on a specific node.

        Args:
            node_name: Name of the node.

        Returns:
            List of V1Pod objects running on the node.
        """
        pods = self.k8s_client.list_pods_on_node(node_name)
        logger.info(f"Found {len(pods)} pods on node {node_name}")
        return pods

    def is_node_schedulable(self, node: V1Node) -> bool:
        """
        Check if a node is schedulable (not cordoned, not in maintenance).

        Args:
            node: V1Node object to check.

        Returns:
            True if the node is schedulable, False otherwise.
        """
        node_name = node.metadata.name if node.metadata else "unknown"

        # Check if node is unschedulable
        if node.spec and node.spec.unschedulable:
            logger.debug(f"Node {node_name} is unschedulable")
            return False

        if node.spec and node.spec.taints:
            for taint in node.spec.taints:
                if taint.effect in ["NoExecute"]:
                    logger.debug(
                        f"Node {node_name} has taint with effect {taint.effect}"
                    )
                    return False

        # Check if node is in Ready state
        if node.status and node.status.conditions:
            for condition in node.status.conditions:
                if condition.type == "Ready" and condition.status != "True":
                    logger.debug(f"Node {node_name} is not in Ready state")
                    return False

        return True

    def select_node(self) -> Optional[str]:
        """
        Select a node for scheduling a new pod based on availability and constraints.
        Uses a pipeline approach where constraints are applied serially.

        Returns:
            Name of the selected node, or None if no suitable node is available.
        """
        # Get all nodes
        nodes = self.get_all_nodes()

        # Filter to only schedulable nodes (basic Kubernetes checks)
        schedulable_nodes = [node for node in nodes if self.is_node_schedulable(node)]

        if not schedulable_nodes:
            logger.warning("No schedulable nodes available")
            return None

        logger.info(f"Found {len(schedulable_nodes)} schedulable nodes")

        # Execute enforcers in pipeline - each enforcer filters and ranks the nodes
        ranked_nodes = schedulable_nodes
        for i, enforcer in enumerate(self.enforcers):
            logger.debug(
                f"Applying enforcer {i+1}/{len(self.enforcers)}: {enforcer.name}"
            )
            ranked_nodes = enforcer.rank_nodes(ranked_nodes, self.k8s_client)

            if not ranked_nodes:
                logger.warning(f"No nodes available after applying {enforcer.name}")
                return None

            logger.debug(f"{len(ranked_nodes)} nodes remain after {enforcer.name}")

        if not ranked_nodes:
            logger.warning("No nodes available after constraint evaluation")
            return None

        # Select the top-ranked node (first in the list after all filtering and ranking)
        selected_node = ranked_nodes[0]
        selected_node_name = (
            selected_node.metadata.name if selected_node.metadata else "unknown"
        )

        logger.info(
            f"Selected node {selected_node_name} (ranked 1st out of {len(ranked_nodes)} candidates)"
        )

        return selected_node_name
