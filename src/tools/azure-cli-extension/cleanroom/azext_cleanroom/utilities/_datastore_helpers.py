from cleanroom_common.azure_cleanroom_core.exceptions.exception import *
from ..utilities._azcli_helpers import logger
from azure.cli.core.util import CLIError
from cleanroom_common.azure_cleanroom_core.models.datastore import DatastoreEntry
from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
    Encryptor,
)


def get_datastore_internal(datastore_name, datastore_config_file) -> DatastoreEntry:
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        get_datastore,
    )

    try:
        datastore = get_datastore(datastore_name, datastore_config_file, logger)
    except CleanroomSpecificationError as e:
        if e.code == ErrorCode.DatastoreNotFound:
            raise CLIError(
                f"Datastore {datastore_name} not found. Run az cleanroom datastore add first."
            )

    return datastore


def config_add_datastore_internal(
    cleanroom_config_file,
    datastore_name,
    datastore_config_file,
    identity,
    secretstore_config_file,
    dek_secret_store,
    kek_secret_store,
    kek_name,
    access_mode,
    logger,
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
            secretstore_config_file,
            dek_secret_store,
            kek_secret_store,
            kek_name,
            access_mode,
            logger,
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
    encryption_key: bytes,
):
    import os, hashlib, base64
    from azure.cli.core.util import CLIError
    from ._azcli_helpers import logger, az_cli

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
        encryption_key_base_64 = base64.b64encode(encryption_key).decode("utf-8")
        encryption_key_sha256 = hashlib.sha256(encryption_key).digest()
        encryption_key_sha256_base_64 = base64.b64encode(encryption_key_sha256).decode(
            "utf-8"
        )
        os.environ["CPK_ENCRYPTION_KEY"] = encryption_key_base_64
        os.environ["CPK_ENCRYPTION_KEY_SHA256"] = encryption_key_sha256_base_64

        azcopy_cmd.append("--cpk-by-value")

    import subprocess

    result: subprocess.CompletedProcess
    try:
        logger.warning(f"Copying dataset from {source_location} to {target_location}")

        result = subprocess.run(
            azcopy_cmd,
            capture_output=True,
        )
    except FileNotFoundError:
        raise CLIError(
            "azcopy not installed. Install from https://github.com/Azure/azure-storage-azcopy?tab=readme-ov-file#download-azcopy and try again."
        )

    try:
        for line in str.splitlines(result.stdout.decode()):
            logger.warning(line)
        for line in str.splitlines(result.stderr.decode()):
            logger.warning(line)
        result.check_returncode()
    except subprocess.CalledProcessError:
        for line in str.splitlines(result.stdout.decode()):
            logger.error(line)
        for line in str.splitlines(result.stderr.decode()):
            logger.error(line)
        raise CLIError("Failed to copy data. See error details above.")


def config_get_datastore_name_internal(cleanroom_config, access_name, access_mode):
    from azure.cli.core.util import CLIError
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        config_get_datastore_name,
    )

    try:
        return config_get_datastore_name(
            cleanroom_config, access_name, access_mode, logger
        )
    except CleanroomSpecificationError as e:
        if e.code == ErrorCode.DatastoreNotFound:
            raise CLIError(f"{access_name} not found in cleanroom configuration.")


def encrypt_file_internal(plaintextPath, key, blockSize, ciphertextPath):
    from azure.cli.core.util import CLIError
    from ._azcli_helpers import logger
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        encrypt_file,
    )

    try:
        encrypt_file(plaintextPath, key, blockSize, ciphertextPath, logger)
    except Exception as e:
        raise CLIError(f"Error during encryption: {e}")


def decrypt_file_internal(ciphertextPath, key, blockSize, plaintextPath):
    from azure.cli.core.util import CLIError
    from ._azcli_helpers import logger
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        decrypt_file,
    )

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
    import os, glob, base64
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        get_datastore,
    )
    from ._secretstore_helpers import get_secretstore
    from cleanroom_common.azure_cleanroom_core.utilities.secretstore_helpers import (
        get_secretstore_entry,
    )
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        encrypt_file,
        decrypt_file,
    )

    datastore = get_datastore(datastore_name, datastore_config_file, logger)
    if operation == Encryptor.Operation.Decrypt:
        source_path = os.path.join(source_path, datastore.name, datastore.storeName)
        destination_path = os.path.join(
            destination_path, datastore.name, datastore.storeName
        )

    os.makedirs(destination_path, mode=0o755, exist_ok=True)
    # Get the key path.
    secret_store = get_secretstore(
        get_secretstore_entry(
            datastore.secretstore_name, datastore.secretstore_config, logger
        )
    )
    encryption_key = secret_store.get_secret(datastore_name)
    blockSize = int(blockSize)
    blockSize *= 1024 * 1024

    # Recursively encypt/decrypt files in the source path.
    for source_file in glob.glob(os.path.join(source_path, "**/*"), recursive=True):
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
    from cryptography.hazmat.primitives.asymmetric import padding
    from cryptography.hazmat.primitives import hashes
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        get_datastore,
    )
    from ._secretstore_helpers import get_secretstore
    from cleanroom_common.azure_cleanroom_core.utilities.secretstore_helpers import (
        get_secretstore_entry,
    )

    datastore = get_datastore(datastore_name, datastore_config_file, logger)
    secret_store = get_secretstore(
        get_secretstore_entry(
            datastore.secretstore_name, datastore.secretstore_config, logger
        )
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
