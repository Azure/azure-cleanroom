from azure.cli.core.util import CLIError
from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
    CleanroomSpecificationError,
    ErrorCode,
)
from cleanroom_common.azure_cleanroom_core.models.collaboration import (
    CollaborationContext,
    CollaborationSpecification,
)

from ..utilities._azcli_helpers import logger


class CollaborationConfiguration:
    """
    This class is used to read and write the collaboration configuration file.
    """

    @staticmethod
    def default_collaboration_config_file() -> str:
        """
        Returns the default collaboration configuration file path.
        If the CLEANROOM_COLLABORATION_CONFIG_FILE environment variable is set, it returns its value.
        If the environment variable is not set, it returns the default path for config files.
        """

        import os

        from ._configuration_helpers import get_default_config_file

        return os.environ.get(
            "CLEANROOM_COLLABORATION_CONFIG_FILE"
        ) or get_default_config_file("collaborations.yaml")

    @staticmethod
    def load(
        config_file: str,
        create_if_not_existing: bool = False,
        require_current_context: bool = True,
    ) -> CollaborationSpecification:
        import os

        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            read_collaboration_config,
        )

        try:
            spec = read_collaboration_config(config_file, logger)
        except FileNotFoundError:
            if (not os.path.exists(config_file)) and create_if_not_existing:
                spec = CollaborationSpecification(collaborations=[])
            else:
                raise CLIError(
                    f"Cannot find file {config_file}. Check the --*-config parameter value."
                )

        if require_current_context and (spec.current_context is None):
            raise CLIError(
                f"Current collaboration context is not set. Run 'az cleanroom collaboration context set' first."
            )

        return spec

    @staticmethod
    def store(config_file: str, config: CollaborationSpecification):
        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            write_collaboration_config,
        )

        write_collaboration_config(config_file, config, logger)

    @staticmethod
    def get_default_collaboration_context(config_file) -> CollaborationContext:
        collaboration_config = CollaborationConfiguration.load(
            config_file, require_current_context=True
        )
        try:
            collaboration = collaboration_config.get_active_collaboration_context()
        except CleanroomSpecificationError as e:
            if e.code == ErrorCode.CollaborationNotFound:
                raise CLIError(
                    f"Collaboration {collaboration_config.current_context} not found. Run 'az cleanroom collaboration context add' first."
                )
            elif e.code == ErrorCode.CurrentCollaborationNotSet:
                raise CLIError(
                    f"Current collaboration context is not set. Run 'az cleanroom collaboration context set' first."
                )

        return collaboration

    @staticmethod
    def set_default_collaboration_context(
        config_file: str, collaboration_name: str
    ) -> None:
        collaboration_config = CollaborationConfiguration.load(
            config_file, require_current_context=False
        )
        try:
            collaboration_config.set_active_collaboration_context(collaboration_name)
        except CleanroomSpecificationError as e:
            if e.code == ErrorCode.CollaborationNotFound:
                raise CLIError(
                    f"Collaboration {collaboration_name} not found. Run 'az cleanroom collaboration context add' first."
                )

        CollaborationConfiguration.store(config_file, collaboration_config)
