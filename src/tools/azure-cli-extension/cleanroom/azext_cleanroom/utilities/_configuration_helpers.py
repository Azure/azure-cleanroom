from typing import Any
from ..utilities._azcli_helpers import logger
import cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers as config_helpers
from azure.cli.core.util import CLIError
from cleanroom_common.azure_cleanroom_core.models.model import CleanRoomSpecification
from cleanroom_common.azure_cleanroom_core.models.datastore import (
    DatastoreSpecification,
)
from cleanroom_common.azure_cleanroom_core.models.secretstore import (
    SecretStoreSpecification,
)


# TODO (HPrabh): Model a Configuration class that wraps the below methods.


def read_cleanroom_spec_internal(config_file) -> CleanRoomSpecification:
    try:
        spec = config_helpers.read_cleanroom_spec(config_file, logger)
    except FileNotFoundError:
        raise CLIError(
            f"Cannot find file {config_file}. Check the --*-config parameter value."
        )

    return spec


def read_datastore_config_internal(config_file) -> DatastoreSpecification:
    try:
        spec = config_helpers.read_datastore_config(config_file, logger)
    except FileNotFoundError:
        raise CLIError(
            f"Cannot find file {config_file}. Check the --*-config parameter value."
        )

    return spec


def read_secretstore_config_internal(config_file) -> SecretStoreSpecification:
    try:
        spec = config_helpers.read_secretstore_config(config_file, logger)
    except FileNotFoundError:
        raise CLIError(
            f"Cannot find file {config_file}. Check the --*-config parameter value."
        )

    return spec


def write_cleanroom_spec_internal(config_file, config: CleanRoomSpecification):
    config_helpers.write_cleanroom_spec(config_file, config, logger)


def write_datastore_config_internal(config_file, datastore: DatastoreSpecification):
    config_helpers.write_datastore_config(config_file, datastore, logger)


def write_secretstore_config_internal(
    config_file, secretstore: SecretStoreSpecification
):
    config_helpers.write_secretstore_config(config_file, secretstore, logger)
