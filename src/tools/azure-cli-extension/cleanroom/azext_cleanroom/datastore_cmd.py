import os
from cleanroom_common.azure_cleanroom_core.models.datastore import (
    DatastoreEntry,
    DatastoreSpecification,
)
from cleanroom_common.azure_cleanroom_core.models.secretstore import SecretStoreEntry
from .utilities._azcli_helpers import logger


def datastore_add_cmd(
    cmd,
    datastore_name,
    datastore_config_file,
    secretstore_config_file,
    datastore_secret_store,
    encryption_mode: DatastoreEntry.EncryptionMode,
    backingstore_type: DatastoreEntry.StoreType,
    backingstore_id,
    container_name="",
):
    import os
    from .utilities._azcli_helpers import logger, az_cli
    from .utilities._configuration_helpers import (
        read_datastore_config_internal,
        write_datastore_config_internal,
    )

    from .utilities._secretstore_helpers import (
        get_secretstore,
        get_secretstore_entry_internal,
    )

    container_name = container_name or datastore_name

    if os.path.exists(datastore_config_file):
        datastore_config = read_datastore_config_internal(datastore_config_file)
    else:
        datastore_config = DatastoreSpecification(datastores=[])

    for index, x in enumerate(datastore_config.datastores):
        if x.name == datastore_name:
            logger.error(
                f"Datastore '{datastore_name}' already exists ({index}):\\n{x}"
            )
            return

    secret_store_entry = get_secretstore_entry_internal(
        datastore_secret_store, secretstore_config_file
    )

    # TODO (HPrabh): Add support for Key Vault.
    assert (
        secret_store_entry.secretStoreType
        == SecretStoreEntry.SecretStoreType.Local_File
    ), f"Unsupported secret store type passed {secret_store_entry.secretStoreType}."
    secret_store = get_secretstore(secret_store_entry)

    def generate_key():
        from Crypto.Random import get_random_bytes

        return get_random_bytes(32)

    _ = secret_store.add_secret(datastore_name, generate_secret=generate_key)

    if backingstore_type == DatastoreEntry.StoreType.Azure_BlobStorage:
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
    elif backingstore_type == DatastoreEntry.StoreType.Azure_OneLake:
        storeProviderUrl = backingstore_id
        storeName = ""

    datastore_entry = DatastoreEntry(
        name=datastore_name,
        secretstore_config=secretstore_config_file,
        secretstore_name=datastore_secret_store,
        encryptionMode=encryption_mode,
        storeType=backingstore_type,
        storeProviderUrl=storeProviderUrl,
        storeName=storeName,
    )
    datastore_config.datastores.append(datastore_entry)

    write_datastore_config_internal(datastore_config_file, datastore_config)
    logger.warning(f"Datastore '{datastore_name}' added to datastore configuration.")


def datastore_upload_cmd(cmd, datastore_name, datastore_config_file, source_path):
    import os
    from .utilities._datastore_helpers import get_datastore_internal, azcopy
    from .utilities._secretstore_helpers import (
        get_secretstore,
        get_secretstore_entry_internal,
    )

    datastore = get_datastore_internal(datastore_name, datastore_config_file)

    # Get the key path.
    container_url = datastore.storeProviderUrl + datastore.storeName
    source_path = source_path + f"{os.path.sep}*"

    if datastore.storeType == DatastoreEntry.StoreType.Azure_BlobStorage:
        use_cpk = (
            True
            if datastore.encryptionMode == DatastoreEntry.EncryptionMode.SSE_CPK
            else False
        )
        encryption_key = get_secretstore(
            get_secretstore_entry_internal(
                datastore.secretstore_name, datastore.secretstore_config
            )
        ).get_secret(datastore_name)
        assert (
            encryption_key is not None
        ), f"Encryption key for datastore {datastore_name} is None."
        azcopy(source_path, container_url, use_cpk, encryption_key)


def datastore_download_cmd(
    cmd, datastore_name, datastore_config_file, destination_path
):
    from .utilities._datastore_helpers import get_datastore_internal, azcopy
    from .utilities._secretstore_helpers import (
        get_secretstore,
        get_secretstore_entry_internal,
    )

    datastore = get_datastore_internal(datastore_name, datastore_config_file)

    datastore_path = os.path.join(destination_path, datastore_name)
    os.makedirs(datastore_path, exist_ok=True)

    # Get the key path.
    container_url = datastore.storeProviderUrl + datastore.storeName

    if (
        datastore.storeType == DatastoreEntry.StoreType.Azure_BlobStorage
        or datastore.storeType == DatastoreEntry.StoreType.Azure_OneLake
    ):
        use_cpk = (
            True
            if datastore.encryptionMode == DatastoreEntry.EncryptionMode.SSE_CPK
            else False
        )
        encryption_key = get_secretstore(
            get_secretstore_entry_internal(
                datastore.secretstore_name, datastore.secretstore_config
            )
        ).get_secret(datastore_name)
        assert (
            encryption_key is not None
        ), f"Encryption key for datastore {datastore_name} is None."
        azcopy(container_url, datastore_path, use_cpk, encryption_key)


def datastore_encrypt_cmd(
    cmd,
    datastore_name,
    datastore_config_file,
    source_path,
    destination_path,
    blockSize=4,
):
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        Encryptor,
    )
    from .utilities._datastore_helpers import cryptocopy

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
    datastore_config_file,
    source_path,
    destination_path,
    blockSize=4,
):
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        Encryptor,
    )
    from .utilities._datastore_helpers import cryptocopy

    cryptocopy(
        Encryptor.Operation.Decrypt,
        datastore_name,
        datastore_config_file,
        source_path,
        destination_path,
        blockSize,
        logger,
    )
