import base64
import json
import os

from azure.cli.core.azclierror import CLIError
from cleanroom_common.azure_cleanroom_core.models.datastore import DataStoreEntry
from cleanroom_common.azure_cleanroom_core.models.secretstore import SecretStoreEntry

from .utilities._azcli_helpers import logger
from .utilities._datastore_helpers import DataStoreConfiguration
from .utilities._secretstore_helpers import SecretStoreConfiguration


def datastore_add_cmd(
    cmd,
    datastore_name,
    backingstore_type: DataStoreEntry.StoreType,
    encryption_mode=DataStoreEntry.EncryptionMode.SSE,
    datastore_secret_store="",
    backingstore_id="",
    aws_config_cgs_secret_id: str = "",
    datastore_config_file=DataStoreConfiguration.default_datastore_config_file(),
    secretstore_config_file: str = "",
    container_name="",
):
    from azure.cli.core.util import CLIError

    if backingstore_type in DataStoreEntry.StoreType.Aws_S3:
        assert (
            encryption_mode == DataStoreEntry.EncryptionMode.SSE
        ), f"Only SSE is supported for AWS S3. Got: {encryption_mode}"

        # TODO: Enable support for CSE and CPK encryption modes for AWS S3
        if not aws_config_cgs_secret_id:
            raise CLIError(
                f"AWS Config CGS secret ID must be specified for encryption mode '{encryption_mode}'."
            )
    if encryption_mode in [
        DataStoreEntry.EncryptionMode.CSE,
        DataStoreEntry.EncryptionMode.SSE_CPK,
    ]:
        if not datastore_secret_store:
            raise CLIError(
                f"Datastore secret store must be specified for encryption mode '{encryption_mode}'."
            )
    from .utilities._azcli_helpers import az_cli, logger
    from .utilities._datastore_helpers import DataStoreConfiguration
    from .utilities._secretstore_helpers import SecretStoreConfiguration

    container_name = container_name or datastore_name
    datastore_config = DataStoreConfiguration.load(
        datastore_config_file, create_if_not_existing=True
    )

    exists, index, datastore_entry = datastore_config.check_datastore_entry(
        datastore_name
    )
    if exists:
        logger.warning(
            f"Datastore '{datastore_name}' already exists ({index}):\\n{datastore_entry}"
        )
        return

    if (
        backingstore_type == DataStoreEntry.StoreType.Azure_BlobStorage
        or backingstore_type == DataStoreEntry.StoreType.Azure_OneLake
    ):
        if encryption_mode in [
            DataStoreEntry.EncryptionMode.CSE,
            DataStoreEntry.EncryptionMode.SSE_CPK,
        ]:
            secretstore_config_file = (
                secretstore_config_file
                or SecretStoreConfiguration.default_secretstore_config_file()
            )
            secret_store = SecretStoreConfiguration.get_secretstore(
                datastore_secret_store, secretstore_config_file
            )

            # TODO (HPrabh): Add support for Key Vault.
            assert (
                secret_store.entry.secretStoreType
                == SecretStoreEntry.SecretStoreType.Local_File
            ), f"Unsupported secret store type passed {secret_store.entry.secretStoreType}."

            def generate_key():
                from Crypto.Random import get_random_bytes

                return get_random_bytes(32)

            _ = secret_store.add_secret(datastore_name, generate_secret=generate_key)

    if backingstore_type == DataStoreEntry.StoreType.Azure_BlobStorage:
        assert backingstore_id != "", "backingstore_id is required."
        storage_account_name = az_cli(
            f"storage account show --ids {backingstore_id} --query name"
        )
        storage_account_url = az_cli(
            f"storage account show --ids {backingstore_id} --query primaryEndpoints.blob"
        )

        logger.warning(
            f"Creating storage container '{container_name}' in {backingstore_id}."
        )
        container = az_cli(
            f"storage container create --name {container_name} --account-name {storage_account_name} --auth-mode login"
        )

        storeProviderUrl = storage_account_url
        storeName = container_name
        storeProviderConfig = ""
    elif backingstore_type == DataStoreEntry.StoreType.Azure_OneLake:
        assert backingstore_id != "", "backingstore_id is required."
        storeProviderUrl = backingstore_id
        storeName = ""
        storeProviderConfig = ""
    elif backingstore_type == DataStoreEntry.StoreType.Aws_S3:
        assert aws_config_cgs_secret_id != "", "aws_config_cgs_secret_id is required."
        storeProviderUrl = backingstore_id or "https://s3.amazonaws.com"
        storeName = container_name
        storeProviderConfig = base64.b64encode(
            json.dumps({"secretId": aws_config_cgs_secret_id}).encode()
        ).decode()

    datastore_entry = DataStoreEntry(
        name=datastore_name,
        secretstore_config=secretstore_config_file,
        secretstore_name=datastore_secret_store,
        encryptionMode=encryption_mode,
        storeType=backingstore_type,
        storeProviderUrl=storeProviderUrl,
        storeProviderConfiguration=storeProviderConfig,
        storeName=storeName,
    )
    datastore_config.add_datastore_entry(datastore_entry)

    DataStoreConfiguration.store(datastore_config_file, datastore_config)
    logger.warning(f"Datastore '{datastore_name}' added to datastore configuration.")


def datastore_upload_cmd(
    cmd,
    datastore_name,
    source_path,
    datastore_config_file=DataStoreConfiguration.default_datastore_config_file(),
):
    import os

    from .utilities._datastore_helpers import azcopy
    from .utilities._secretstore_helpers import SecretStoreConfiguration

    datastore = DataStoreConfiguration.get_datastore(
        datastore_name, datastore_config_file
    )

    # Get the key path.
    container_url = datastore.storeProviderUrl + datastore.storeName
    source_path = source_path + f"{os.path.sep}*"

    if datastore.storeType == DataStoreEntry.StoreType.Azure_BlobStorage:
        use_cpk = (
            True
            if datastore.encryptionMode == DataStoreEntry.EncryptionMode.SSE_CPK
            else False
        )
        encryption_key = None
        if use_cpk:
            encryption_key = SecretStoreConfiguration.get_secretstore(
                datastore.secretstore_name, datastore.secretstore_config
            ).get_secret(datastore_name)
            assert (
                encryption_key is not None
            ), f"Encryption key for datastore {datastore_name} is None."
        azcopy(source_path, container_url, use_cpk, encryption_key)


def datastore_download_cmd(
    cmd,
    destination_path,
    datastore_name,
    datastore_config_file=DataStoreConfiguration.default_datastore_config_file(),
):
    import re

    from .utilities._azcli_helpers import az_cli, logger
    from .utilities._datastore_helpers import azcopy
    from .utilities._secretstore_helpers import SecretStoreConfiguration

    datastore = DataStoreConfiguration.get_datastore(
        datastore_name, datastore_config_file
    )
    datastore_path = os.path.join(destination_path, datastore_name)
    os.makedirs(datastore_path, exist_ok=True)

    # Get the key path.
    container_url = datastore.storeProviderUrl + datastore.storeName

    assert datastore.storeType in [
        DataStoreEntry.StoreType.Azure_BlobStorage,
        DataStoreEntry.StoreType.Azure_OneLake,
    ], f"Download is only supported for Azure Blob or Azure Onelake Storage datastores. Datastore '{datastore_name}' has type '{datastore.storeType}'."

    use_cpk = (
        True
        if datastore.encryptionMode == DataStoreEntry.EncryptionMode.SSE_CPK
        else False
    )
    encryption_key = None
    if use_cpk:
        encryption_key = SecretStoreConfiguration.get_secretstore(
            datastore.secretstore_name, datastore.secretstore_config
        ).get_secret(datastore_name)
        assert (
            encryption_key is not None
        ), f"Encryption key for datastore {datastore_name} is None."

    if datastore.storeType == DataStoreEntry.StoreType.Azure_OneLake:
        azcopy(container_url, datastore_path, use_cpk, encryption_key)
        return

    assert datastore.storeType == DataStoreEntry.StoreType.Azure_BlobStorage

    # Find the folders to download as the container may also contain marker files to indicate directories.
    # Downloading these marker files results in errors as the file system tries to create a file
    # with the same name as an existing directory or vice versa.
    storage_account_name = re.match(
        "https://(.*?).blob.core.windows.net", datastore.storeProviderUrl
    ).group(1)

    result = az_cli(
        f"storage blob list --container-name {datastore.storeName} "
        f"--account-name {storage_account_name} "
        f"--auth-mode login --delimiter '/' --output json"
    )

    if len(result) > 0:
        for entry in result:
            name = entry.get("name", "")
            content_length = entry.get("properties", {}).get("contentLength", 0)
            if content_length <= 0:
                logger.warning(
                    f"Skipping blob '{name}' with invalid content length {content_length}."
                )
                continue
            if "/" in name:
                folder = name.split("/")[0]
                folder_path = datastore_path + "/" + folder
                os.makedirs(folder_path, exist_ok=True)
            else:
                folder_path = datastore_path
            azcopy(
                f"{container_url}/{name}",
                folder_path,
                use_cpk,
                encryption_key,
            )
    else:
        logger.warning(
            f"No blobs found in container '{datastore.storeName}' of datastore '{datastore_name}'."
        )


def datastore_encrypt_cmd(
    cmd,
    datastore_name,
    source_path,
    destination_path,
    datastore_config_file=DataStoreConfiguration.default_datastore_config_file(),
    blockSize=16,
):
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        Encryptor,
    )

    from .utilities._datastore_helpers import cryptocopy

    datastore = DataStoreConfiguration.get_datastore(
        datastore_name, datastore_config_file
    )
    if datastore.encryptionMode != DataStoreEntry.EncryptionMode.CSE:
        raise CLIError(
            f"Encryption is only supported for datastores with encryption mode 'CSE'. Datastore '{datastore_name}' has mode '{datastore.encryptionMode}'."
        )
        return
    cryptocopy(
        Encryptor.Operation.Encrypt,
        datastore_name,
        datastore_config_file,
        source_path,
        destination_path,
        blockSize,
        logger,
    )


def datastore_decrypt_cmd(
    cmd,
    datastore_name,
    source_path,
    destination_path,
    datastore_config_file=DataStoreConfiguration.default_datastore_config_file(),
    blockSize=16,
):
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        Encryptor,
    )

    from .utilities._datastore_helpers import cryptocopy

    datastore = DataStoreConfiguration.get_datastore(
        datastore_name, datastore_config_file
    )
    if datastore.encryptionMode != DataStoreEntry.EncryptionMode.CSE:
        raise CLIError(
            f"Decryption is only supported for datastores with encryption mode 'CSE'. Datastore '{datastore_name}' has mode '{datastore.encryptionMode}'."
        )
        return
    cryptocopy(
        Encryptor.Operation.Decrypt,
        datastore_name,
        datastore_config_file,
        source_path,
        destination_path,
        blockSize,
        logger,
    )
