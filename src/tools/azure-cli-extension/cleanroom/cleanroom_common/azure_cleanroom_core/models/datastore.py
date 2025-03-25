from pydantic import BaseModel
from typing import List
from enum import Enum, StrEnum


class DatastoreEntry(BaseModel):
    class StoreType(StrEnum):
        Azure_BlobStorage = "Azure_BlobStorage"
        Azure_OneLake = "Azure_OneLake"

    class AccessMode(Enum):
        Source = 1
        Sink = 2

    class EncryptionMode(StrEnum):
        CSE = "CSE"
        SSE_CPK = "CPK"
        SSE = "SSE"

    name: str
    secretstore_config: str
    secretstore_name: str
    encryptionMode: EncryptionMode
    storeType: StoreType
    storeProviderUrl: str
    storeName: str


class DatastoreSpecification(BaseModel):
    datastores: List[DatastoreEntry]
