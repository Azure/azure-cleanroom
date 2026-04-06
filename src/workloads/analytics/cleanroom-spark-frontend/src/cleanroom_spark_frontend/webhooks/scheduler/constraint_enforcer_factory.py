"""Factory for creating constraint enforcer instances."""

import logging
from typing import Dict

from .constraint_enforcer import ConstraintEnforcer
from .pod_count_constraint import PodCountConstraint

logger = logging.getLogger("constraint_enforcer_factory")


class ConstraintEnforcerFactory:

    @staticmethod
    def create_enforcer(
        constraint_type: str, constraint_value: str
    ) -> ConstraintEnforcer:
        """
        Create a constraint enforcer based on the constraint type.

        Args:
            constraint_type: The type of constraint (e.g., "POD_COUNT").
            constraint_value: The value for the constraint (e.g., "25").

        Returns:
            An instance of ConstraintEnforcer.

        Raises:
            ValueError: If the constraint type is unknown or value is invalid.
        """
        if constraint_type == "POD_COUNT":
            try:
                max_pods = int(constraint_value)
                if max_pods <= 0:
                    raise ValueError(f"POD_COUNT must be positive, got {max_pods}")
                logger.info(f"Creating PodCountConstraint with max_pods={max_pods}")
                return PodCountConstraint(max_pods=max_pods)
            except ValueError as e:
                logger.error(
                    f"Invalid value for POD_COUNT constraint: {constraint_value}"
                )
                raise ValueError(
                    f"Invalid POD_COUNT value '{constraint_value}': must be a positive integer"
                ) from e
        else:
            logger.error(f"Unknown constraint type: {constraint_type}")
            raise ValueError(f"Unknown constraint type: {constraint_type}")

    @staticmethod
    def create_enforcers(constraints: Dict[str, str]) -> list[ConstraintEnforcer]:
        """
        Create multiple constraint enforcers from a constraint dictionary.

        Args:
            constraints: Dictionary mapping constraint types to their values.

        Returns:
            List of ConstraintEnforcer instances to be executed in pipeline.
        """
        enforcers = []
        for constraint_type, constraint_value in constraints.items():
            try:
                enforcer = ConstraintEnforcerFactory.create_enforcer(
                    constraint_type, constraint_value
                )
                enforcers.append(enforcer)
            except ValueError as e:
                logger.warning(
                    f"Skipping invalid constraint {constraint_type}={constraint_value}: {e}"
                )
                continue
        return enforcers
