from ast import alias
from enum import Enum, StrEnum
from typing import List, Optional

from pydantic import BaseModel, Field


class DataStoreEntry(BaseModel):
    class StoreType(StrEnum):
        Azure_BlobStorage = "Azure_BlobStorage"
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
