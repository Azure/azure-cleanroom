import base64
import json

from cleanroom_common.azure_cleanroom_core.models.secretstore import (
    SecretStoreEntry,
    SecretStoreSpecification,
)

from .utilities._secretstore_helpers import SecretStoreConfiguration


def secretstore_add_cmd(
    cmd,
    secretstore_name: str,
    backingstore_type: SecretStoreEntry.SecretStoreType,
    secretstore_config_file: str = SecretStoreConfiguration.default_secretstore_config_file(),
    backingstore_id: str = "",
    backingstore_path: str = "",
    attestation_endpoint: str = "",
):
    import os

    from .utilities._azcli_helpers import az_cli, logger

    secretstore_config = SecretStoreConfiguration.load(
        secretstore_config_file, create_if_not_existing=True
    )

    exists, index, secretstore_entry = secretstore_config.check_secretstore_entry(
        secretstore_name
    )
    if exists:
        logger.warning(
            f"Secret store '{secretstore_name}' already exists ({index}):\\n{secretstore_entry}"
        )
        return

    if backingstore_type == SecretStoreEntry.SecretStoreType.Local_File:
        assert (
            backingstore_path != ""
        ), "backingstore_path is required for Local_File secret store."
        storeProviderUrl = backingstore_path
        configuration = ""
        supported_secret_types = [
            SecretStoreEntry.SupportedSecretTypes.Secret,
        ]
        if not os.path.exists(backingstore_path):
            os.makedirs(backingstore_path)
    else:
        assert backingstore_id != "", "backingstore_id is required for KeyVault."
        kv_details = az_cli(f"resource show --id {backingstore_id}")
        match backingstore_type:
            case SecretStoreEntry.SecretStoreType.Azure_KeyVault_Managed_HSM:
                if kv_details["type"] == "Microsoft.KeyVault/managedHSMs":
                    storeProviderUrl = kv_details["properties"]["hsmUri"]
                else:
                    assert (
                        kv_details["type"] == "Microsoft.KeyVault/vaults"
                    ), f"Unknown KeyVault type: {kv_details['type']}"
                    assert (
                        kv_details["properties"]["sku"]["name"].lower() == "premium"
                    ), f"Unsupported SKU for Managed HSM: {kv_details['properties']['sku']['name']}"
                    storeProviderUrl = kv_details["properties"]["vaultUri"]

                assert attestation_endpoint != "", "attestation_endpoint is required."
                configuration = base64.b64encode(
                    json.dumps({"authority": attestation_endpoint}).encode()
                ).decode()
                supported_secret_types = [
                    SecretStoreEntry.SupportedSecretTypes.Key,
                ]
            case SecretStoreEntry.SecretStoreType.Azure_KeyVault:
                assert (
                    kv_details["type"] == "Microsoft.KeyVault/vaults"
                ), f"Unknown KeyVault type: {kv_details['type']}"
                storeProviderUrl = kv_details["properties"]["vaultUri"]
                configuration = ""
                supported_secret_types = [
                    SecretStoreEntry.SupportedSecretTypes.Secret,
                ]

    secretstore_entry = SecretStoreEntry(
        name=secretstore_name,
        storeProviderUrl=storeProviderUrl,
        secretStoreType=backingstore_type,
        configuration=configuration,
        supportedSecretTypes=supported_secret_types,
    )

    secretstore_config.add_secretstore_entry(secretstore_entry)

    SecretStoreConfiguration.store(secretstore_config_file, secretstore_config)
    logger.warning(f"Secret store '{secretstore_name}' added to configuration.")
