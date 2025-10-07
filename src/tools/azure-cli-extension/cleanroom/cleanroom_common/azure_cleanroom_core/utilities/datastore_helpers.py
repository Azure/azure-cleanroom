import base64
import json

from ..exceptions import *
from ..exceptions.exception import CleanroomSpecificationError, ErrorCode
from ..models.datastore import *
from ..models.model import *


class Encryptor:
    import os

    aes_encryptor_so = f"{os.path.dirname(__file__)}{os.path.sep}..{os.path.sep}binaries{os.path.sep}aes_encryptor.so"

    from typing import Final

    # encryptor tooling constants
    NonceSize: Final[int] = 12
    AuthTagSize: Final[int] = 16
    PadLengthSize: Final[int] = 8
    MetaSize: Final[int] = NonceSize + AuthTagSize

    from enum import Enum

    class Operation(Enum):
        Encrypt = "Encrypt"
        Decrypt = "Decrypt"


def config_add_datastore(
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
    from .configuration_helpers import (
        read_cleanroom_spec,
        read_datastore_config,
        read_secretstore_config,
        write_cleanroom_spec,
    )

    access_name = access_name or datastore_name
    cleanroom_spec = read_cleanroom_spec(cleanroom_config_file, logger)

    datastore_config = read_datastore_config(datastore_config_file, logger)
    datastore_entry = datastore_config.get_datastore_entry(datastore_name)

    if access_mode == DataStoreEntry.AccessMode.Source:
        node = "datasources"
        candidate_list = cleanroom_spec.datasources
        access_point_type = AccessPointType.Volume_ReadOnly
    else:
        assert (
            access_mode == DataStoreEntry.AccessMode.Sink
        ), f"Unknown access mode {access_mode} for datastore {access_name}."
        node = "datasinks"
        candidate_list = cleanroom_spec.datasinks
        access_point_type = AccessPointType.Volume_ReadWrite

    import uuid

    encryption_mode = datastore_entry.encryptionMode
    encryption_secrets = None
    proxy_config = {"EncryptionMode": str(encryption_mode)}
    if encryption_mode in [
        DataStoreEntry.EncryptionMode.CSE,
        DataStoreEntry.EncryptionMode.SSE_CPK,
    ]:
        proxy_config["KeyType"] = "KEK"
        kek_name = kek_name or (
            str(uuid.uuid3(uuid.NAMESPACE_X500, cleanroom_config_file))[:8] + "-kek"
        )
        wrapped_dek_name = f"wrapped-{datastore_name}-dek-{kek_name}"

        secretstore_config = read_secretstore_config(secretstore_config_file, logger)
        dek_secret_store_entry = secretstore_config.get_secretstore_entry(
            dek_secret_store
        )
        kek_secret_store_entry = secretstore_config.get_secretstore_entry(
            kek_secret_store
        )

        # TODO (HPrabh): Remove this check when key release is supported directly on the DEK.
        if not dek_secret_store_entry.is_secret_supported():
            raise CleanroomSpecificationError(
                ErrorCode.UnsupportedDekSecretStore,
                f"Unsupported DEK secret store {dek_secret_store_entry.name}. Please use Standard or Premium Key Vault",
            )

        if not kek_secret_store_entry.is_key_release_supported():
            raise CleanroomSpecificationError(
                ErrorCode.UnsupportedKekSecretStore,
                f"Unsupported KEK secret store {kek_secret_store_entry.name}. Please use MHSM or Premium Key Vault",
            )

        encryption_secrets = EncryptionSecrets(
            # TODO (HPrabh): Add support for DEK to be key released without having a wrapping KEK.
            dek=EncryptionSecret(
                name=wrapped_dek_name,
                secret=CleanroomSecret(
                    secretType=SecretType.Key,
                    backingResource=Resource(
                        id=dek_secret_store,
                        name=wrapped_dek_name,
                        type=ResourceType.AzureKeyVault,
                        provider=ServiceEndpoint(
                            protocol=ProtocolType.AzureKeyVault_Secret,
                            url=dek_secret_store_entry.storeProviderUrl,
                        ),
                    ),
                ),
            ),
            kek=EncryptionSecret(
                name=kek_name,
                secret=CleanroomSecret(
                    secretType=SecretType.Key,
                    backingResource=Resource(
                        id=kek_secret_store,
                        name=kek_name,
                        type=ResourceType.AzureKeyVault,
                        provider=ServiceEndpoint(
                            protocol=ProtocolType.AzureKeyVault_SecureKey,
                            url=kek_secret_store_entry.storeProviderUrl,
                            configuration=kek_secret_store_entry.configuration,
                        ),
                    ),
                ),
            ),
        )
    if datastore_entry.storeType == DataStoreEntry.StoreType.Azure_BlobStorage:
        proxy_type = (
            ProxyType.SecureVolume__ReadOnly__Azure__BlobStorage
            if access_mode == DataStoreEntry.AccessMode.Source
            else ProxyType.SecureVolume__ReadWrite__Azure__BlobStorage
        )
        access_identity = [x for x in cleanroom_spec.identities if x.name == identity]
        if len(access_identity) == 0:
            raise CleanroomSpecificationError(
                ErrorCode.IdentityConfigurationNotFound,
                "Identity configuration not found.",
            )
        protection_identity = access_identity[0]
        provider_protocol = ProtocolType.Azure_BlobStorage
    elif datastore_entry.storeType == DataStoreEntry.StoreType.Azure_OneLake:
        proxy_type = (
            ProxyType.SecureVolume__ReadOnly__Azure__OneLake
            if access_mode == DataStoreEntry.AccessMode.Source
            else ProxyType.SecureVolume__ReadWrite__Azure__OneLake
        )
        access_identity = [x for x in cleanroom_spec.identities if x.name == identity]
        if len(access_identity) == 0:
            raise CleanroomSpecificationError(
                ErrorCode.IdentityConfigurationNotFound,
                "Identity configuration not found.",
            )
        protection_identity = access_identity[0]
        provider_protocol = ProtocolType.Azure_OneLake
    elif datastore_entry.storeType == DataStoreEntry.StoreType.Aws_S3:
        proxy_type = (
            ProxyType.SecureVolume__ReadOnly__Aws__S3
            if access_mode == DataStoreEntry.AccessMode.Source
            else ProxyType.SecureVolume__ReadWrite__Aws__S3
        )
        protection_identity = None
        provider_protocol = ProtocolType.Aws_S3
    privacyProxySettings = PrivacyProxySettings(
        proxyType=proxy_type,
        proxyMode=ProxyMode.Secure,
        configuration=base64.b64encode(json.dumps(proxy_config).encode()).decode(),
        encryptionSecrets=encryption_secrets,
        encryptionSecretAccessIdentity=protection_identity,
    )
    store = Resource(
        name=datastore_entry.storeName,
        type=ResourceType(str(datastore_entry.storeType)),
        id=datastore_name,
        provider=ServiceEndpoint(
            protocol=provider_protocol,
            url=datastore_entry.storeProviderUrl,
            configuration=datastore_entry.storeProviderConfiguration,
        ),
    )

    access_point = AccessPoint(
        name=access_name,
        type=access_point_type,
        path="",
        store=store,
        identity=protection_identity,
        protection=privacyProxySettings,
    )

    index = next(
        (i for i, x in enumerate(candidate_list) if x.name == access_point.name),
        None,
    )
    if index == None:
        logger.info(f"Adding entry for {node} {access_point.name} in configuration.")
        candidate_list.append(access_point)
    else:
        logger.info(f"Patching {node} {access_point.name} in configuration.")
        candidate_list[index] = access_point

    write_cleanroom_spec(cleanroom_config_file, cleanroom_spec, logger)
    logger.warning(f"'{datastore_name}' added to '{node}' in cleanroom configuration.")


def config_get_datastore_name(cleanroom_config, access_name, access_mode, logger):
    from .configuration_helpers import read_cleanroom_spec

    spec = read_cleanroom_spec(cleanroom_config, logger)
    eligible_candidates = (
        spec.datasources
        if access_mode == DataStoreEntry.AccessMode.Source
        else spec.datasinks
    )
    candidates = [x for x in eligible_candidates if x.name == access_name]
    if len(candidates) == 0:
        raise CleanroomSpecificationError(
            ErrorCode.DataStoreNotFound, f"Datastore {access_name} not found."
        )

    datastore_name = candidates[0].store.id
    return datastore_name


def generate_safe_datastore_name(prefix, unique_name, friendly_name):
    import uuid

    safe_name = (
        f"{prefix}-"
        + str(uuid.uuid3(uuid.NAMESPACE_X500, unique_name))[:8]
        + f"-{friendly_name}"
    )[:63]

    return safe_name


def encrypt_file(plaintextPath, key, blockSize, ciphertextPath, logger):
    import base64
    import ctypes
    import json

    with open(ciphertextPath, "wb") as ciphertextFile:
        paddingLength = 0
        with open(plaintextPath, "rb") as plaintextFile:
            while True:
                buffer = plaintextFile.read(blockSize)
                dataLen = len(buffer)
                if not buffer:
                    break

                # Padding zeros to make it multiple of blocksize.
                if dataLen < blockSize:
                    paddingLength = blockSize - len(buffer)
                    padding = bytes(paddingLength)
                    buffer = buffer + padding

                # Invoking go encryption code.
                aes_encryption_lib = ctypes.CDLL(Encryptor.aes_encryptor_so)
                encrypt = aes_encryption_lib.GoEncryptChunk
                encrypt.argtypes = [ctypes.c_char_p]
                encrypt.restype = ctypes.c_void_p

                document = {
                    "Data": base64.b64encode(buffer).decode(),
                    "Key": base64.b64encode(key).decode(),
                }
                response = encrypt(json.dumps(document).encode("utf-8"))
                response_bytes = ctypes.string_at(response)
                response_string = response_bytes.decode("utf-8")
                jsonResponse = json.loads(response_string)
                encryptedChunk = base64.b64decode(jsonResponse.get("CipherText"))
                nonce = base64.b64decode(jsonResponse.get("Nonce"))

                encryptedChunk = nonce + encryptedChunk
                n = ciphertextFile.write(encryptedChunk)
                logger.info(
                    f"Encrypted chunk written along with nonce and auth tag, total bytes: {n}"
                )
            paddingLengthByte = paddingLength.to_bytes(8, byteorder="big")
            n = ciphertextFile.write(paddingLengthByte)
            logger.info(f"Padding length written, total bytes: {n}")


def decrypt_file(ciphertextPath, key, blockSize, plaintextPath, logger):
    import base64
    import ctypes
    import json
    import os

    with open(ciphertextPath, "rb") as ciphertextFile:
        with open(plaintextPath, "wb") as plaintextFile:
            ciphertextFileSize = os.path.getsize(ciphertextPath)
            totalBlocks = (ciphertextFileSize - Encryptor.PadLengthSize) // (
                blockSize + Encryptor.MetaSize
            )

            for i in range(totalBlocks):
                buffer = ciphertextFile.read(blockSize + Encryptor.MetaSize)
                nonce = buffer[: Encryptor.NonceSize]
                buffer = buffer[Encryptor.NonceSize :]
                if not buffer:
                    break
                # Invoking go decryption code.
                aes_encryption_lib = ctypes.CDLL(Encryptor.aes_encryptor_so)
                decrypt = aes_encryption_lib.GoDecryptChunk
                decrypt.argtypes = [ctypes.c_char_p]
                decrypt.restype = ctypes.c_void_p

                document = {
                    "CipherText": base64.b64encode(buffer).decode(),
                    "Nonce": base64.b64encode(nonce).decode(),
                    "Key": base64.b64encode(key).decode(),
                }

                response = decrypt(json.dumps(document).encode("utf-8"))
                response_bytes = ctypes.string_at(response)
                response_string = response_bytes.decode("utf-8")
                jsonResponse = json.loads(response_string)
                decryptedChunk = base64.b64decode(jsonResponse.get("PlainText"))
                if i == totalBlocks - 1:
                    paddingLength = ciphertextFile.read(Encryptor.PadLengthSize)
                    paddingLength = int.from_bytes(paddingLength, byteorder="big")
                    decryptedChunk = decryptedChunk[
                        : len(decryptedChunk) - paddingLength
                    ]

                n = plaintextFile.write(decryptedChunk)
