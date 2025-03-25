# pylint: disable=line-too-long,too-many-statements,too-many-lines
# pylint: disable=too-many-return-statements
# pylint: disable=too-many-locals
# pylint: disable=protected-access
# pylint: disable=broad-except
# pylint: disable=too-many-branches
# pylint: disable=missing-timeout
# pylint: disable=missing-function-docstring
# pylint: disable=missing-module-docstring

# Note (gsinha): Various imports are also mentioned inline in the code at the point of usage.
# This is done to speed up command execution as having all the imports listed at top level is making
# execution slow for every command even if the top level imported packaged will not be used by that
# command.
import hashlib
import json
from math import e
from multiprocessing import Value
import os
import tempfile
from time import sleep
import base64
from urllib.parse import urlparse
import uuid
import shlex
import jsonschema_specifications
from knack import CLI
from knack.log import get_logger
from azure.cli.core.util import CLIError
import oras.oci
import requests
import yaml
from azure.cli.core import get_default_cli
from azure.cli.core.util import get_file_json, shell_safe_json_parse, is_guid
from .custom import response_error_message
from .custom_ccf import (
    parse_provider_config,
    get_provider_client_endpoint,
    to_security_policy_config,
    to_security_policy_option,
)

logger = get_logger(__name__)


def ccf_recovery_service_create(
    cmd,
    service_name,
    infra_type,
    key_vault,
    maa_endpoint,
    identity,
    ccf_network_join_policy,
    security_policy_creation_option,
    security_policy,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    security_policy_config = to_security_policy_config(
        security_policy_creation_option, security_policy
    )
    provider_config = parse_provider_config(provider_config, infra_type)

    ccf_network_join_policy = parse_network_join_policy(ccf_network_join_policy)
    if infra_type == "caci" and not identity:
        raise CLIError(f"--identity input is required for infra type {infra_type}")

    from .utilities._azcli_helpers import az_cli

    key_vault_url = az_cli(
        f"resource show --id {key_vault} --query properties.vaultUri"
    )
    content = {
        "infraType": infra_type,
        "akvEndpoint": key_vault_url,
        "maaEndpoint": maa_endpoint,
        "managedIdentityId": identity,
        "ccfNetworkJoinPolicy": ccf_network_join_policy,
        "securityPolicy": security_policy_config,
        "providerConfig": provider_config,
    }

    logger.warning(
        f"Run `docker compose -p {provider_client_name} logs -f` to monitor recovery service creation progress."
    )
    r = requests.post(
        f"{provider_endpoint}/recoveryservices/{service_name}/create", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_recovery_service_delete(
    cmd, service_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {"infraType": infra_type, "providerConfig": provider_config}

    r = requests.post(
        f"{provider_endpoint}/recoveryservices/{service_name}/delete", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def ccf_recovery_service_show(
    cmd, service_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/recoveryservices/{service_name}/get", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_recovery_service_api_network_show_join_policy(
    cmd, service_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    service_config = parse_svc_config(service_config)
    content = {
        "recoveryService": service_config["recoveryService"],
    }

    endpoint = f"{provider_endpoint}/recoveryservices/api/network/joinpolicy"
    r = requests.get(endpoint, json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_recovery_service_security_policy_generate(
    cmd,
    infra_type,
    security_policy_creation_option,
    ccf_network_join_policy,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    policy_option = to_security_policy_option(security_policy_creation_option)
    ccf_network_join_policy = parse_network_join_policy(ccf_network_join_policy)

    content = {
        "infraType": infra_type,
        "ccfNetworkJoinPolicy": ccf_network_join_policy,
        "securityPolicyCreationOption": policy_option,
    }

    r = requests.post(
        f"{provider_endpoint}/recoveryservices/generateSecurityPolicy", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_recovery_service_api_member_show(
    cmd, member_name, service_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    service_config = parse_svc_config(service_config)
    content = {
        "recoveryService": service_config["recoveryService"],
    }

    members_endpoint = f"{provider_endpoint}/recoveryservices/api/members"
    if member_name:
        members_endpoint = members_endpoint + f"/{member_name}"

    r = requests.get(members_endpoint, json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_recovery_service_api_member_show_report(
    cmd, member_name, service_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    service_config = parse_svc_config(service_config)
    content = {
        "recoveryService": service_config["recoveryService"],
    }

    r = requests.get(
        f"{provider_endpoint}/recoveryservices/api/{member_name}/report", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_recovery_service_api_show_report(cmd, service_config, provider_client_name):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    service_config = parse_svc_config(service_config)
    content = {
        "recoveryService": service_config["recoveryService"],
    }

    r = requests.get(f"{provider_endpoint}/recoveryservices/api/report", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def parse_svc_config(svc_config):
    if svc_config:
        if os.path.exists(svc_config):
            svc_config = get_file_json(svc_config)
        else:
            svc_config = shell_safe_json_parse(svc_config)

    if not svc_config:
        raise CLIError(f"--service-config parameter must be specified")

    return svc_config


def parse_network_join_policy(ccf_network_join_policy):
    if not ccf_network_join_policy:
        raise CLIError(f"--ccf-network-join-policy parameter must be specified")

    if os.path.exists(ccf_network_join_policy):
        ccf_network_join_policy = get_file_json(ccf_network_join_policy)
    else:
        ccf_network_join_policy = shell_safe_json_parse(ccf_network_join_policy)

    return ccf_network_join_policy
