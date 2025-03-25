from ..exceptions.exception import CleanroomSpecificationError, ErrorCode
from ..models.secretstore import SecretStoreEntry


def get_secretstore_entry(
    secretstore_name, secretstore_config_file, logger
) -> SecretStoreEntry:
    from .configuration_helpers import read_secretstore_config

    secretstore_config = read_secretstore_config(secretstore_config_file, logger)
    for index, x in enumerate(secretstore_config.secretstores):
        if x.name == secretstore_name:
            return secretstore_config.secretstores[index]
    else:
        raise CleanroomSpecificationError(
            ErrorCode.SecretStoreNotFound, f"Secret store {secretstore_name} not found."
        )
