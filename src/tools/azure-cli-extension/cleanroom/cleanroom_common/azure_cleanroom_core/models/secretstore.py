import os
from enum import StrEnum
from typing import List, Optional

from pydantic import BaseModel, Field


class SecretStoreEntry(BaseModel):
    class SecretStoreType(StrEnum):
        Azure_KeyVault = "Azure_KeyVault"
        Azure_KeyVault_Managed_HSM = "Azure_KeyVault_Managed_HSM"
        Local_File = "Local_File"

    class SupportedSecretTypes(StrEnum):
        Secret = "Secret"
        Key = "Key"

    name: str
    secretStoreType: SecretStoreType
    storeProviderUrl: str
    configuration: str
    supportedSecretTypes: List[SupportedSecretTypes]

    def is_key_release_supported(self) -> bool:
        return SecretStoreEntry.SupportedSecretTypes.Key in self.supportedSecretTypes

    def is_secret_supported(self) -> bool:
        return SecretStoreEntry.SupportedSecretTypes.Secret in self.supportedSecretTypes


class SecretStoreSpecification(BaseModel):
    secretstores: Optional[List[SecretStoreEntry]] = Field(default_factory=list)

    def check_secretstore_entry(
        self, secretstore_name: str
    ) -> tuple[bool, Optional[int], Optional[SecretStoreEntry]]:
        self.secretstores = self.secretstores or []
        for index, x in enumerate(self.secretstores):
            if x.name == secretstore_name:
                return True, index, x

        return False, None, None

    def get_secretstore_entry(self, secretstore_name: str) -> SecretStoreEntry:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        exists, _, secretstore_entry = self.check_secretstore_entry(secretstore_name)
        if not exists:
            raise CleanroomSpecificationError(
                ErrorCode.SecretStoreNotFound,
                (f"Secret store {secretstore_name} not found."),
            )

        return secretstore_entry

    def add_secretstore_entry(self, secretstore_entry: SecretStoreEntry) -> None:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        exists, _, _ = self.check_secretstore_entry(secretstore_entry.name)
        if exists:
            raise CleanroomSpecificationError(
                ErrorCode.SecretStoreAlreadyExists,
                (f"Secret store {secretstore_entry.name} already exists."),
            )

        self.secretstores = self.secretstores or []
        self.secretstores.append(secretstore_entry)
