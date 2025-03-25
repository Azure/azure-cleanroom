from ..models.datastore import *
from ..models.secretstore import *
from ..models.model import *
from typing import Any


# TODO (HPrabh): Model a Configuration class that wraps the below methods.


def read_cleanroom_spec(config_file, logger) -> CleanRoomSpecification:
    spec = _read_configuration_file(config_file, logger)
    return CleanRoomSpecification(**spec)


def read_datastore_config(config_file, logger) -> DatastoreSpecification:
    spec = _read_configuration_file(config_file, logger)
    return DatastoreSpecification(**spec)


def read_secretstore_config(config_file, logger) -> SecretStoreSpecification:
    spec = _read_configuration_file(config_file, logger)
    return SecretStoreSpecification(**spec)


def _read_configuration_file(config_file, logger) -> dict[str, Any]:
    import yaml

    logger.info(f"Reading configuration file {config_file}")
    with open(config_file, "r") as f:
        return yaml.safe_load(f)


def write_cleanroom_spec(config_file, config: CleanRoomSpecification, logger):
    _write_configuration_file(config_file, config, logger)


def write_datastore_config(config_file, datastore: DatastoreSpecification, logger):
    _write_configuration_file(config_file, datastore, logger)


def write_secretstore_config(
    config_file, secretstore: SecretStoreSpecification, logger
):
    _write_configuration_file(config_file, secretstore, logger)


def _write_configuration_file(config_file, config: BaseModel, logger):
    import yaml

    with open(config_file, "w") as f:
        yaml.dump(config.model_dump(mode="json"), f)


def generate_backcompat_datastore_configuration(cleanroom_config):
    import os

    file_name, file_ext = os.path.splitext(cleanroom_config)
    datastore_config_file = file_name + "_datastore_config" + file_ext

    datastore_keys_dir = f"{file_name}_datastore.keys"
    return datastore_config_file, datastore_keys_dir
