import enum
import json

# StrEnum compatibility for Python < 3.11
try:
    from enum import StrEnum
except ImportError:
    # Fallback for Python < 3.11
    class StrEnum(str, enum.Enum):
        pass


class ErrorCode(StrEnum):
    DataStoreNotFound = "DataStoreNotFound"
    SecretStoreNotFound = "SecretStoreNotFound"
    IdentityConfigurationNotFound = "IdentityConfigurationNotFound"
    UnsupportedDekSecretStore = "UnsupportedDekSecretStore"
    UnsupportedKekSecretStore = "UnsupportedKekSecretStore"
    MultipleApplicationEndpointsNotSupported = (
        "MultipleApplicationEndpointsNotSupported"
    )
    DatasinkNotFound = "DatasinkNotFound"
    DuplicatePort = "DuplicatePort"
    DataStoreAlreadyExists = "DataStoreAlreadyExists"
    SecretStoreAlreadyExists = "SecretStoreAlreadyExists"


class CleanroomSpecificationError(Exception):
    def __init__(self, code, message):
        self.code = code
        self.message = message

    def __str__(self):
        return json.dumps({"code": self.code, "message": self.message})
