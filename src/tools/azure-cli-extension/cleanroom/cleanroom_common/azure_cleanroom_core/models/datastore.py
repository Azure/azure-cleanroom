import uuid
from ast import alias
from enum import Enum, StrEnum
from typing import List, Optional

from pydantic import BaseModel, Field

from .cleanroom import AccessPoint, Identity
from .dataset import DataSchema
from .secretstore import SecretStoreEntry


class DataStoreEntry(BaseModel):
    class StoreType(StrEnum):
        Azure_BlobStorage = "Azure_BlobStorage"
        Azure_BlobStorage_DataLakeGen2 = "Azure_BlobStorage_DataLakeGen2"
        Azure_OneLake = "Azure_OneLake"
        Aws_S3 = "Aws_S3"

    class AccessMode(Enum):
        Source = 1
        Sink = 2

    class EncryptionMode(StrEnum):
        CSE = "CSE"
        SSE_CPK = "CPK"
        SSE = "SSE"

    name: str
    secretstore_config: Optional[str] = None
    secretstore_name: Optional[str] = None
    encryptionMode: Optional[EncryptionMode] = None
    storeType: StoreType
    storeProviderUrl: str
    storeProviderConfiguration: Optional[str] = None
    storeName: str
    datasetSchema: Optional[DataSchema] = None

    def get_access_point(
        self,
        access_name: str,
        access_mode: AccessMode,
        access_identity: Identity,
        kek_name: str,
        dek_secret_store_entry: SecretStoreEntry,
        kek_secret_store_entry: SecretStoreEntry,
    ) -> AccessPoint:
        import base64
        import json

        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode
        from .cleanroom import (
            AccessPointType,
            CleanroomSecret,
            EncryptionSecret,
            EncryptionSecrets,
            PrivacyProxySettings,
            ProtocolType,
            ProxyMode,
            ProxyType,
            Resource,
            ResourceType,
            SecretType,
            ServiceEndpoint,
        )

        datastore_entry = self
        encryption_secrets = None
        if datastore_entry.encryptionMode in [
            DataStoreEntry.EncryptionMode.CSE,
            DataStoreEntry.EncryptionMode.SSE_CPK,
        ]:
            assert (
                dek_secret_store_entry is not None
                and kek_secret_store_entry is not None
            ), (
                f"Secret store information not specified when generating access point for "
                f"datastore {datastore_entry.name} using encryption mode "
                f"{datastore_entry.encryptionMode}"
            )

            if kek_name is None or kek_name == "":
                kek_name = (
                    str(
                        uuid.uuid3(
                            uuid.NAMESPACE_X500, datastore_entry.name + access_name
                        )
                    )[:8]
                    + "-kek"
                )

            wrapped_dek_name = f"wrapped-{datastore_entry.name}-dek-{kek_name}"

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
                            id=dek_secret_store_entry.name,
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
                            id=kek_secret_store_entry.name,
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
            provider_protocol = ProtocolType.Azure_BlobStorage
        elif (
            datastore_entry.storeType
            == DataStoreEntry.StoreType.Azure_BlobStorage_DataLakeGen2
        ):
            proxy_type = (
                ProxyType.SecureVolume__ReadOnly__Azure__BlobStorage__DataLakeGen2
                if access_mode == DataStoreEntry.AccessMode.Source
                else ProxyType.SecureVolume__ReadWrite__Azure__BlobStorage__DataLakeGen2
            )
            provider_protocol = ProtocolType.Azure_BlobStorage_DataLakeGen2
        elif datastore_entry.storeType == DataStoreEntry.StoreType.Azure_OneLake:
            proxy_type = (
                ProxyType.SecureVolume__ReadOnly__Azure__OneLake
                if access_mode == DataStoreEntry.AccessMode.Source
                else ProxyType.SecureVolume__ReadWrite__Azure__OneLake
            )
            provider_protocol = ProtocolType.Azure_OneLake
        elif datastore_entry.storeType == DataStoreEntry.StoreType.Aws_S3:
            proxy_type = (
                ProxyType.SecureVolume__ReadOnly__Aws__S3
                if access_mode == DataStoreEntry.AccessMode.Source
                else ProxyType.SecureVolume__ReadWrite__Aws__S3
            )
            provider_protocol = ProtocolType.Aws_S3

        encryption_mode = str(datastore_entry.encryptionMode)

        store = Resource(
            name=datastore_entry.storeName,
            type=ResourceType(str(datastore_entry.storeType)),
            id=datastore_entry.name,
            provider=ServiceEndpoint(
                protocol=provider_protocol,
                url=datastore_entry.storeProviderUrl,
                configuration=datastore_entry.storeProviderConfiguration,
            ),
        )

        privacyProxySettings = PrivacyProxySettings(
            proxyType=proxy_type,
            proxyMode=ProxyMode.Secure,
            configuration=base64.b64encode(
                json.dumps(
                    {"KeyType": "KEK", "EncryptionMode": encryption_mode}
                ).encode()
            ).decode(),
            encryptionSecrets=encryption_secrets,
            encryptionSecretAccessIdentity=access_identity,
        )

        if access_mode == DataStoreEntry.AccessMode.Source:
            access_point_type = AccessPointType.Volume_ReadOnly
        else:
            assert (
                access_mode == DataStoreEntry.AccessMode.Sink
            ), f"Unknown access mode {access_mode} for datastore {access_name}."
            access_point_type = AccessPointType.Volume_ReadWrite

        access_point = AccessPoint(
            name=access_name,
            type=access_point_type,
            path="",
            store=store,
            identity=access_identity,
            protection=privacyProxySettings,
        )
        return access_point


class DataStoreSpecification(BaseModel):
    datastores: Optional[List[DataStoreEntry]] = Field(default_factory=list)

    def check_datastore_entry(
        self, datastore_name: str
    ) -> tuple[bool, Optional[int], Optional[DataStoreEntry]]:
        self.datastores = self.datastores or []
        for index, x in enumerate(self.datastores):
            if x.name == datastore_name:
                return True, index, x

        return False, None, None

    def get_datastore_entry(self, datastore_name: str) -> DataStoreEntry:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        exists, _, datastore_entry = self.check_datastore_entry(datastore_name)
        if not exists:
            raise CleanroomSpecificationError(
                ErrorCode.DataStoreNotFound, (f"Datastore {datastore_name} not found.")
            )

        return datastore_entry

    def add_datastore_entry(self, datastore_entry: DataStoreEntry) -> None:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        exists, _, _ = self.check_datastore_entry(datastore_entry.name)
        if exists:
            raise CleanroomSpecificationError(
                ErrorCode.DataStoreAlreadyExists,
                (f"Datastore {datastore_entry.name} already exists."),
            )

        self.datastores = self.datastores or []
        self.datastores.append(datastore_entry)
