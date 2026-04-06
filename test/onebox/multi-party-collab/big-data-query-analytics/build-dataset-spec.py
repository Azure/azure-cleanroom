#!/usr/bin/env python3
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Script to build dataset specification object for frontend service.

This script reads datastore configuration and builds the dataset input details
object that can be posted to the frontend service /publish endpoint.

Usage from PowerShell:
    $datasetSpec = python3 build-dataset-spec.py `
        --dataset-name $datasetName `
        --datastore-name $datastoreName `
        --datastore-config $datastoreConfig `
        --access-mode read `
        --allowed-fields "date,author,mentions" `
        --client-id $clientId `
        --tenant-id $tenantId `
        --dek-secret-id $dekSecretId `
        --dek-kv-url $dekKvUrl `
        --kek-secret-id $kekSecretId `
        --kek-kv-url $kekKvUrl `
        --kek-maa-url $maaUrl | ConvertFrom-Json
"""

import argparse
import base64
import json
from pathlib import Path
from typing import Optional

import yaml


def build_dataset_specification(
    dataset_name: str,
    datastore_name: str,
    datastore_config: str,
    access_mode: str,
    allowed_fields: list[str],
    identity_name: Optional[str] = None,
    client_id: Optional[str] = None,
    tenant_id: Optional[str] = None,
    issuer_url: Optional[str] = None,
    dek_secret_id: Optional[str] = None,
    dek_kv_url: Optional[str] = None,
    kek_secret_id: Optional[str] = None,
    kek_kv_url: Optional[str] = None,
    kek_maa_url: Optional[str] = None,
) -> dict:
    """
    Build dataset specification object for frontend service.

    :param dataset_name: Name of the dataset.
    :param datastore_name: Name of the datastore.
    :param datastore_config: Path to datastore config file.
    :param access_mode: Access mode (read/write).
    :param allowed_fields: List of allowed fields.
    :param identity_name: Friendly name of the identity.
    :param client_id: Azure identity client ID.
    :param tenant_id: Azure identity tenant ID.
    :param issuer_url: Token issuer URL for the identity.
    :param dek_secret_id: DEK secret ID.
    :param dek_kv_url: DEK Key Vault URL.
    :param kek_secret_id: KEK secret ID.
    :param kek_kv_url: KEK Key Vault URL.
    :param kek_maa_url: MAA URL for KEK.
    :return: Dataset specification object.
    """

    # Read the datastore configuration YAML file.
    with open(datastore_config, "r") as f:
        config = yaml.safe_load(f)

    # Find the datastore by name.
    datastore = None
    for ds in config.get("datastores", []):
        if ds.get("name") == datastore_name:
            datastore = ds
            break

    if datastore is None:
        raise ValueError(f"Datastore '{datastore_name}' not found in config file")

    # Extract schema fields.
    schema_fields = []
    for field in datastore.get("datasetSchema", {}).get("fields", []):
        schema_fields.append(
            {"fieldName": field.get("fieldName"), "fieldType": field.get("fieldType")}
        )

    # Build the base dataset specification.
    dataset_spec = {
        "name": dataset_name,
        "datasetSchema": {
            "format": datastore.get("datasetSchema", {}).get("format"),
            "fields": schema_fields,
        },
        "datasetAccessPolicy": {
            "accessMode": access_mode,
            "allowedFields": allowed_fields,
        },
        "store": {
            "containerName": datastore.get("storeName"),
            "storageAccountType": datastore.get("storeType"),
            "storageAccountUrl": datastore.get("storeProviderUrl"),
            "encryptionMode": datastore.get("encryptionMode") or "None",
        },
    }

    # Add AWS CGS secret ID from storeProviderConfiguration if present.
    store_config = datastore.get("storeProviderConfiguration")
    if store_config and store_config.strip():
        # Decode base64 configuration.
        config_json = json.loads(base64.b64decode(store_config).decode())
        if "secretId" in config_json:
            dataset_spec["store"]["awsCgsSecretId"] = config_json.get("secretId")

    # Add identity if provided.
    if client_id and tenant_id:
        dataset_spec["identity"] = {
            "name": identity_name,
            "clientId": client_id,
            "tenantId": tenant_id,
            "issuerUrl": issuer_url,
        }

    # Add DEK if provided.
    if dek_secret_id and dek_kv_url:
        dataset_spec["dek"] = {"secretId": dek_secret_id, "keyVaultUrl": dek_kv_url}

    # Add KEK if provided.
    if kek_secret_id and kek_kv_url:
        dataset_spec["kek"] = {
            "secretId": kek_secret_id,
            "keyVaultUrl": kek_kv_url,
            "maaUrl": kek_maa_url,
        }

    return dataset_spec


def main():
    parser = argparse.ArgumentParser(
        description="Build dataset specification for frontend service"
    )
    parser.add_argument("--dataset-name", required=True, help="Name of the dataset")
    parser.add_argument("--datastore-name", required=True, help="Name of the datastore")
    parser.add_argument(
        "--datastore-config", required=True, help="Path to datastore config file"
    )
    parser.add_argument(
        "--access-mode",
        required=True,
        choices=["read", "write"],
        help="Access mode (read/write)",
    )
    parser.add_argument(
        "--allowed-fields",
        required=True,
        help="Comma-separated list of allowed fields",
    )
    parser.add_argument("--identity-name", help="Friendly name of the identity")
    parser.add_argument("--client-id", help="Azure identity client ID")
    parser.add_argument("--tenant-id", help="Azure identity tenant ID")
    parser.add_argument("--issuer-url", help="Token issuer URL for the identity")
    parser.add_argument("--dek-secret-id", help="DEK secret ID")
    parser.add_argument("--dek-kv-url", help="DEK Key Vault URL")
    parser.add_argument("--kek-secret-id", help="KEK secret ID")
    parser.add_argument("--kek-kv-url", help="KEK Key Vault URL")
    parser.add_argument("--kek-maa-url", help="MAA URL for KEK")

    args = parser.parse_args()

    # Parse allowed fields.
    allowed_fields = [field.strip() for field in args.allowed_fields.split(",")]

    # Build the dataset specification.
    dataset_spec = build_dataset_specification(
        dataset_name=args.dataset_name,
        datastore_name=args.datastore_name,
        datastore_config=args.datastore_config,
        access_mode=args.access_mode,
        allowed_fields=allowed_fields,
        identity_name=args.identity_name,
        client_id=args.client_id,
        tenant_id=args.tenant_id,
        issuer_url=args.issuer_url,
        dek_secret_id=args.dek_secret_id,
        dek_kv_url=args.dek_kv_url,
        kek_secret_id=args.kek_secret_id,
        kek_kv_url=args.kek_kv_url,
        kek_maa_url=args.kek_maa_url,
    )

    # Output as JSON.
    print(json.dumps(dataset_spec, indent=2))


if __name__ == "__main__":
    main()
