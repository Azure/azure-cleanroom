import base64 as b64
import json
import logging
import os

import requests
from pydantic import BaseModel
from requests.exceptions import RequestException
from rich import print

logger = logging.getLogger("utilities")


class KekConfig(BaseModel):
    kid: str
    akvEndpoint: str
    maaEndpoint: str


class WrappedSecretConfig(BaseModel):
    clientId: str
    tenantId: str
    kid: str
    akvEndpoint: str
    kek: KekConfig


class DbConfig(BaseModel):
    dbEndpoint: str
    dbIP: str
    dbUser: str
    dbName: str
    dbPassword: WrappedSecretConfig


class CgsSecretResponse(BaseModel):
    value: str


class UnwrapSecretResponse(BaseModel):
    value: str


def get_db_config(secret_id):
    uri = f"http://localhost:8300/secrets/{secret_id}"
    logger.info(f"Getting secret from {uri}")
    try:
        response = requests.post(uri)
        response.raise_for_status()
    except requests.exceptions.HTTPError as err:
        raise Exception(
            f"GET secrets failed. status_code: {err.response.status_code}, body: {err.response.text}"
        )

    secret_value = CgsSecretResponse(**response.json())
    secret_decoded = b64.b64decode(secret_value.value).decode("utf-8")
    db_config = DbConfig.model_validate_json(secret_decoded)

    logger.info("Successfully retrieved DB config")
    print(db_config)

    return db_config


def get_db_password(db_config: DbConfig):
    uri = f"http://localhost:9300/secrets/unwrap"
    logger.info(f"Unwrapping secret from {uri}")
    unwrap_request = db_config.dbPassword
    print(unwrap_request.model_dump_json())
    try:
        response = requests.post(
            uri,
            headers={"Content-Type": "application/json"},
            data=unwrap_request.model_dump_json(),
        )
        response.raise_for_status()
    except requests.exceptions.HTTPError as err:
        raise Exception(
            f"POST secrets/unwrap failed. status_code: {err.response.status_code}, body: {err.response.text}"
        )

    unwrap_response = UnwrapSecretResponse(**response.json())
    return b64.b64decode(unwrap_response.value).decode("utf-8")
