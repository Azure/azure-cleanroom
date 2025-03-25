import os
from typing import List
from pydantic import BaseModel
from enum import StrEnum


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
    secretstores: List[SecretStoreEntry]
