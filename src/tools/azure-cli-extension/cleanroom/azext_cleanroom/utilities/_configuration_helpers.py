from typing import Any

import cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers as config_helpers
from azure.cli.core.util import CLIError
from cleanroom_common.azure_cleanroom_core.models.model import CleanRoomSpecification
from cleanroom_common.azure_cleanroom_core.models.secretstore import (
    SecretStoreSpecification,
)

from ..utilities._azcli_helpers import logger

# TODO (HPrabh): Model a Configuration class that wraps the below methods.


def get_default_config_file(config_file_name: str) -> str:
    from pathlib import Path

    from azure.cli.core.api import get_config_dir

    default_path = Path(get_config_dir()) / "cleanroom"
    default_path.mkdir(parents=True, exist_ok=True)
    default_config_file = default_path / config_file_name
    return default_config_file.resolve().as_posix()


def read_cleanroom_spec_internal(config_file) -> CleanRoomSpecification:
    try:
        spec = config_helpers.read_cleanroom_spec(config_file, logger)
    except FileNotFoundError:
        raise CLIError(
            f"Cannot find file {config_file}. Check the --*-config parameter value."
        )

    return spec


def write_cleanroom_spec_internal(config_file, config: CleanRoomSpecification):
    config_helpers.write_cleanroom_spec(config_file, config, logger)
