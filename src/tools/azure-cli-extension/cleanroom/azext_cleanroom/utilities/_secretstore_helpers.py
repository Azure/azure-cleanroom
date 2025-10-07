import base64
import json
import os
import tempfile
import uuid
from abc import ABC, ABCMeta, abstractmethod
from typing import Callable
from urllib.parse import urlparse

from azure.cli.core.util import CLIError
from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
    CleanroomSpecificationError,
    ErrorCode,
)
from cleanroom_common.azure_cleanroom_core.models.secretstore import (
    SecretStoreEntry,
    SecretStoreSpecification,
)

from ..utilities._azcli_helpers import logger


class ISecretStore(ABC):
    entry: SecretStoreEntry

    @abstractmethod
    def add_secret(
        self,
        secret_name: str,
        generate_secret: Callable,
        security_policy: str | None = None,
    ) -> bytes:
        pass

    @abstractmethod
    def get_secret(self, secret_name: str) -> bytes | None:
        pass


class LocalSecretStore(ISecretStore):
    def __init__(self, entry: SecretStoreEntry):
        self._entry = entry

    def add_secret(
        self,
        secret_name: str,
        generate_secret: Callable,
        security_policy: str | None = None,
    ) -> bytes:
        from ._azcli_helpers import logger

        key_file_path = os.path.abspath(
            os.path.join(self._entry.storeProviderUrl, f"{secret_name}.bin")
        )

        logger.warning(
            f"Creating secret {secret_name} in store {self._entry.storeProviderUrl}"
        )
        secret = generate_secret()
        with open(key_file_path, "wb") as key_file:
            key_file.write(secret)

        return secret

    def get_secret(self, secret_name: str) -> bytes | None:
        key_file_path = os.path.abspath(
            os.path.join(self._entry.storeProviderUrl, f"{secret_name}.bin")
        )
        if not os.path.exists(key_file_path):
            return

        with open(key_file_path, "rb") as key_file:
            return key_file.read()


class AzureSecureSecretStore(ISecretStore):
    def __init__(self, entry: SecretStoreEntry):
        self._entry = entry

    def add_secret(
        self,
        secret_name: str,
        generate_secret: Callable,
        security_policy: str | None = None,
    ) -> bytes:
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.primitives.asymmetric import rsa

        from ._azcli_helpers import az_cli, logger

        assert (
            security_policy is not None
        ), "Security policy is required for secure store."

        logger.warning(
            f"Creating secret {secret_name} in store {self._entry.storeProviderUrl}"
        )
        tempdir = tempfile.gettempdir()
        pem_file_path = os.path.join(tempdir, f"{secret_name}.pem")
        skr_file_path = os.path.join(tempdir, f"{secret_name}-skr-policy.json")

        try:
            private_key = generate_secret()

            assert isinstance(
                private_key, rsa.RSAPrivateKey
            ), f"Invalid private key type {type(private_key)}"
            private_key_bytes = private_key.private_bytes(
                encoding=serialization.Encoding.PEM,
                format=serialization.PrivateFormat.TraditionalOpenSSL,
                encryption_algorithm=serialization.NoEncryption(),
            )

            with open(pem_file_path, "w") as private_key_file:
                private_key_file.write(private_key_bytes.decode())

            maa_url = json.loads(base64.b64decode(self._entry.configuration).decode())[
                "authority"
            ]
            skr_policy = {
                "anyOf": [
                    {
                        "allOf": [
                            {
                                "claim": "x-ms-sevsnpvm-hostdata",
                                "equals": security_policy,
                            },
                            {
                                "claim": "x-ms-compliance-status",
                                "equals": "azure-compliant-uvm",
                            },
                            {"claim": "x-ms-attestation-type", "equals": "sevsnpvm"},
                        ],
                        "authority": maa_url,
                    }
                ],
                "version": "1.0.0",
            }
            with open(skr_file_path, "w") as f:
                json.dump(skr_policy, f, indent=2)

            vault_param = (
                "--hsm-name"
                if ".managedhsm.azure.net" in self._entry.storeProviderUrl.lower()
                else "--vault-name"
            )

            kv_name = urlparse(self._entry.storeProviderUrl).hostname.split(".")[0]
            az_cli(
                f"keyvault key import --name {secret_name} --pem-file {pem_file_path} "
                + f"--policy {skr_file_path} {vault_param} {kv_name} --exportable true "
                + f"--protection hsm --ops encrypt wrapKey --immutable false"
            )

            return private_key.public_key()

        finally:
            os.remove(pem_file_path)
            os.remove(skr_file_path)

    def get_secret(self, secret_name: str) -> bytes | None:
        from ._azcli_helpers import az_cli, logger

        tempdir = tempfile.gettempdir()
        pem_suffix = str(uuid.uuid4())[:8]
        pem_file_path = os.path.join(
            tempdir, f"{secret_name}-{pem_suffix}-public-key.pem"
        )
        vault_param = (
            "--hsm-name"
            if ".managedhsm.azure.net" in self._entry.storeProviderUrl.lower()
            else "--vault-name"
        )

        kv_name = urlparse(self._entry.storeProviderUrl).hostname.split(".")[0]

        key = az_cli(
            f"keyvault key list {vault_param} {kv_name} --query [?name=='{secret_name}']"
        )
        if key is None:
            logger.warning(
                f"Secret {secret_name} not found in store {self._entry.storeProviderUrl}"
            )
            return

        try:
            az_cli(
                f"keyvault key download --file {pem_file_path} --encoding PEM "
                + f"--name {secret_name} {vault_param} {kv_name}"
            )

            from cryptography.hazmat.primitives import serialization

            with open(f"{pem_file_path}", "rb") as key_file:
                return serialization.load_pem_public_key(key_file.read())

        finally:
            os.remove(pem_file_path)


class AzureSecretStore(ISecretStore):
    def __init__(self, entry: SecretStoreEntry):
        self._entry = entry

    def add_secret(
        self,
        secret_name: str,
        generate_secret: Callable,
        security_policy: str | None = None,
    ) -> bytes:
        from ._azcli_helpers import az_cli

        secret = generate_secret()
        vault_name = urlparse(self._entry.storeProviderUrl).hostname.split(".")[0]
        az_cli(
            f"keyvault secret set --name {secret_name} --vault-name {vault_name} --value {secret}"
        )

        return secret

    def get_secret(self, secret_name: str) -> bytes | None:
        from ._azcli_helpers import az_cli, logger

        tempdir = tempfile.gettempdir()
        secret_file_suffix = str(uuid.uuid4())[:8]
        secret_file_path = os.path.join(
            tempdir, f"{secret_name}-{secret_file_suffix}.bin"
        )

        kv_name = urlparse(self._entry.storeProviderUrl).hostname.split(".")[0]
        secret = az_cli(
            f"keyvault secret list --vault-name {kv_name} --query [?name=='{secret_name}']"
        )
        if secret is None:
            logger.warning(
                f"Secret {secret_name} not found in store {self._entry.storeProviderUrl}"
            )
            return
        try:

            az_cli(
                f"keyvault secret download --file {secret_file_path} --encoding base64 "
                + f"--name {secret_name} --vault-name {kv_name}"
            )

            with open(f"{secret_file_path}", "rb") as secret_file:
                return secret_file.read()
        finally:
            os.remove(secret_file_path)


class SecretStoreConfiguration:
    """
    This class is used to read and write the secret store configuration file.
    """

    @staticmethod
    def default_secretstore_config_file() -> str:
        """
        Returns the default secret store configuration file path.
        If the CLEANROOM_SECRETSTORE_CONFIG_FILE environment variable is set, it returns its value.
        If the environment variable is not set, it returns the default path for config files.
        """

        import os

        from ._configuration_helpers import get_default_config_file

        return os.environ.get(
            "CLEANROOM_SECRETSTORE_CONFIG_FILE"
        ) or get_default_config_file("secretstores.yaml")

    @staticmethod
    def load(
        config_file: str, create_if_not_existing: bool = False
    ) -> SecretStoreSpecification:
        import os

        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            read_secretstore_config,
        )

        try:
            spec = read_secretstore_config(config_file, logger)
        except FileNotFoundError:
            if (not os.path.exists(config_file)) and create_if_not_existing:
                spec = SecretStoreSpecification(secretstores=[])
            else:
                raise CLIError(
                    f"Cannot find file {config_file}. Check the --*-config parameter value."
                )

        return spec

    @staticmethod
    def store(config_file: str, config: SecretStoreSpecification):
        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            write_secretstore_config,
        )

        write_secretstore_config(config_file, config, logger)

    @staticmethod
    def get_secretstore(name, config_file) -> ISecretStore:
        try:
            secretstore_config = SecretStoreConfiguration.load(config_file)
            secretstore_entry = secretstore_config.get_secretstore_entry(name)
            secretstore = SecretStoreConfiguration._get_secretstore(secretstore_entry)
            secretstore.entry = secretstore_entry
        except CleanroomSpecificationError as e:
            if e.code == ErrorCode.SecretStoreNotFound:
                raise CLIError(
                    f"Secret store {name} not found. Run az cleanroom secretstore add first."
                )

        return secretstore

    @staticmethod
    def _get_secretstore(secretstore_entry: SecretStoreEntry) -> ISecretStore:
        from azure.cli.core.util import CLIError

        match secretstore_entry.secretStoreType:
            case SecretStoreEntry.SecretStoreType.Local_File:
                return LocalSecretStore(secretstore_entry)
            case SecretStoreEntry.SecretStoreType.Azure_KeyVault_Managed_HSM:
                return AzureSecureSecretStore(secretstore_entry)
            case SecretStoreEntry.SecretStoreType.Azure_KeyVault:
                return AzureSecretStore(secretstore_entry)
            case _:
                raise CLIError(f"Invalid type of secret store found.")
