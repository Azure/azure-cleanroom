from typing import Any

from ..models.cleanroom import *
from ..models.collaboration import CollaborationSpecification
from ..models.datastore import *
from ..models.query import Query
from ..models.secretstore import *

# TODO (HPrabh): Model a Configuration class that wraps the below methods.


def read_cleanroom_spec(config_file, logger) -> CleanRoomSpecification:
    spec = _read_configuration_file(config_file, logger)
    return CleanRoomSpecification(**spec)


def read_datastore_config(config_file, logger) -> DataStoreSpecification:
    spec = _read_configuration_file(config_file, logger)
    return DataStoreSpecification(**spec)


def read_secretstore_config(config_file, logger) -> SecretStoreSpecification:
    spec = _read_configuration_file(config_file, logger)
    return SecretStoreSpecification(**spec)


def read_collaboration_config(config_file, logger) -> CollaborationSpecification:
    spec = _read_configuration_file(config_file, logger)
    return CollaborationSpecification(**spec)


def read_querysegment_file_config(config_file, logger) -> Query:
    spec = _read_configuration_file(config_file, logger)
    return Query(**spec)


def _read_configuration_file(config_file, logger) -> dict[str, Any]:
    import yaml

    logger.info(f"Reading configuration file {config_file}")
    with open(config_file, "r") as f:
        config = yaml.safe_load(f)
        return {} if config is None else config


def write_cleanroom_spec(config_file, config: CleanRoomSpecification, logger):
    _write_configuration_file(config_file, config, logger)


def write_datastore_config(config_file, datastore: DataStoreSpecification, logger):
    _write_configuration_file(config_file, datastore, logger)


def write_secretstore_config(
    config_file, secretstore: SecretStoreSpecification, logger
):
    _write_configuration_file(config_file, secretstore, logger)


def write_collaboration_config(
    config_file, collaboration: CollaborationSpecification, logger
):
    _write_configuration_file(config_file, collaboration, logger)


def write_querysegment_file_config(config_file, config: Query, logger):
    _write_configuration_file(config_file, config, logger)


def _write_configuration_file(config_file, config: BaseModel, logger):
    import yaml

    with open(config_file, "w") as f:
        yaml.dump(config.model_dump(mode="json"), f)
