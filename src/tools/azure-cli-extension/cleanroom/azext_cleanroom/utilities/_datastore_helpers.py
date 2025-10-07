from pathlib import Path
from typing import Optional

from azure.cli.core.util import CLIError
from cleanroom_common.azure_cleanroom_core.exceptions.exception import *
from cleanroom_common.azure_cleanroom_core.models.datastore import (
    DataStoreEntry,
    DataStoreSpecification,
)
from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import Encryptor

from ..utilities._azcli_helpers import logger


class DataStoreConfiguration:
    """
    This class is used to read and write the data store configuration file.
    """

    @staticmethod
    def default_datastore_config_file() -> str:
        """
        Returns the default data store configuration file path.
        If the CLEANROOM_DATASTORE_CONFIG_FILE environment variable is set, it returns its value.
        If the environment variable is not set, it returns the default path for config files.
        """

        import os

        from ._configuration_helpers import get_default_config_file

        return os.environ.get(
            "CLEANROOM_DATASTORE_CONFIG_FILE"
        ) or get_default_config_file("datastores.yaml")

    @staticmethod
    def load(
        config_file: str, create_if_not_existing: bool = False
    ) -> DataStoreSpecification:
        import os

        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            read_datastore_config,
        )

        try:
            spec = read_datastore_config(config_file, logger)
        except FileNotFoundError:
            if (not os.path.exists(config_file)) and create_if_not_existing:
                spec = DataStoreSpecification(datastores=[])
            else:
                raise CLIError(
                    f"Cannot find file {config_file}. Check the --*-config parameter value."
                )

        return spec

    @staticmethod
    def store(config_file: str, config: DataStoreSpecification):
        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            write_datastore_config,
        )

        write_datastore_config(config_file, config, logger)

    @staticmethod
    def get_datastore(name, config_file) -> DataStoreEntry:
        try:
            datastore_config = DataStoreConfiguration.load(config_file)
            datastore = datastore_config.get_datastore_entry(name)
        except CleanroomSpecificationError as e:
            if e.code == ErrorCode.DataStoreNotFound:
                raise CLIError(
                    f"Datastore {name} not found. Run az cleanroom datastore add first."
                )

        return datastore


def config_add_datastore_internal(
    cleanroom_config_file,
    datastore_name,
    datastore_config_file,
    identity,
    access_mode,
    logger,
    secretstore_config_file="",
    dek_secret_store="",
    kek_secret_store="",
    kek_name="",
    access_name="",
):
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        config_add_datastore,
    )

    try:
        config_add_datastore(
            cleanroom_config_file,
            datastore_name,
            datastore_config_file,
            identity,
            access_mode,
            logger,
            secretstore_config_file,
            dek_secret_store,
            kek_secret_store,
            kek_name,
            access_name,
        )
    except CleanroomSpecificationError as e:
        match e.code:
            case ErrorCode.IdentityConfigurationNotFound:
                raise CLIError("Run az cleanroom config add-identity first.")
            case ErrorCode.UnsupportedDekSecretStore:
                raise CLIError(
                    "Unsupported secret store for DEK. Please use Standard or Premium Key Vault"
                )
            case ErrorCode.UnsupportedKekSecretStore:
                raise CLIError(
                    "Unsupported secret store for KEK. Please use MHSM or Premium Key Vault"
                )
            case _:
                raise CLIError(f"Error adding datastore: {e}")


def azcopy(
    source_location: str,
    target_location: str,
    use_cpk: bool,
    encryption_key: Optional[bytes],
):
    import base64
    import hashlib
    import os

    from ._azcli_helpers import az_cli, logger

    # Get the tenant Id of the logged in user and indicate azcopy to use the tenant Id.
    # https://learn.microsoft.com/en-us/azure/storage/common/storage-ref-azcopy-configuration-settings
    tenant_id = az_cli("account show --query tenantId -o tsv")
    if isinstance(tenant_id, str):
        os.environ["AZCOPY_TENANT_ID"] = tenant_id

    azcopy_auto_login_type = "AZCLI"
    account_details = az_cli("account show")
    if "user" in account_details:
        if (
            account_details["user"]["name"] == "userAssignedIdentity"
            or account_details["user"]["name"] == "systemAssignedIdentity"
        ):
            azcopy_auto_login_type = "MSI"

    os.environ["AZCOPY_AUTO_LOGIN_TYPE"] = azcopy_auto_login_type
    azcopy_cmd = [
        "azcopy",
        "copy",
        source_location,
        target_location,
        "--recursive",
    ]

    if use_cpk:
        # azcopy with CPK needs the values below for encryption
        # https://learn.microsoft.com/en-us/azure/storage/common/storage-ref-azcopy-copy
        assert (
            encryption_key is not None
        ), "Encryption key must not be None when encryption mode is CPK"
        encryption_key_base_64 = base64.b64encode(encryption_key).decode("utf-8")
        encryption_key_sha256 = hashlib.sha256(encryption_key).digest()
        encryption_key_sha256_base_64 = base64.b64encode(encryption_key_sha256).decode(
            "utf-8"
        )
        os.environ["CPK_ENCRYPTION_KEY"] = encryption_key_base_64
        os.environ["CPK_ENCRYPTION_KEY_SHA256"] = encryption_key_sha256_base_64

        azcopy_cmd.append("--cpk-by-value")

    import subprocess

    max_retries = 5
    delay = 10
    attempt = 0
    while True:
        result: subprocess.CompletedProcess
        try:
            logger.warning(
                f"Copying dataset from {source_location} to {target_location}. Attempt {attempt + 1} of {max_retries}."
            )

            result = subprocess.run(
                azcopy_cmd,
                capture_output=True,
            )
        except FileNotFoundError:
            raise CLIError(
                "azcopy not installed. Install from https://github.com/Azure/azure-storage-azcopy?tab=readme-ov-file#download-azcopy and try again."
            )

        isRetryable = False
        try:
            for line in str.splitlines(result.stdout.decode()):
                logger.warning(line)
            for line in str.splitlines(result.stderr.decode()):
                logger.warning(line)
            result.check_returncode()
            break
        except subprocess.CalledProcessError:
            for line in str.splitlines(result.stdout.decode()):
                logger.error(line)
                # Have seen AuthorizationPermissionMismatch if permission was recently given and its not percolated yet. So we retry.
                if "ERROR CODE: AuthorizationPermissionMismatch" in line:
                    isRetryable = True
            for line in str.splitlines(result.stderr.decode()):
                if "ERROR CODE: AuthorizationPermissionMismatch" in line:
                    isRetryable = True
                logger.error(line)

            if isRetryable and attempt < max_retries:
                import time

                logger.warning(
                    f"Hit retryable issue while copying dataset from {source_location} to {target_location}. Will try again in {delay}s."
                )

                attempt += 1
                time.sleep(delay)
            else:
                raise CLIError(
                    f"Failed to copy data. Total attempts: {attempt + 1}. See error details above."
                )


def config_get_datastore_name_internal(cleanroom_config, access_name, access_mode):
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        config_get_datastore_name,
    )

    try:
        return config_get_datastore_name(
            cleanroom_config, access_name, access_mode, logger
        )
    except CleanroomSpecificationError as e:
        if e.code == ErrorCode.DataStoreNotFound:
            raise CLIError(f"{access_name} not found in cleanroom configuration.")


def encrypt_file_internal(plaintextPath, key, blockSize, ciphertextPath):
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        encrypt_file,
    )

    from ._azcli_helpers import logger

    try:
        encrypt_file(plaintextPath, key, blockSize, ciphertextPath, logger)
    except Exception as e:
        raise CLIError(f"Error during encryption: {e}")


def decrypt_file_internal(ciphertextPath, key, blockSize, plaintextPath):
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        decrypt_file,
    )

    from ._azcli_helpers import logger

    try:
        decrypt_file(ciphertextPath, key, blockSize, plaintextPath, logger)
    except Exception as e:
        raise CLIError(f"Error during decryption: {e}")


def cryptocopy(
    operation: Encryptor.Operation,
    datastore_name,
    datastore_config_file,
    source_path,
    destination_path,
    blockSize,
    logger,
):
    import base64
    import glob
    import os

    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        decrypt_file,
        encrypt_file,
    )

    from ._secretstore_helpers import SecretStoreConfiguration

    datastore = DataStoreConfiguration.get_datastore(
        datastore_name, datastore_config_file
    )
    if operation == Encryptor.Operation.Decrypt:
        destination_path = os.path.join(
            destination_path, datastore.name, datastore.storeName
        )

    os.makedirs(destination_path, mode=0o755, exist_ok=True)
    # Get the key path.
    secret_store = SecretStoreConfiguration.get_secretstore(
        datastore.secretstore_name, datastore.secretstore_config
    )

    encryption_key = secret_store.get_secret(datastore_name)
    blockSize = int(blockSize)
    blockSize *= 1024 * 1024

    for source_file_path in Path(source_path).glob("*"):
        source_file = str(source_file_path)
        if os.path.isfile(source_file):
            source_rel_path = os.path.relpath(source_file, start=source_path)
            destination_file = os.path.join(destination_path, source_rel_path)
            destination_dir = os.path.dirname(destination_file)
            os.makedirs(destination_dir, exist_ok=True)

            logger.info(f"[{operation}] '{source_file}' -> '{destination_file}'")
            if operation == Encryptor.Operation.Encrypt:
                encrypt_file(
                    source_file, encryption_key, blockSize, destination_file, logger
                )
            else:
                decrypt_file(
                    source_file, encryption_key, blockSize, destination_file, logger
                )


def generate_wrapped_dek(datastore_name, datastore_config_file, public_key, logger):
    import base64

    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.asymmetric import padding

    from ._secretstore_helpers import SecretStoreConfiguration

    datastore = DataStoreConfiguration.get_datastore(
        datastore_name, datastore_config_file
    )
    secret_store = SecretStoreConfiguration.get_secretstore(
        datastore.secretstore_name, datastore.secretstore_config
    )
    dek_bytes = secret_store.get_secret(datastore_name)
    return base64.b64encode(
        public_key.encrypt(
            dek_bytes,
            padding.OAEP(
                mgf=padding.MGF1(algorithm=hashes.SHA256()),
                algorithm=hashes.SHA256(),
                label=None,
            ),
        )
    ).decode()
