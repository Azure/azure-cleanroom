import json
import enum


class ErrorCode(enum.StrEnum):
    DatastoreNotFound = "DatastoreNotFound"
    SecretStoreNotFound = "SecretStoreNotFound"
    IdentityConfigurationNotFound = "IdentityConfigurationNotFound"
    UnsupportedDekSecretStore = "UnsupportedDekSecretStore"
    UnsupportedKekSecretStore = "UnsupportedKekSecretStore"
    MultipleApplicationEndpointsNotSupported = (
        "MultipleApplicationEndpointsNotSupported"
    )
    DatasinkNotFound = "DatasinkNotFound"
    DuplicatePort = "DuplicatePort"


class CleanroomSpecificationError(Exception):
    def __init__(self, code, message):
        self.code = code
        self.message = message

    def __str__(self):
        return json.dumps({"code": self.code, "message": self.message})
