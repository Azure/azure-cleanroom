# pylint: disable=line-too-long,too-many-statements,too-many-lines
# pylint: disable=too-many-return-statements
# pylint: disable=too-many-locals
# pylint: disable=protected-access
# pylint: disable=broad-except
# pylint: disable=too-many-branches
# pylint: disable=missing-timeout
# pylint: disable=missing-function-docstring
# pylint: disable=missing-module-docstring

import base64
import hashlib
import json
import os
import shlex
import uuid

# Note (gsinha): Various imports are also mentioned inline in the code at the point of usage.
# This is done to speed up command execution as having all the imports listed at top level is making
# execution slow for every command even if the top level imported packaged will not be used by that
# command.
from enum import StrEnum
from pathlib import Path
from time import sleep
from typing import Any
from urllib.parse import urlparse

import requests
import yaml
from azure.cli.core.util import CLIError, get_file_json, shell_safe_json_parse
from cleanroom_common.azure_cleanroom_core.models.network import *

from .config_cmd import *
from .datastore_cmd import *
from .secretstore_cmd import *
from .utilities._azcli_helpers import az_cli, logger

MCR_CLEANROOM_VERSIONS_REGISTRY = "mcr.microsoft.com/azurecleanroom"
MCR_CGS_REGISTRY = "mcr.microsoft.com/azurecleanroom"
mcr_cgs_constitution_url = f"{MCR_CGS_REGISTRY}/cgs-constitution:6.0.0"
mcr_cgs_jsapp_url = f"{MCR_CGS_REGISTRY}/cgs-js-app:6.0.0"

cgs_client_compose_file = f"{os.path.dirname(__file__)}{os.path.sep}data{os.path.sep}cgs-client{os.path.sep}docker-compose.yaml"
aspire_dashboard_compose_file = f"{os.path.dirname(__file__)}{os.path.sep}data{os.path.sep}aspire-dashboard{os.path.sep}docker-compose.yaml"
keygenerator_sh = (
    f"{os.path.dirname(__file__)}{os.path.sep}data{os.path.sep}keygenerator.sh"
)
application_yml = (
    f"{os.path.dirname(__file__)}{os.path.sep}data{os.path.sep}application.yaml"
)
default_msal_token_cache_root_dir: str = os.path.join(
    os.path.join(Path.home(), ".azcleanroom"), "token_cache"
)


def governance_client_deploy_cmd(
    cmd,
    ccf_endpoint: str,
    signing_cert_id,
    signing_cert,
    signing_key,
    use_azlogin_identity,
    use_microsoft_identity,
    use_local_identity,
    local_identity_endpoint,
    gov_client_name,
    msal_token_cache_root_dir="",
    cgs_msal_token_cache_dir="",
    service_cert="",
    service_cert_discovery_endpoint="",
    service_cert_discovery_snp_host_data="",
    service_cert_discovery_constitution_digest="",
    service_cert_discovery_jsapp_bundle_digest="",
):
    if (
        not signing_cert
        and not signing_key
        and not signing_cert_id
        and not use_azlogin_identity
        and not use_microsoft_identity
        and not use_local_identity
    ):
        raise CLIError(
            "Either (signing-cert,signing-key) or signing-cert-id/use-azlogin-identity/use-microsoft-identity/use-local-identity must be specified."
        )
    if signing_cert_id:
        if (
            signing_cert
            or signing_key
            or use_azlogin_identity
            or use_microsoft_identity
            or use_local_identity
        ):
            raise CLIError(
                "signing-cert/signing-key/use-azlogin-identity/use-microsoft-identity/use-local-identity cannot be specified along with signing-cert-id."
            )
        if os.path.exists(signing_cert_id):
            with open(signing_cert_id, "r") as f:
                signing_cert_id = f.read()
    elif use_azlogin_identity or use_microsoft_identity or use_local_identity:
        if signing_cert or signing_key or signing_cert_id:
            raise CLIError(
                "signing-cert/signing-key/signing_cert_id cannot be specified along with use-azlogin-identity/use-microsoft-identity/use-local-identity."
            )
    else:
        if not signing_cert or not signing_key:
            raise CLIError("Both signing-cert and signing-key must be specified.")

        if not os.path.exists(signing_cert):
            raise CLIError(f"File {signing_cert} does not exist.")

        if not os.path.exists(signing_key):
            raise CLIError(f"File {signing_key} does not exist.")

    if use_local_identity:
        if not local_identity_endpoint:
            raise CLIError(
                "local-identity-endpoint must be specified with use-local-identity."
            )

    if service_cert:
        if service_cert_discovery_endpoint or service_cert_discovery_snp_host_data:
            raise CLIError(
                "service-cert-discovery-endpoint/service-cert-snp-host-data cannot be specified along with service-cert."
            )
    else:
        if (
            not service_cert_discovery_endpoint
            or not service_cert_discovery_snp_host_data
        ):
            raise CLIError(
                "service-cert or [service-cert-discovery-endpoint and service-cert-snp-host-data] must be specified."
            )

    from python_on_whales import DockerClient

    compose_profiles = []
    if signing_cert_id or use_azlogin_identity:
        compose_profiles = ["creds-proxy"]

    if use_microsoft_identity:
        from pathlib import Path

        msal_token_cache_root_dir = (
            msal_token_cache_root_dir or default_msal_token_cache_root_dir
        )
        msal_token_cache_dir = os.path.join(msal_token_cache_root_dir, gov_client_name)
        if not os.path.exists(msal_token_cache_dir):
            os.makedirs(msal_token_cache_dir)
        ms_perform_device_code_flow(msal_token_cache_dir)
        cgs_msal_token_cache_dir = cgs_msal_token_cache_dir or msal_token_cache_dir
        os.environ["AZCLI_CGS_MSAL_TOKEN_CACHE_DIR"] = cgs_msal_token_cache_dir

    if use_local_identity:
        os.environ["AZCLI_CGS_LOCAL_IDP_ENDPOINT"] = local_identity_endpoint
    else:
        os.environ["AZCLI_CGS_LOCAL_IDP_ENDPOINT"] = ""

    docker = DockerClient(
        compose_files=[cgs_client_compose_file],
        compose_project_name=gov_client_name,
        compose_profiles=compose_profiles,
    )

    uid = os.getuid()
    gid = os.getgid()
    os.environ["AZCLI_CCF_PROVIDER_UID"] = str(uid)
    os.environ["AZCLI_CCF_PROVIDER_GID"] = str(gid)

    if "AZCLI_CGS_CLIENT_IMAGE" in os.environ:
        image = os.environ["AZCLI_CGS_CLIENT_IMAGE"]
        logger.warning(f"Using cgs-client image from override url: {image}")

    docker.compose.up(remove_orphans=True, detach=True)

    import time

    timeout = 300  # 5 minutes from now
    timeout_start = time.time()
    started = False
    while time.time() < timeout_start + timeout:
        try:
            (_, port) = docker.compose.port(service="cgs-client", private_port=8080)
            (_, uiport) = docker.compose.port(service="cgs-ui", private_port=6300)
            cgs_endpoint = f"http://localhost:{port}"
            r = requests.get(f"{cgs_endpoint}/ready")
            if r.status_code == 200:
                started = True
                break
            elif r.status_code == 404:
                logger.warning(
                    f"Restarting cgs-client container as its reporting unexpected status code 404 for /ready endpoint..."
                )
                docker.compose.down()
                docker.compose.up(remove_orphans=True, detach=True)
            else:
                logger.warning(
                    f"Waiting for cgs-client endpoint to be up... (status code: {r.status_code})"
                )
                sleep(5)
        except:
            logger.warning("Waiting for cgs-client endpoint to be up...")
            sleep(5)

    if not started:
        raise CLIError(
            f"Hit timeout waiting for cgs-client endpoint to be up on localhost:{port}"
        )

    authMode = (
        "AzureLogin"
        if use_azlogin_identity
        else (
            "MsLogin"
            if use_microsoft_identity
            else "LocalIdp" if use_local_identity else None
        )
    )

    form_data = {
        "CcfEndpoint": (None, ccf_endpoint),
        "SigningCertId": (None, signing_cert_id),
        "AuthMode": (None, authMode),
        "ServiceCertDiscovery": (
            (
                None,
                json.dumps(
                    {
                        "CertificateDiscoveryEndpoint": service_cert_discovery_endpoint,
                        "HostData": [service_cert_discovery_snp_host_data],
                        "ConstitutionDigest": service_cert_discovery_constitution_digest,
                        "JsAppBundleDigest": service_cert_discovery_jsapp_bundle_digest,
                        "SkipDigestCheck": service_cert_discovery_constitution_digest
                        is None
                        and service_cert_discovery_jsapp_bundle_digest is None,
                    }
                    if service_cert_discovery_endpoint
                    else None
                ),
                "application/json",
            )
            if service_cert_discovery_endpoint
            else None
        ),
    }
    files = dict(form_data)
    if signing_cert_id is None and authMode is None:
        files["SigningCertPemFile"] = ("SigningCertPemFile", open(signing_cert, "rb"))
        files["SigningKeyPemFile"] = ("SigningKeyPemFile", open(signing_key, "rb"))

    if service_cert != "":
        files["ServiceCertPemFile"] = ("ServiceCertPemFile", open(service_cert, "rb"))

    r = requests.post(f"{cgs_endpoint}/configure", files=files)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))

    logger.warning(
        "cgs-client container is listening on %s. Open CGS UI at http://localhost:%s.",
        port,
        uiport,
    )


def governance_client_remove_cmd(cmd, gov_client_name):
    from python_on_whales import DockerClient

    gov_client_name = get_gov_client_name(cmd.cli_ctx, gov_client_name)
    docker = DockerClient(
        compose_files=[cgs_client_compose_file], compose_project_name=gov_client_name
    )
    docker.compose.down()


def governance_client_show_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/show")
    if r.status_code == 204:
        return "{}"

    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_client_get_access_token_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/identity/accessToken")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_client_version_cmd(cmd, gov_client_name=""):
    gov_client_name = get_gov_client_name(cmd.cli_ctx, gov_client_name)
    digest = get_cgs_client_digest(gov_client_name)
    version = try_get_cgs_client_version(digest)

    return {
        "cgs-client": {
            "digest": digest,
            "version": version,
        }
    }


def governance_client_get_upgrades_cmd(cmd, gov_client_name=""):
    gov_client_name = get_gov_client_name(cmd.cli_ctx, gov_client_name)
    digest = get_cgs_client_digest(gov_client_name)
    cgs_client_version = find_cgs_client_version_entry(digest)
    if cgs_client_version == None:
        raise CLIError(
            f"Could not identify version for cgs-client container image: {digest}."
        )

    latest_tag = os.environ.get("AZCLI_CGS_CLIENT_LATEST_TAG", "latest")
    latest_cgs_client_version = find_cgs_client_version_entry(latest_tag)
    from packaging.version import Version

    upgrades = []
    current_version = Version(cgs_client_version)
    if (
        latest_cgs_client_version != None
        and Version(latest_cgs_client_version) > current_version
    ):
        upgrades.append({"clientVersion": latest_cgs_client_version})

    return {"clientVersion": str(current_version), "upgrades": upgrades}


def governance_client_show_deployment_cmd(cmd, gov_client_name=""):
    from python_on_whales import DockerClient, exceptions

    gov_client_name = get_gov_client_name(cmd.cli_ctx, gov_client_name)
    docker = DockerClient(
        compose_files=[cgs_client_compose_file], compose_project_name=gov_client_name
    )
    try:
        (_, port) = docker.compose.port(service="cgs-client", private_port=8080)
        (_, uiport) = docker.compose.port(service="cgs-ui", private_port=6300)

    except exceptions.DockerException as e:
        raise CLIError(
            f"Not finding a client instance running with name '{gov_client_name}'. "
            + f"Check the --governance-client parameter value."
        ) from e

    return {
        "projectName": gov_client_name,
        "ports": {"cgs-client": port, "cgs-ui": uiport},
        "uiLink": f"http://localhost:{uiport}",
    }


def governance_service_deploy_cmd(cmd, gov_client_name):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)

    # Download the constitution and js_app to deploy.
    dir_path = os.path.dirname(os.path.realpath(__file__))
    bin_folder = os.path.join(dir_path, "bin")
    if not os.path.exists(bin_folder):
        os.makedirs(bin_folder)

    constitution, bundle = download_constitution_jsapp(bin_folder)

    # Submit and accept set_constitution proposal.
    logger.warning("Deploying constitution on CCF")
    content = {
        "actions": [
            {
                "name": "set_constitution",
                "args": {"constitution": constitution},
            }
        ]
    }
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(
            f"set_constitution proposal failed with status: {r.status_code} and response: {r.text}"
        )

    # A set_constitution proposal might already be accepted if the default constitution was
    # unconditionally accepting proposals. So only vote if not already accepted.
    if r.json()["proposalState"] != "Accepted":
        proposal_id = r.json()["proposalId"]
        r = requests.post(f"{cgs_endpoint}/proposals/{proposal_id}/ballots/vote_accept")
        if r.status_code != 200:
            raise CLIError(
                f"set_constitution proposal acceptance failed with status: {r.status_code} and "
                + f"response: {r.text}"
            )
        if r.json()["proposalState"] == "Open":
            logger.warning(
                "set_constitution proposal %s remains open. "
                + "Other members need to vote their acceptance for changes to take affect.",
                proposal_id,
            )
        elif r.json()["proposalState"] == "Rejected":
            raise CLIError(f"set_constitution proposal {proposal_id} was rejected")

    # Submit and accept set_js_runtime_options proposal.
    logger.warning("Configuring js runtime options on CCF")
    content = {
        "actions": [
            {
                "name": "set_js_runtime_options",
                "args": {
                    "max_heap_bytes": 104857600,
                    "max_stack_bytes": 1048576,
                    "max_execution_time_ms": 1000,
                    "log_exception_details": True,
                    "return_exception_details": True,
                },
            }
        ]
    }
    r = requests.post(
        f"{cgs_endpoint}/proposals/create", json=content
    )  # [missing-timeout]
    if r.status_code != 200:
        raise CLIError(
            f"set_js_runtime_options proposal failed with status: {r.status_code} and response: {r.text}"
        )

    proposal_id = r.json()["proposalId"]
    r = requests.post(f"{cgs_endpoint}/proposals/{proposal_id}/ballots/vote_accept")
    if r.status_code != 200:
        raise CLIError(
            f"set_js_runtime_options proposal acceptance failed with status: {r.status_code} "
            + f"and response: {r.text}"
        )

    # Submit and accept set_js_app proposal.
    logger.warning("Deploying governance service js application on CCF")
    content = {
        "actions": [
            {
                "name": "set_js_app",
                "args": {"bundle": bundle},
            }
        ]
    }
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(
            f"set_js_app proposal failed with status: {r.status_code} and response: {r.text}"
        )

    proposal_id = r.json()["proposalId"]
    r = requests.post(f"{cgs_endpoint}/proposals/{proposal_id}/ballots/vote_accept")
    if r.status_code != 200:
        raise CLIError(
            f"set_js_app proposal acceptance failed with status: {r.status_code} and response: {r.text}"
        )
    if r.json()["proposalState"] == "Open":
        logger.warning(
            "set_js_app proposal %s remains open. "
            + "Other members need to vote their acceptance for changes to take affect.",
            proposal_id,
        )
    elif r.json()["proposalState"] == "Rejected":
        raise CLIError(f"set_js_app proposal {proposal_id} was rejected")

    # Enable the OIDC issuer by default as its required for mainline scenarios.
    r = governance_oidc_issuer_show_cmd(cmd, gov_client_name)
    if r["enabled"] != True:
        logger.warning("Enabling OIDC Issuer capability")
        r = governance_oidc_issuer_propose_enable_cmd(cmd, gov_client_name)
        proposal_id = r["proposalId"]
        r = requests.post(f"{cgs_endpoint}/proposals/{proposal_id}/ballots/vote_accept")
        if r.status_code != 200:
            raise CLIError(
                f"enable_oidc_issuer proposal acceptance failed with status: {r.status_code} and response: {r.text}"
            )
        if r.json()["proposalState"] == "Open":
            logger.warning(
                "enable_oidc_issuer proposal %s remains open. "
                + "Other members need to vote their acceptance for changes to take affect.",
                proposal_id,
            )
        elif r.json()["proposalState"] == "Rejected":
            raise CLIError(f"enable_oidc_issuer proposal {proposal_id} was rejected")

        governance_oidc_issuer_generate_signing_key_cmd(cmd, gov_client_name)
    else:
        logger.warning("OIDC Issuer capability is already enabled")


def governance_service_version_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    _, current_constitution_hash = get_current_constitution(cgs_endpoint)
    (_, _, _, canonical_current_jsapp_bundle_hash) = get_current_jsapp_bundle(
        cgs_endpoint
    )
    constitution_version = try_get_constitution_version(current_constitution_hash)
    jsapp_version = try_get_jsapp_version(canonical_current_jsapp_bundle_hash)

    return {
        "constitution": {
            "digest": f"sha256:{current_constitution_hash}",
            "version": constitution_version,
        },
        "jsapp": {
            "digest": f"sha256:{canonical_current_jsapp_bundle_hash}",
            "version": jsapp_version,
        },
    }


def governance_service_get_upgrades_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)

    _, current_constitution_hash = get_current_constitution(cgs_endpoint)
    (_, _, _, canonical_current_jsapp_bundle_hash) = get_current_jsapp_bundle(
        cgs_endpoint
    )
    upgrades = []
    constitution_version, upgrade = constitution_digest_to_version_info(
        current_constitution_hash
    )
    if upgrade != None:
        upgrades.append(upgrade)

    jsapp_version, upgrade = bundle_digest_to_version_info(
        canonical_current_jsapp_bundle_hash
    )
    if upgrade != None:
        upgrades.append(upgrade)

    return {
        "constitutionVersion": constitution_version,
        "jsappVersion": jsapp_version,
        "upgrades": upgrades,
    }


def governance_service_upgrade_constitution_cmd(
    cmd,
    constitution_version="",
    constitution_url="",
    gov_client_name="",
):
    if constitution_version and constitution_url:
        raise CLIError(
            "Both constitution_version and constitution_url cannot be specified together."
        )

    if constitution_version:
        constitution_url = f"{MCR_CGS_REGISTRY}/cgs-constitution:{constitution_version}"

    if not constitution_url:
        raise CLIError("constitution_version must be specified")

    updates = governance_service_upgrade_status_cmd(cmd, gov_client_name)
    for index, x in enumerate(updates["proposals"]):
        if x["actionName"] == "set_constitution":
            raise CLIError(
                "Open constitution proposal(s) already exist. Use 'az cleanroom governance "
                + f"service upgrade status' command to see pending proposals and "
                + f"approve/withdraw them to submit a new upgrade proposal."
            )

    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)

    dir_path = os.path.dirname(os.path.realpath(__file__))
    bin_folder = os.path.join(dir_path, "bin")
    if not os.path.exists(bin_folder):
        os.makedirs(bin_folder)

    constitution = download_constitution(bin_folder, constitution_url)
    content = {
        "actions": [
            {
                "name": "set_constitution",
                "args": {"constitution": constitution},
            }
        ]
    }
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(
            f"set_constitution proposal failed with status: {r.status_code} and response: {r.text}"
        )

    return r.json()


def governance_service_upgrade_js_app_cmd(
    cmd,
    js_app_version="",
    js_app_url="",
    gov_client_name="",
):
    if js_app_version and js_app_url:
        raise CLIError(
            "Both js_app_version and jsapp_url cannot be specified together."
        )

    if js_app_version:
        js_app_url = f"{MCR_CGS_REGISTRY}/cgs-js-app:{js_app_version}"

    if not js_app_url:
        raise CLIError("jsapp_version must be specified")

    updates = governance_service_upgrade_status_cmd(cmd, gov_client_name)
    for index, x in enumerate(updates["proposals"]):
        if x["actionName"] == "set_js_app":
            raise CLIError(
                "Open jsapp proposal(s) already exist. Use 'az cleanroom governance service "
                + f"upgrade status' command to see pending proposals and approve/withdraw "
                + f"them to submit a new upgrade proposal."
            )

    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)

    dir_path = os.path.dirname(os.path.realpath(__file__))
    bin_folder = os.path.join(dir_path, "bin")
    if not os.path.exists(bin_folder):
        os.makedirs(bin_folder)

    bundle = download_jsapp(bin_folder, js_app_url)
    content = {
        "actions": [
            {
                "name": "set_js_app",
                "args": {"bundle": bundle},
            }
        ]
    }
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(
            f"set_js_app proposal failed with status: {r.status_code} and response: {r.text}"
        )

    return r.json()


def governance_service_upgrade_status_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/checkUpdates")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_contract_create_cmd(
    cmd, contract_id, data, gov_client_name="", version=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)

    contract = {"version": version, "data": data}
    r = requests.put(f"{cgs_endpoint}/contracts/{contract_id}", json=contract)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def governance_contract_show_cmd(cmd, gov_client_name="", contract_id=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/contracts/{contract_id}")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_contract_propose_cmd(cmd, contract_id, version, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    data = {"version": version}
    r = requests.post(f"{cgs_endpoint}/contracts/{contract_id}/propose", json=data)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_contract_vote_cmd(
    cmd, contract_id, proposal_id, action, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    data = {"proposalId": proposal_id}
    vote_method = "vote_accept" if action == "accept" else "vote_reject"
    r = requests.post(
        f"{cgs_endpoint}/contracts/{contract_id}/{vote_method}", json=data
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_identity_add_cmd(
    cmd,
    object_id,
    tenant_id,
    identifier,
    account_type,
    accepted_invitation_id,
    gov_client_name="",
):
    if object_id and accepted_invitation_id:
        raise CLIError(
            "Both object_id and accepted_invitation_id cannot be specified together."
        )

    content = {}
    if object_id:
        content["objectId"] = object_id
        if not tenant_id:
            raise CLIError("Tenant Id must be specified with object id.")
        content["tenantId"] = tenant_id
        if not identifier:
            raise CLIError("Identifier must be specified with object id.")
        content["identifier"] = identifier
        if not account_type:
            raise CLIError("Account type must be specified with object id.")
        if account_type == "microsoft":
            content["accountType"] = "microsoft"
        else:
            raise CLIError("--account-type value not handled.")
    elif accepted_invitation_id:
        if tenant_id:
            raise CLIError("Tenant Id cannot be specified with accepted invitation id.")
        if identifier:
            raise CLIError(
                "Identifier cannot be specified with accepted invitation id."
            )
        if account_type:
            raise CLIError(
                "Account type cannot be specified with accepted invitation id."
            )
        content["acceptedInvitationId"] = accepted_invitation_id
    else:
        raise CLIError("Either object_id or accepted_invitation_id must be specified.")

    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/users/identities/add", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_identity_remove_cmd(cmd, object_id, gov_client_name=""):
    content = {"actions": [{"name": "remove_user_identity", "args": {"id": object_id}}]}
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_identity_show_cmd(cmd, identity_id="", gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/users/identities/{identity_id}")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_identity_invitation_create_cmd(
    cmd,
    invitation_id,
    account_type,
    username,
    identity_type,
    tenant_id,
    gov_client_name="",
):
    if identity_type == "service-principal" and not tenant_id:
        raise CLIError("Tenant Id must be specified with service principal type.")

    content = {
        "invitationId": invitation_id,
        "userName": username,
        "identityType": (
            "ServicePrincipal" if identity_type == "service-principal" else "User"
        ),
        "tenantId": tenant_id,
    }

    if account_type == "microsoft":
        content["accountType"] = "microsoft"
    else:
        raise CLIError("--account-type value not handled.")

    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/users/invitations/propose", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_identity_invitation_accept_cmd(
    cmd, invitation_id, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/users/invitations/{invitation_id}/accept")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def governance_user_identity_invitation_show_cmd(
    cmd, invitation_id="", gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/users/invitations/{invitation_id}")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_proposal_create_cmd(cmd, content, gov_client_name=""):
    if os.path.exists(content):
        content = get_file_json(content)
    else:
        content = shell_safe_json_parse(content)

    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_proposal_list_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/proposals")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_proposal_show_cmd(cmd, proposal_id, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/proposals/{proposal_id}")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_proposal_show_actions_cmd(cmd, proposal_id, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/proposals/{proposal_id}/actions")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_proposal_vote_cmd(cmd, proposal_id, action, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    vote_method = "vote_accept" if action == "accept" else "vote_reject"
    r = requests.post(f"{cgs_endpoint}/proposals/{proposal_id}/ballots/{vote_method}")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_proposal_withdraw_cmd(cmd, proposal_id, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/{proposal_id}/withdraw")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_deployment_generate_cmd(
    cmd,
    contract_id,
    output_dir,
    security_policy_creation_option,
    gov_client_name="",
):
    generate_security_policy_creation_option = (
        security_policy_creation_option == "generate"
        or security_policy_creation_option == "generate-debug"
    )
    if not os.path.exists(output_dir):
        raise CLIError(f"Output folder location {output_dir} does not exist.")

    from .utilities._helpers import get_deployment_template_internal

    contract = governance_contract_show_cmd(cmd, gov_client_name, contract_id)
    contract_yaml = yaml.safe_load(contract["data"])
    cleanroomSpec = CleanRoomSpecification(**contract_yaml)
    _validate_config(cleanroomSpec)
    ccf_details = governance_client_show_cmd(cmd, gov_client_name)
    ssl_cert = ccf_details["serviceCert"]
    ssl_cert_base64 = base64.b64encode(bytes(ssl_cert, "utf-8")).decode("utf-8")

    arm_template, policy_json, policy_rego = get_deployment_template_internal(
        cleanroomSpec,
        contract_id,
        ccf_details["ccfEndpoint"],
        ssl_cert_base64,
        security_policy_creation_option,
    )

    with open(output_dir + f"{os.path.sep}cleanroom-policy-in.json", "w") as f:
        f.write(json.dumps(policy_json, indent=2))

    if generate_security_policy_creation_option:
        cmd = (
            f"confcom acipolicygen -i {output_dir}{os.path.sep}cleanroom-policy-in.json "
            + f"--outraw-pretty-print -s {output_dir}{os.path.sep}cleanroom-policy.rego"
        )

        if security_policy_creation_option == "generate-debug":
            cmd += " --debug-mode"
        result = az_cli(cmd)
        print(f"Result: {result}")
    else:
        assert (
            security_policy_creation_option == "allow-all"
            or security_policy_creation_option == "cached"
            or security_policy_creation_option == "cached-debug"
        ), f"Invalid security policy creation option passed: {security_policy_creation_option}"
        with open(output_dir + f"{os.path.sep}cleanroom-policy.rego", "w") as f:
            f.write(policy_rego)

    with open(f"{output_dir}{os.path.sep}cleanroom-policy.rego", "r") as f:
        cce_policy = f.read()

    cce_policy_base64 = base64.b64encode(bytes(cce_policy, "utf-8")).decode("utf-8")
    cce_policy_hash = hashlib.sha256(bytes(cce_policy, "utf-8")).hexdigest()

    arm_template["resources"][0]["properties"]["confidentialComputeProperties"][
        "ccePolicy"
    ] = cce_policy_base64

    with open(output_dir + f"{os.path.sep}cleanroom-arm-template.json", "w") as f:
        f.write(json.dumps(arm_template, indent=2))

    governance_policy_json = {
        "type": "add",
        "claims": {
            "x-ms-sevsnpvm-is-debuggable": False,
            "x-ms-sevsnpvm-hostdata": cce_policy_hash,
        },
    }

    with open(output_dir + f"{os.path.sep}cleanroom-governance-policy.json", "w") as f:
        f.write(json.dumps(governance_policy_json, indent=2))


def governance_deployment_template_propose_cmd(
    cmd, contract_id, template_file, gov_client_name=""
):
    if not os.path.exists(template_file):
        raise CLIError(
            f"File {template_file} not found. Check the input parameter value."
        )

    with open(template_file, encoding="utf-8") as f:
        template_json = json.loads(f.read())

    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(
        f"{cgs_endpoint}/contracts/{contract_id}/deploymentspec/propose",
        json=template_json,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_deployment_template_show_cmd(cmd, contract_id, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/contracts/{contract_id}/deploymentspec")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_deployment_policy_propose_cmd(
    cmd, contract_id, allow_all=None, policy_file="", gov_client_name=""
):
    if not allow_all and policy_file == "":
        raise CLIError("Either --policy-file or --allow-all flag must be specified")

    if allow_all and policy_file != "":
        raise CLIError(
            "Both --policy-file and --allow-all cannot be specified together"
        )

    if allow_all:
        policy_json = {
            "type": "add",
            "claims": {
                "x-ms-sevsnpvm-is-debuggable": False,
                "x-ms-sevsnpvm-hostdata": "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20",
            },
        }
    else:
        if not os.path.exists(policy_file):
            raise CLIError(
                f"File {policy_file} not found. Check the input parameter value."
            )

        with open(policy_file, encoding="utf-8") as f:
            policy_json = json.loads(f.read())

    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(
        f"{cgs_endpoint}/contracts/{contract_id}/cleanroompolicy/propose",
        json=policy_json,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_deployment_policy_show_cmd(cmd, contract_id, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/contracts/{contract_id}/cleanroompolicy")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_oidc_issuer_propose_enable_cmd(cmd, gov_client_name=""):
    content = {
        "actions": [{"name": "enable_oidc_issuer", "args": {"kid": uuid.uuid4().hex}}]
    }
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_oidc_issuer_generate_signing_key_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/oidc/generateSigningKey")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_oidc_issuer_propose_rotate_signing_key_cmd(cmd, gov_client_name=""):
    content = {
        "actions": [{"name": "oidc_issuer_enable_rotate_signing_key", "args": {}}]
    }
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_oidc_issuer_set_issuer_url_cmd(cmd, url, gov_client_name=""):
    content = {"url": url}
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/oidc/setIssuerUrl", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def governance_oidc_issuer_propose_set_issuer_url_cmd(cmd, url, gov_client_name=""):
    content = {
        "actions": [{"name": "set_oidc_issuer_url", "args": {"issuer_url": url}}]
    }
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_oidc_issuer_show_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/oidc/issuerInfo")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_ca_propose_enable_cmd(cmd, contract_id, gov_client_name=""):
    content = {"actions": [{"name": "enable_ca", "args": {"contractId": contract_id}}]}
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_ca_generate_key_cmd(cmd, contract_id, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/contracts/{contract_id}/ca/generateSigningKey")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_ca_propose_rotate_key_cmd(cmd, contract_id, gov_client_name=""):
    content = {
        "actions": [
            {
                "name": "ca_enable_rotate_signing_key",
                "args": {"contractId": contract_id},
            }
        ]
    }
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_ca_show_cmd(cmd, contract_id, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/contracts/{contract_id}/ca/info")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_contract_runtime_option_get_cmd(
    cmd, contract_id, option_name, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(
        f"{cgs_endpoint}/contracts/{contract_id}/checkstatus/{option_name}"
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_contract_runtime_option_set_cmd(
    cmd, contract_id, option_name, action, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/contracts/{contract_id}/{option_name}/{action}")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def governance_contract_runtime_option_propose_cmd(
    cmd, contract_id, option_name, action, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(
        f"{cgs_endpoint}/contracts/{contract_id}/{option_name}/propose-{action}"
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_contract_secret_set_cmd(
    cmd, contract_id, secret_name, value, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    content = {"value": value}
    r = requests.put(
        f"{cgs_endpoint}/contracts/{contract_id}/secrets/{secret_name}", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_contract_secret_list_cmd(cmd, contract_id, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/contracts/{contract_id}/secrets")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_contract_secret_get_cleanroom_policy_cmd(
    cmd, contract_id, secret_name, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(
        f"{cgs_endpoint}/contracts/{contract_id}/secrets/{secret_name}/cleanroompolicy"
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_contract_event_list_cmd(
    cmd, contract_id, all=None, event_id="", scope="", gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    query_url = f"{cgs_endpoint}/contracts/{contract_id}/events"
    query = ""
    if event_id != "":
        query += f"&id=all{event_id}"
    if scope != "":
        query += f"&scope={scope}"
    if all:
        query += "&from_seqno=1"

    if query != "":
        query_url += f"?{query}"

    r = requests.get(f"{query_url}")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_member_document_create_cmd(
    cmd, document_id, contract_id, data, gov_client_name="", version=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    document = {"version": version, "contractId": contract_id, "data": data}
    r = requests.put(f"{cgs_endpoint}/memberdocuments/{document_id}", json=document)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def governance_member_document_show_cmd(cmd, gov_client_name="", document_id=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/memberdocuments/{document_id}")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_member_document_propose_cmd(
    cmd, document_id, version, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    data = {"version": version}
    r = requests.post(
        f"{cgs_endpoint}/memberdocuments/{document_id}/propose", json=data
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_member_document_vote_cmd(
    cmd, document_id, proposal_id, action, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    data = {"proposalId": proposal_id}
    vote_method = "vote_accept" if action == "accept" else "vote_reject"
    r = requests.post(
        f"{cgs_endpoint}/memberdocuments/{document_id}/{vote_method}", json=data
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_document_create_cmd(
    cmd, document_id, contract_id, data, approvers="", gov_client_name="", version=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    document = {
        "version": version,
        "contractId": contract_id,
        "data": data,
        "approvers": [],
    }

    user_document_approvers = []
    if approvers:
        if os.path.exists(approvers):
            user_document_approvers = get_file_json(approvers)
        else:
            user_document_approvers = shell_safe_json_parse(approvers)
    for approver in user_document_approvers:
        approverId = approver["id"]
        approverType = approver["type"]
        if approverType not in ["user", "member"]:
            raise CLIError(
                f"Invalid approver type {approverType} for approver {approverId}. Valid types are 'user' or 'member'."
            )
        document["approvers"].append(
            {"approverId": approverId, "approverIdType": approverType}
        )
    r = requests.put(f"{cgs_endpoint}/userdocuments/{document_id}", json=document)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def governance_user_document_show_cmd(cmd, gov_client_name="", document_id=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/userdocuments/{document_id}")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_document_propose_cmd(cmd, document_id, version, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    data = {"version": version}
    r = requests.post(f"{cgs_endpoint}/userdocuments/{document_id}/propose", json=data)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_document_vote_cmd(
    cmd, document_id, proposal_id, action, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    data = {"proposalId": proposal_id}
    vote_method = "vote_accept" if action == "accept" else "vote_reject"
    r = requests.post(
        f"{cgs_endpoint}/userdocuments/{document_id}/{vote_method}", json=data
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_document_runtime_option_get_cmd(
    cmd, document_id, option_name, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(
        f"{cgs_endpoint}/userdocuments/{document_id}/checkstatus/{option_name}"
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_user_document_runtime_option_set_cmd(
    cmd, document_id, option_name, action, gov_client_name=""
):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(
        f"{cgs_endpoint}/userdocuments/{document_id}/runtimeoptions/{option_name}/{action}"
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def governance_network_set_recovery_threshold_cmd(
    cmd, recovery_threshold, gov_client_name=""
):
    content = {
        "actions": [
            {
                "name": "set_recovery_threshold",
                "args": {"recovery_threshold": int(recovery_threshold)},
            }
        ]
    }

    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_network_show_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/network/show")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_member_add_cmd(
    cmd,
    identifier,
    certificate,
    encryption_public_key,
    recovery_role,
    tenant_id,
    member_data,
    gov_client_name="",
):
    if not os.path.exists(certificate):
        raise CLIError(f"File {certificate} does not exist.")

    if recovery_role and not encryption_public_key:
        raise CLIError(
            f"--recovery-role can only be specified along with --encryption-public-key."
        )

    if member_data:
        if identifier:
            raise CLIError(
                f"Both --identifier and --member-data cannot be specified together. Specify identifier property within the member data JSON."
            )
        if tenant_id:
            raise CLIError(
                f"Both --tenant-id and --member-data cannot be specified together. Specify tenant_id property within the member data JSON."
            )
        if os.path.exists(member_data):
            member_data = get_file_json(member_data)
        else:
            member_data = shell_safe_json_parse(member_data)
    else:
        if not identifier:
            raise CLIError(f"--identifier must be specified.")
        member_data = {"identifier": identifier}
        if tenant_id != "":
            member_data["tenantId"] = tenant_id

    encryption_public_key_pem = ""
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    with open(certificate, encoding="utf-8") as f:
        cert_pem = f.read()
    if encryption_public_key:
        with open(encryption_public_key, encoding="utf-8") as f:
            encryption_public_key_pem = f.read()

    args = {
        "cert": cert_pem,
        "member_data": member_data,
    }
    if encryption_public_key_pem:
        args["encryption_pub_key"] = encryption_public_key_pem
        if recovery_role:
            args["recovery_role"] = (
                "Owner" if recovery_role == "owner" else "Participant"
            )

    content = {
        "actions": [
            {
                "name": "set_member",
                "args": args,
            }
        ]
    }

    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_member_set_tenant_id_cmd(cmd, identifier, tenant_id, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    member_data = {"identifier": identifier, "tenantId": tenant_id}
    members = governance_member_show_cmd(cmd, gov_client_name)
    member = [
        x
        for x in members["value"]
        if "identifier" in x["memberData"]
        and x["memberData"]["identifier"] == identifier
    ]
    if len(member) == 0:
        raise CLIError(f"Member with identifier {identifier} was not found.")

    content = {
        "actions": [
            {
                "name": "set_member_data",
                "args": {
                    "member_id": member[0]["memberId"],
                    "member_data": member_data,
                },
            }
        ]
    }

    r = requests.post(f"{cgs_endpoint}/proposals/create", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_member_activate_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.post(f"{cgs_endpoint}/members/statedigests/ack")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def governance_member_show_cmd(cmd, gov_client_name=""):
    cgs_endpoint = get_cgs_client_endpoint(cmd, gov_client_name)
    r = requests.get(f"{cgs_endpoint}/members")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def governance_member_keygeneratorsh_cmd(cmd):
    with open(keygenerator_sh, encoding="utf-8") as f:
        print(f.read())


def governance_member_get_default_certificate_policy_cmd(cmd, member_name):
    policy = get_default_cert_policy(member_name)
    print(policy)


def governance_member_generate_identity_certificate_cmd(
    cmd, member_name, vault_name, output_dir
):
    cert_policy = get_default_cert_policy(member_name)
    cert_policy_file = output_dir + f"{os.path.sep}cert-policy.json"
    with open(cert_policy_file, "w") as f:
        f.write(cert_policy)
    cert_name = f"{member_name}-identity"
    logger.warning(
        f"Generating identity private key and certificate for participant '{member_name}' in Azure Key Vault..."
    )
    az_cli(
        f"keyvault certificate create --name {cert_name} --vault-name {vault_name} --policy @{cert_policy_file}"
    )
    cert_pem_file = output_dir + f"{os.path.sep}{member_name}_cert.pem"
    if os.path.exists(cert_pem_file):
        os.remove(cert_pem_file)
    az_cli(
        f"keyvault certificate download --name {cert_name} --vault-name {vault_name} --file {cert_pem_file} --encoding PEM"
    )
    cert_id = az_cli(
        f"keyvault certificate show --name {cert_name} --vault-name {vault_name} --query id --output tsv"
    )
    cert_id_file = output_dir + f"{os.path.sep}{member_name}_cert.id"
    with open(cert_id_file, "w") as f:
        f.write(str(cert_id))
    logger.warning(
        f"Identity certificate generated at: {cert_pem_file} (to be registered in CCF)"
    )
    logger.warning(
        f"Identity certificate Azure Key Vault Id written out at: {cert_id_file}"
    )


def governance_member_generate_encryption_key_cmd(
    cmd, member_name, vault_name, output_dir
):
    key_name = f"{member_name}-encryption"
    logger.warning(
        f"Generating RSA encryption key pair for participant '{member_name}' in Azure Key Vault..."
    )
    az_cli(
        f"keyvault key create --name {key_name} --vault-name {vault_name} --kty RSA --size 2048 --ops decrypt"
    )
    enc_pubk_pem_file = output_dir + f"{os.path.sep}{member_name}_enc_pubk.pem"
    if os.path.exists(enc_pubk_pem_file):
        os.remove(enc_pubk_pem_file)
    az_cli(
        f"keyvault key download --name {key_name} --vault-name {vault_name} --file {enc_pubk_pem_file}"
    )
    kid = az_cli(
        f"keyvault key show --name {key_name} --vault-name {vault_name} --query key.kid --output tsv"
    )
    kid_file = output_dir + f"{os.path.sep}{member_name}_enc_key.id"
    with open(kid_file, "w") as f:
        f.write(str(kid))
    logger.warning(
        f"Encryption public key generated at: {enc_pubk_pem_file} (to be registered in CCF)"
    )
    logger.warning(f"Encryption key Azure Key Vault Id written out at: {kid_file}")


def get_cgs_client_endpoint(cmd, gov_client_name: str):
    port = get_cgs_client_port(cmd, gov_client_name)
    return f"http://localhost:{port}"


def get_cgs_client_port(cmd, gov_client_name: str):
    gov_client_name = get_gov_client_name(cmd.cli_ctx, gov_client_name)

    # Note (gsinha): Not using python_on_whales here as its load time is found to be slow and this
    # method gets invoked frequently to determin the client port. using the docker package instead.
    # from python_on_whales import DockerClient, exceptions

    import docker

    client = docker.from_env()

    try:
        container_name = f"{gov_client_name}-cgs-client-1"
        container = client.containers.get(container_name)
        port = container.ports["8080/tcp"][0]["HostPort"]
        # docker = DockerClient(
        #     compose_files=[compose_file], compose_project_name=gov_client_name
        # )
        # (_, port) = docker.compose.port(service="cgs-client", private_port=8080)
        return port
    # except exceptions.DockerException as e:
    except Exception as e:
        # Perhaps the client was started without docker compose and if so the container name might
        # be directly supplied as input.
        try:
            container_name = f"{gov_client_name}"
            container = client.containers.get(container_name)
            port = container.ports["8080/tcp"][0]["HostPort"]
            return port
        except Exception as e:
            raise CLIError(
                f"Not finding a client instance running with name '{gov_client_name}'. Check "
                + "the --governance-client parameter value."
            ) from e


def get_gov_client_name(cli_ctx, gov_client_name):
    if gov_client_name != "":
        return gov_client_name

    gov_client_name = cli_ctx.config.get("cleanroom", "governance.client_name", "")

    if gov_client_name == "":
        raise CLIError(
            "--governance-client=<value> parameter must be specified or set a default "
            + "value via `az config set cleanroom governance.client_name=<value>`"
        )

    logger.debug('Current value of "gov_client_name": %s.', gov_client_name)
    return gov_client_name


def response_error_message(r: requests.Response):
    return f"{r.request.method} {r.request.url} failed with status: {r.status_code} response: {r.text}"


def config_init_cmd(cmd, cleanroom_config_file):

    if os.path.exists(cleanroom_config_file):
        logger.warning(f"{cleanroom_config_file} already exists. Doing nothing.")
        return

    spec = CleanRoomSpecification(
        identities=[],
        datasources=[],
        datasinks=[],
        applications=[],
        applicationEndpoints=[],
        governance=None,
    )

    attested_identity = Identity(
        name="cleanroom_cgs_oidc",
        clientId="",
        tenantId="",
        tokenIssuer=AttestationBasedTokenIssuer(
            issuer=ServiceEndpoint(
                protocol=ProtocolType.Attested_OIDC,
                url="https://cgs/oidc",
            ),
            issuerType="AttestationBasedTokenIssuer",
        ),
    )
    spec.identities.append(attested_identity)

    from .utilities._configuration_helpers import write_cleanroom_spec_internal

    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def merge_specs(this: Any, that: Any):
    for k in that.model_fields.keys():
        this_attr = getattr(this, k)
        that_attr = getattr(that, k)

        if that_attr is None:
            continue

        if this_attr is None:
            setattr(this, k, that_attr)
            continue

        if this_attr == that_attr:
            continue

        if isinstance(this_attr, list) and isinstance(that_attr, list):
            for i in that_attr:
                if i not in this_attr:
                    this_attr.append(i)
                else:
                    index = ((j for j, x in enumerate(this_attr) if x == i), None)
                    assert index is not None

        else:
            this_attr = merge_specs(this_attr, that_attr)

    return this


def config_view_cmd(cmd, cleanroom_config_file, configs, output_file, no_print):

    from rich import print

    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )

    cleanroom_spec = read_cleanroom_spec_internal(cleanroom_config_file)

    for config in configs:
        cleanroom_spec = merge_specs(
            cleanroom_spec, read_cleanroom_spec_internal(config)
        )

    write_cleanroom_spec_internal(output_file, cleanroom_spec)
    if not no_print:
        print(cleanroom_spec)


def config_create_kek_policy_cmd(
    cmd,
    cleanroom_config_file,
    contract_id,
    b64_cl_policy,
    secretstore_config_file=SecretStoreConfiguration.default_secretstore_config_file(),
):
    cl_policy = json.loads(base64.b64decode(b64_cl_policy).decode("utf-8"))
    if not "policy" in cl_policy or not "x-ms-sevsnpvm-hostdata" in cl_policy["policy"]:
        raise CLIError(
            f"No clean room policy found under contract '{contract_id}'. Check "
            + "--contract-id parameter is correct and that a policy proposal for the contract has been accepted."
        )
    print(cl_policy)
    config_create_kek(
        cleanroom_config_file,
        secretstore_config_file,
        cl_policy["policy"]["x-ms-sevsnpvm-hostdata"][0],
    )


def config_wrap_deks_cmd(
    cmd,
    cleanroom_config_file,
    contract_id,
    gov_client_name="",
    datastore_config_file=DataStoreConfiguration.default_datastore_config_file(),
    secretstore_config_file=SecretStoreConfiguration.default_secretstore_config_file(),
):
    if gov_client_name != "":
        # Create the KEK first that will be used to wrap the DEKs.
        create_kek_via_governance(
            cmd,
            cleanroom_config_file,
            secretstore_config_file,
            contract_id,
            gov_client_name,
        )

    config_wrap_deks(
        cmd,
        cleanroom_config_file,
        datastore_config_file,
        secretstore_config_file,
    )


def config_wrap_secret_cmd(
    cmd,
    cleanroom_config_file,
    kek_secretstore_name,
    contract_id,
    name: str,
    value: str,
    secret_key_vault,
    secretstore_config_file=SecretStoreConfiguration.default_secretstore_config_file(),
    kek_name="",
    gov_client_name="",
):
    if gov_client_name != "":
        # Create the KEK first that will be used to wrap the DEKs.
        cl_policy = governance_deployment_policy_show_cmd(
            cmd, contract_id, gov_client_name
        )
        if (
            not "policy" in cl_policy
            or not "x-ms-sevsnpvm-hostdata" in cl_policy["policy"]
        ):
            raise CLIError(
                f"No clean room policy found under contract '{contract_id}'. Check "
                + "--contract-id parameter is correct and that a policy proposal for the contract has been accepted."
            )

        kek_name = (
            kek_name
            or str(uuid.uuid3(uuid.NAMESPACE_X500, cleanroom_config_file + "-1"))[:8]
            + "-kek"
        )

        create_kek(
            secretstore_config_file,
            kek_secretstore_name,
            kek_name,
            cl_policy["policy"]["x-ms-sevsnpvm-hostdata"][0],
        )

    from .utilities._secretstore_helpers import SecretStoreConfiguration

    kek_secret_store = SecretStoreConfiguration.get_secretstore(
        kek_secretstore_name, secretstore_config_file
    )
    public_key = kek_secret_store.get_secret(kek_name)

    if public_key is None:
        raise CLIError(
            f"KEK with name {kek_name} not found. Please run az cleanroom config create-kek first."
        )

    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.asymmetric import padding

    # Wrap the supplied secret
    ciphertext = base64.b64encode(
        public_key.encrypt(
            value.encode("utf-8"),
            padding.OAEP(
                mgf=padding.MGF1(algorithm=hashes.SHA256()),
                algorithm=hashes.SHA256(),
                label=None,
            ),
        )
    ).decode()

    secret_name = name
    vault_url = az_cli(
        f"resource show --id {secret_key_vault} --query properties.vaultUri"
    )
    vault_name = urlparse(vault_url).hostname.split(".")[0]

    logger.warning(
        f"Creating wrapped secret '{secret_name}' in key vault '{vault_name}'."
    )
    az_cli(
        f"keyvault secret set --name {secret_name} --vault-name {vault_name} --value {ciphertext}"
    )

    maa_endpoint = json.loads(
        base64.b64decode(kek_secret_store.entry.configuration).decode()
    )["authority"]

    return {
        "kid": secret_name,
        "akvEndpoint": vault_url,
        "kek": {
            "kid": kek_name,
            "akvEndpoint": kek_secret_store.entry.storeProviderUrl,
            "maaEndpoint": maa_endpoint,
        },
    }


def config_add_application_cmd(
    cmd,
    cleanroom_config_file,
    name,
    auto_start,
    image,
    cpu,
    memory,
    ports: List[int] = [],
    command_line=None,
    datasources={},
    datasinks={},
    env_vars={},
    acr_access_identity=None,
):
    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    acr_identity = None
    if acr_access_identity is not None:
        access_identity = [x for x in spec.identities if x.name == acr_access_identity]
        if len(access_identity) == 0:
            raise CLIError("Run az cleanroom config add-identity first.")
        acr_identity = access_identity[0]

    app_start_type = ApplicationStartType.Manual
    if auto_start:
        app_start_type = ApplicationStartType.Auto
    registry_url = image.split("/")[0]
    command = shlex.split(command_line) if command_line else []

    application = Application(
        name=name,
        startType=app_start_type,
        image=Image(
            executable=Document(
                documentType="OCI",
                authenticityReceipt="",
                identity=acr_identity,
                backingResource=Resource(
                    id=image,
                    name=name,
                    type=ResourceType.AzureContainerRegistry,
                    provider=ServiceEndpoint(
                        protocol=ProtocolType.AzureContainerRegistry,
                        url=registry_url,
                    ),
                ),
            ),
            enforcementPolicy=Policy(
                policy=InlinePolicy(
                    policyDocument=base64.b64encode(
                        json.dumps({"trustType": "https"}).encode()
                    ).decode()
                )
            ),
        ),
        command=command,
        environmentVariables=env_vars,
        datasources=datasources,
        datasinks=datasinks,
        runtimeSettings=RuntimeSettings(
            ports=ports,
            resource=ApplicationResource(requests=Requests(cpu=cpu, memoryInGB=memory)),
        ),
    )

    index = next(
        (i for i, x in enumerate(spec.applications) if x.name == application.name),
        None,
    )
    if index == None:
        logger.info(
            f"Adding entry for application {application.name} in configuration."
        )
        spec.applications.append(application)
    else:
        logger.info(f"Patching application {application.name} in configuration.")
        spec.applications[index] = application

    write_cleanroom_spec_internal(cleanroom_config_file, spec)
    logger.warning(f"Application {name} added to cleanroom configuration.")


def config_network_http_enable_cmd(
    cmd,
    cleanroom_config_file,
    direction: TrafficDirection,
    policy_bundle_url="",
):
    from azure.cli.core.util import CLIError

    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    if not spec.network:
        spec.network = NetworkSettings()

    if not spec.network.http:
        spec.network.http = HttpSettings()

    privacy_policy = None
    if policy_bundle_url:
        privacy_policy = Policy(
            policy=ExternalPolicy(
                documentType="OCI",
                authenticityReceipt="",
                backingResource=Resource(
                    name=policy_bundle_url,
                    id=policy_bundle_url,
                    type=ResourceType.AzureContainerRegistry,
                    provider=ServiceEndpoint(
                        protocol=ProtocolType.AzureContainerRegistry,
                        url=policy_bundle_url,
                    ),
                ),
            )
        )

    if direction == TrafficDirection.Inbound:
        spec.network.http.inbound = Inbound(
            enabled=True,
            policy=PrivacyProxySettings(
                proxyType=ProxyType.API,
                proxyMode=ProxyMode.Open,
                privacyPolicy=privacy_policy,
            ),
        )
    elif direction == TrafficDirection.Outbound:
        spec.network.http.outbound = Outbound(
            enabled=True,
            policy=PrivacyProxySettings(
                proxyType=ProxyType.API,
                proxyMode=ProxyMode.Open,
                privacyPolicy=privacy_policy,
            ),
        )

    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_network_http_disable_cmd(
    cmd, cleanroom_config_file, direction: TrafficDirection
):

    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    if not spec.network or not spec.network.http:
        return

    if direction == TrafficDirection.Inbound:
        spec.network.http.inbound = None
    elif direction == TrafficDirection.Outbound:
        spec.network.http.outbound = None

    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_network_tcp_enable_cmd(
    cmd,
    cleanroom_config_file,
    allowed_ips={},
):
    import ipaddress

    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    if not allowed_ips:
        raise CLIError("No IP addresses provided for allow listing.")

    if not spec.network:
        spec.network = NetworkSettings()

    if not spec.network.tcp:
        spec.network.tcp = TcpSettings(outbound=Outbound1(enabled=True, allowedIPs=[]))

    for address, port in allowed_ips.items():
        try:
            ipaddress.ip_address(address)
        except ValueError:
            raise CLIError(
                f"Invalid IP address: {address}. Only IP address allow listing is supported currently."
            )
        spec.network.tcp.outbound.allowedIPs.append(
            NetworkEndpoint(address=address, port=int(port))
        )

    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_network_tcp_disable_cmd(cmd, cleanroom_config_file):
    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    if not spec.network or not spec.network.tcp:
        return

    spec.network.tcp.outbound = None
    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_network_dns_enable_cmd(cmd, cleanroom_config_file, port: int):
    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    if not spec.network:
        spec.network = NetworkSettings()

    spec.network.dns = DnsSettings(enabled=True, port=port)

    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_network_dns_disable_cmd(cmd, cleanroom_config_file):
    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    if not spec.network:
        return

    spec.network.dns = None

    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_validate_cmd(cmd, cleanroom_config_file):
    from .utilities._configuration_helpers import read_cleanroom_spec_internal

    spec = read_cleanroom_spec_internal(cleanroom_config_file)
    _validate_config(spec)


def telemetry_aspire_dashboard_cmd(cmd, telemetry_folder, project_name=""):
    from python_on_whales import DockerClient

    project_name = project_name or "cleanroom-aspire-dashboard"
    os.environ["TELEMETRY_FOLDER"] = os.path.abspath(telemetry_folder)
    docker = DockerClient(
        compose_files=[aspire_dashboard_compose_file],
        compose_project_name=project_name,
    )
    docker.compose.up(remove_orphans=True, detach=True)
    (_, port) = docker.compose.port(service="aspire", private_port=18888)

    logger.warning("Open Aspire Dashboard at http://localhost:%s.", port)


def config_wrap_deks(
    cmd,
    cleanroom_config_file,
    datastore_config_file,
    secretstore_config_file,
):

    from .utilities._configuration_helpers import read_cleanroom_spec_internal

    config = read_cleanroom_spec_internal(cleanroom_config_file)

    from .utilities._datastore_helpers import generate_wrapped_dek
    from .utilities._secretstore_helpers import SecretStoreConfiguration

    for ds_entry in config.datasources + config.datasinks:
        datastore_name = ds_entry.store.id
        if not ds_entry.protection.encryptionSecrets:
            logger.info(
                f"No encryption secrets to wrap for datastore '{datastore_name}'."
            )
            continue
        kek_name = ds_entry.protection.encryptionSecrets.kek.name
        kek_secret_store_name = (
            ds_entry.protection.encryptionSecrets.kek.secret.backingResource.id
        )
        kek_secret_store = SecretStoreConfiguration.get_secretstore(
            kek_secret_store_name, secretstore_config_file
        )
        public_key = kek_secret_store.get_secret(kek_name)

        wrapped_dek_name = (
            ds_entry.protection.encryptionSecrets.dek.secret.backingResource.name
        )
        dek_secret_store_name = (
            ds_entry.protection.encryptionSecrets.dek.secret.backingResource.id
        )
        dek_secret_store = SecretStoreConfiguration.get_secretstore(
            dek_secret_store_name, secretstore_config_file
        )

        logger.warning(
            f"Creating wrapped DEK secret '{wrapped_dek_name}' for '{datastore_name}' in key vault '{dek_secret_store.entry.storeProviderUrl}'."
        )
        dek_secret_store.add_secret(
            wrapped_dek_name,
            lambda: generate_wrapped_dek(
                datastore_name, datastore_config_file, public_key, logger
            ),
        )


def create_kek_via_governance(
    cmd,
    cleanroom_config_file,
    secretstore_config_file,
    contract_id,
    gov_client_name,
):
    cl_policy = governance_deployment_policy_show_cmd(cmd, contract_id, gov_client_name)
    if not "policy" in cl_policy or not "x-ms-sevsnpvm-hostdata" in cl_policy["policy"]:
        raise CLIError(
            f"No clean room policy found under contract '{contract_id}'. Check "
            + "--contract-id parameter is correct and that a policy proposal for the contract has been accepted."
        )

    config_create_kek(
        cleanroom_config_file,
        secretstore_config_file,
        cl_policy["policy"]["x-ms-sevsnpvm-hostdata"][0],
    )


def config_create_kek(
    cleanroom_config_file,
    secretstore_config_file,
    key_release_policy,
):
    from .utilities._azcli_helpers import logger
    from .utilities._configuration_helpers import read_cleanroom_spec_internal

    spec = read_cleanroom_spec_internal(cleanroom_config_file)
    for ds_entry in spec.datasources + spec.datasinks:
        ds_name = ds_entry.name
        if not ds_entry.protection.encryptionSecrets:
            logger.info(
                f"Skipping KEK creation for datastore '{ds_name}' as no encryption secrets are specified."
            )
            continue

        kek_name = ds_entry.protection.encryptionSecrets.kek.secret.backingResource.name
        kek_secret_store_name = (
            ds_entry.protection.encryptionSecrets.kek.secret.backingResource.id
        )
        create_kek(
            secretstore_config_file, kek_secret_store_name, kek_name, key_release_policy
        )
        logger.info(f"Created KEK {kek_name} for {ds_name}")


def create_kek(
    secretstore_config_file,
    kek_secret_store_name,
    kek_name,
    key_release_policy,
):
    from .utilities._azcli_helpers import logger

    kek_secret_store = SecretStoreConfiguration.get_secretstore(
        kek_secret_store_name, secretstore_config_file
    )

    def create_key():
        from cryptography.hazmat.primitives.asymmetric import rsa

        return rsa.generate_private_key(public_exponent=65537, key_size=2048)

    _ = kek_secret_store.add_secret(
        kek_name,
        generate_secret=create_key,
        security_policy=key_release_policy,
    )
    logger.warning(
        f"Created KEK {kek_name} in store {kek_secret_store.entry.storeProviderUrl}"
    )


def get_current_jsapp_bundle(cgs_endpoint: str):
    r = requests.get(f"{cgs_endpoint}/jsapp/bundle")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    bundle = r.json()
    bundle_hash = hashlib.sha256(bytes(json.dumps(bundle), "utf-8")).hexdigest()
    canonical_bundle = json.dumps(bundle, indent=2, sort_keys=True, ensure_ascii=False)
    canonical_bundle_hash = hashlib.sha256(bytes(canonical_bundle, "utf-8")).hexdigest()
    return bundle, bundle_hash, canonical_bundle, canonical_bundle_hash


def find_constitution_version_entry(tag: str) -> str | None:
    if tag == "latest":
        return find_version_document_entry("latest", "cgs-constitution")

    version = find_version_manifest_entry(tag, "cgs-constitution")

    if version is not None:
        return version

    # Handle the first release of 1.0.6 and 1.0.8 that went out which does not have version manifest entry.
    if tag == "d1e339962fca8d92fe543617c89bb69127dd075feb3599d8a7c71938a0a6a29f":
        return "1.0.6"

    if tag == "6b5961db2f6c0c9b0a1a640146dceac20e816225b29925891ecbb4b8e0aa9d02":
        return "1.0.8"


def find_jsapp_version_entry(tag: str) -> str | None:
    if tag == "latest":
        return find_version_document_entry("latest", "cgs-js-app")

    version = find_version_manifest_entry(tag, "cgs-js-app")

    if version is not None:
        return version

    # Handle the first release of 1.0.6 and 1.0.8 that went out which does not have version manifest entry.
    if tag == "01043eb27af3faa8f76c1ef3f95e516dcc0b2b78c71302a878ed968da62967b1":
        return "1.0.6"

    if tag == "d42383b4a2d6c88c68cb1114e71da6ad0aa724e90297d1ad82db6206eb6fd417":
        return "1.0.8"


def find_cgs_client_version_entry(tag) -> str | None:
    # Handle the first release of 1.0.6 and 1.0.8 that went out which does not have version document entry.
    if tag == "sha256:6bbdb78ed816cc702249dcecac40467b1d31e5c8cfbb1ef312b7d119dde7024f":
        return "1.0.6"

    if tag == "sha256:38a2c27065a9b6785081eb5e4bf9f3ddd219860d06ad65f5aad4e63466996561":
        return "1.0.7"

    if tag == "sha256:8627a64bb0db303e7a837a06f65e91e1ee9c9d59df1228849c09a59571de9121":
        return "1.0.8"

    return find_version_document_entry(tag, "cgs-client")


def find_version_manifest_entry(tag: str, component: str) -> str | None:
    registry_url = get_versions_registry()
    import oras.client
    import oras.oci
    from cleanroom_common.azure_cleanroom_core.utilities.helpers import (
        use_insecure_http,
    )

    insecure = use_insecure_http(registry_url)

    if tag.startswith("sha256:"):
        tag = tag[7:]
    component_url = f"{registry_url}/{component}:{tag}"
    client = oras.client.OrasClient(insecure=insecure)
    if not registry_url.startswith(MCR_CLEANROOM_VERSIONS_REGISTRY):
        logger.warning("Fetching the manifest from override url %s", component_url)
    try:
        manifest: dict = client.remote.get_manifest(component_url)
    except Exception as e:
        logger.error(f"Failed to pull manifest: {e}")
        return None

    annotations = manifest.get("annotations", {})
    version = (
        annotations["cleanroom.version"] if "cleanroom.version" in annotations else None
    )
    return version


def find_version_document_entry(tag: str, component: str) -> str | None:
    registry_url = get_versions_registry()
    import oras.client
    from cleanroom_common.azure_cleanroom_core.utilities.helpers import (
        use_insecure_http,
    )

    insecure = use_insecure_http(registry_url)

    dir_path = os.path.dirname(os.path.realpath(__file__))
    versions_folder = os.path.join(
        dir_path, f"bin{os.path.sep}versions{os.path.sep}{component}"
    )
    if not os.path.exists(versions_folder):
        os.makedirs(versions_folder)

    if tag.startswith("sha256:"):
        tag = tag[7:]
    component_url = f"{registry_url}/versions/{component}:{tag}"
    client = oras.client.OrasClient(insecure=insecure)
    if not registry_url.startswith(MCR_CLEANROOM_VERSIONS_REGISTRY):
        logger.warning(
            "Downloading the version document from override url %s", component_url
        )
    try:
        client.pull(target=component_url, outdir=versions_folder)
    except Exception as e:
        logger.error(f"Failed to pull version document: {e}")
        return None

    versions_file = os.path.join(versions_folder, "version.yaml")
    with open(versions_file) as f:
        versions = yaml.safe_load(f)

    return (
        str(versions[component]["version"])
        if component in versions and "version" in versions[component]
        else None
    )


def constitution_digest_to_version_info(digest):
    cgs_constitution = find_constitution_version_entry(digest)
    if cgs_constitution == None:
        raise CLIError(
            f"Could not identify version for cgs-consitution digest: {digest}. "
            "cleanroom extension upgrade may be required."
        )

    from packaging.version import Version

    upgrade = None
    current_version = Version(cgs_constitution)
    latest_tag = os.environ.get("AZCLI_CGS_SERVICE_LATEST_TAG", "latest")
    latest_cgs_constitution = find_constitution_version_entry(latest_tag)
    if (
        latest_cgs_constitution != None
        and Version(latest_cgs_constitution) > current_version
    ):
        upgrade = {"constitutionVersion": latest_cgs_constitution}

    return str(current_version), upgrade


def bundle_digest_to_version_info(canonical_digest):
    cgs_jsapp = find_jsapp_version_entry(canonical_digest)
    if cgs_jsapp == None:
        raise CLIError(
            f"Could not identify version for cgs-js-app bundle digest: {canonical_digest}. "
            "cleanroom extension upgrade may be required."
        )

    from packaging.version import Version

    upgrade = None
    current_version = Version(cgs_jsapp)
    latest_tag = os.environ.get("AZCLI_CGS_SERVICE_LATEST_TAG", "latest")
    latest_cgs_jsapp = find_jsapp_version_entry(latest_tag)
    if latest_cgs_jsapp != None and Version(latest_cgs_jsapp) > current_version:
        upgrade = {"jsappVersion": latest_cgs_jsapp}

    return str(current_version), upgrade


def download_constitution_jsapp(folder, constitution_url="", jsapp_url=""):
    if constitution_url == "":
        constitution_url = os.environ.get(
            "AZCLI_CGS_CONSTITUTION_IMAGE", mcr_cgs_constitution_url
        )
    if jsapp_url == "":
        jsapp_url = os.environ.get("AZCLI_CGS_JSAPP_IMAGE", mcr_cgs_jsapp_url)

    # Extract the registry_hostname from the URL.
    # https://foo.ghcr.io/some:tag => "foo.ghcr.io"
    registry_url = urlparse("https://" + jsapp_url).netloc

    if registry_url != urlparse("https://" + constitution_url).netloc:
        raise CLIError(
            f"Constitution url '{constitution_url}' & js app url '{jsapp_url}' must point to the same registry"
        )

    if constitution_url != mcr_cgs_constitution_url:
        logger.warning(f"Using constitution url override: {constitution_url}")
    if jsapp_url != mcr_cgs_jsapp_url:
        logger.warning(f"Using jsapp url override: {jsapp_url}")

    constitution = download_constitution(folder, constitution_url)
    bundle = download_jsapp(folder, jsapp_url)
    return constitution, bundle


def download_constitution(folder, constitution_url):
    from cleanroom_common.azure_cleanroom_core.utilities.helpers import (
        use_insecure_http,
    )

    insecure = use_insecure_http(constitution_url)

    import oras.client

    client = oras.client.OrasClient(insecure=insecure)
    logger.warning("Downloading the constitution from %s", constitution_url)

    try:
        manifest: dict = client.remote.get_manifest(constitution_url)
    except Exception as e:
        raise CLIError(f"Failed to get manifest: {e}")

    layers = manifest.get("layers", [])
    for index, x in enumerate(layers):
        if (
            "annotations" in x
            and "org.opencontainers.image.title" in x["annotations"]
            and x["annotations"]["org.opencontainers.image.title"]
            == "constitution.json"
        ):
            break
    else:
        raise CLIError(
            f"constitution.json document not found in {constitution_url} manifest."
        )

    try:
        client.pull(target=constitution_url, outdir=folder)
    except Exception as e:
        raise CLIError(f"Failed to pull constitution: {e}")

    constitution = json.load(
        open(f"{folder}{os.path.sep}constitution.json", encoding="utf-8", mode="r")
    )
    return constitution


def download_jsapp(folder, jsapp_url):
    from cleanroom_common.azure_cleanroom_core.utilities.helpers import (
        use_insecure_http,
    )

    insecure = use_insecure_http(jsapp_url)

    import oras.client

    client = oras.client.OrasClient(insecure=insecure)
    logger.warning(
        "Downloading the governance service js application from %s", jsapp_url
    )

    try:
        manifest: dict = client.remote.get_manifest(jsapp_url)
    except Exception as e:
        raise CLIError(f"Failed to get manifest: {e}")

    layers = manifest.get("layers", [])
    for index, x in enumerate(layers):
        if (
            "annotations" in x
            and "org.opencontainers.image.title" in x["annotations"]
            and x["annotations"]["org.opencontainers.image.title"] == "bundle.json"
        ):
            break
    else:
        raise CLIError(f"bundle.json document not found in {jsapp_url} manifest.")

    try:
        client.pull(target=jsapp_url, outdir=folder)
    except Exception as e:
        raise CLIError(f"Failed to pull js app bundle: {e}")

    bundle = json.load(
        open(f"{folder}{os.path.sep}bundle.json", encoding="utf-8", mode="r")
    )
    return bundle


def get_current_constitution(cgs_endpoint: str):
    r = requests.get(f"{cgs_endpoint}/constitution")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))

    hash = hashlib.sha256(bytes(r.text, "utf-8")).hexdigest()
    return r.text, hash


def get_cgs_client_digest(gov_client_name: str) -> str:
    import docker

    client = docker.from_env()
    try:
        container_name = f"{gov_client_name}-cgs-client-1"
        container = client.containers.get(container_name)
    except Exception as e:
        # Perhaps the client was started without docker compose and if so the container name might
        # be directly supplied as input.
        try:
            container_name = f"{gov_client_name}"
            container = client.containers.get(container_name)
        except Exception as e:
            raise CLIError(
                f"Not finding a client instance running with name '{gov_client_name}'. Check the --name parameter value."
            ) from e

    image = client.images.get(container.image.id)
    repoDigest: str = image.attrs["RepoDigests"][0]
    digest = image.attrs["RepoDigests"][0][len(repoDigest) - 71 :]
    return digest


def get_versions_registry() -> str:
    return os.environ.get(
        "AZCLI_CLEANROOM_VERSIONS_REGISTRY", MCR_CLEANROOM_VERSIONS_REGISTRY
    )


def try_get_constitution_version(digest: str):
    entry = find_constitution_version_entry(digest)
    return "unknown" if entry == None else entry


def try_get_jsapp_version(canonical_digest: str):
    entry = find_jsapp_version_entry(canonical_digest)
    return "unknown" if entry == None else entry


def try_get_cgs_client_version(tag: str):
    entry = find_cgs_client_version_entry(tag)
    return "unknown" if entry == None else entry


def _validate_config(spec: CleanRoomSpecification):
    from cleanroom_common.azure_cleanroom_core.utilities.helpers import validate_config
    from rich.console import Console

    console = Console()
    issues, warnings = validate_config(spec, logger)

    if len(warnings) > 0:
        console.print(f"Warnings in the specification: {warnings}", style="bold yellow")

    if len(issues) > 0:
        errors = [str(x) for x in issues]
        raise CLIError(errors)


def cluster_provider_deploy_cmd(cmd, provider_client_name):
    from .custom_cleanroom_cluster import cluster_provider_deploy

    return cluster_provider_deploy(cmd, provider_client_name)


def cluster_provider_remove_cmd(cmd, provider_client_name):
    from .custom_cleanroom_cluster import cluster_provider_remove

    return cluster_provider_remove(cmd, provider_client_name)


def cluster_up_cmd(
    cmd,
    cluster_name,
    infra_type,
    resource_group,
    ws_folder,
    location,
    provider_client_name,
):
    from .custom_cleanroom_cluster import cluster_up

    return cluster_up(
        cmd,
        cluster_name,
        infra_type,
        resource_group,
        ws_folder,
        location,
        provider_client_name,
    )


def cluster_create_cmd(
    cmd,
    cluster_name,
    infra_type,
    provider_config,
    enable_observability,
    enable_analytics_workload,
    analytics_workload_config_url,
    analytics_workload_config_url_ca_cert,
    analytics_workload_disable_telemetry_collection,
    analytics_workload_security_policy_creation_option,
    analytics_workload_security_policy,
    provider_client_name,
):
    from .custom_cleanroom_cluster import cluster_create

    return cluster_create(
        cmd,
        cluster_name,
        infra_type,
        provider_config,
        enable_observability,
        enable_analytics_workload,
        analytics_workload_config_url,
        analytics_workload_config_url_ca_cert,
        analytics_workload_disable_telemetry_collection,
        analytics_workload_security_policy_creation_option,
        analytics_workload_security_policy,
        provider_client_name,
    )


def cluster_update_cmd(
    cmd,
    cluster_name,
    infra_type,
    provider_config,
    enable_observability,
    enable_analytics_workload,
    analytics_workload_config_url,
    analytics_workload_config_url_ca_cert,
    analytics_workload_disable_telemetry_collection,
    analytics_workload_security_policy_creation_option,
    analytics_workload_security_policy,
    provider_client_name,
):
    from .custom_cleanroom_cluster import cluster_update

    return cluster_update(
        cmd,
        cluster_name,
        infra_type,
        provider_config,
        enable_observability,
        enable_analytics_workload,
        analytics_workload_config_url,
        analytics_workload_config_url_ca_cert,
        analytics_workload_disable_telemetry_collection,
        analytics_workload_security_policy_creation_option,
        analytics_workload_security_policy,
        provider_client_name,
    )


def cluster_show_cmd(
    cmd, cluster_name, infra_type, provider_config, provider_client_name
):
    from .custom_cleanroom_cluster import cluster_show

    return cluster_show(
        cmd, cluster_name, infra_type, provider_config, provider_client_name
    )


def cluster_delete_cmd(
    cmd, cluster_name, infra_type, provider_config, provider_client_name
):
    from .custom_cleanroom_cluster import cluster_delete

    return cluster_delete(
        cmd, cluster_name, infra_type, provider_config, provider_client_name
    )


def cluster_get_kubeconfig_cmd(
    cmd, cluster_name, infra_type, file, provider_config, provider_client_name
):
    from .custom_cleanroom_cluster import cluster_get_kubeconfig

    return cluster_get_kubeconfig(
        cmd, cluster_name, infra_type, file, provider_config, provider_client_name
    )


def cluster_analytics_workload_deployment_generate_cmd(
    cmd,
    infra_type,
    provider_config,
    disable_telemetry_collection,
    contract_id,
    gov_client_name,
    security_policy_creation_option,
    output_dir,
    provider_client_name,
):
    from .custom_cleanroom_cluster import cluster_analytics_workload_deployment_generate

    return cluster_analytics_workload_deployment_generate(
        cmd,
        infra_type,
        provider_config,
        disable_telemetry_collection,
        contract_id,
        gov_client_name,
        security_policy_creation_option,
        output_dir,
        provider_client_name,
    )


def ccf_provider_deploy_cmd(cmd, provider_client_name):
    from .custom_ccf import ccf_provider_deploy

    return ccf_provider_deploy(cmd, provider_client_name)


def ccf_provider_configure_cmd(
    cmd, signing_cert_id, signing_cert, signing_key, provider_client_name
):
    from .custom_ccf import ccf_provider_configure

    return ccf_provider_configure(
        cmd, signing_cert_id, signing_cert, signing_key, provider_client_name
    )


def ccf_provider_show_cmd(cmd, provider_client_name):
    from .custom_ccf import ccf_provider_show

    return ccf_provider_show(cmd, provider_client_name)


def ccf_provider_remove_cmd(cmd, provider_client_name):
    from .custom_ccf import ccf_provider_remove

    return ccf_provider_remove(cmd, provider_client_name)


def ccf_network_up_cmd(
    cmd,
    network_name,
    infra_type,
    resource_group,
    ws_folder,
    location,
    security_policy_creation_option,
    recovery_mode,
    provider_client_name,
):
    from .custom_ccf import ccf_network_up

    return ccf_network_up(
        cmd,
        network_name,
        infra_type,
        resource_group,
        ws_folder,
        location,
        security_policy_creation_option,
        recovery_mode,
        provider_client_name,
    )


def ccf_network_create_cmd(
    cmd,
    network_name,
    infra_type,
    node_count,
    node_log_level,
    security_policy_creation_option,
    security_policy,
    members,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_create

    return ccf_network_create(
        cmd,
        network_name,
        infra_type,
        node_count,
        node_log_level,
        security_policy_creation_option,
        security_policy,
        members,
        provider_config,
        provider_client_name,
    )


def ccf_network_delete_cmd(
    cmd, network_name, infra_type, delete_option, provider_config, provider_client_name
):
    from .custom_ccf import ccf_network_delete

    return ccf_network_delete(
        cmd,
        network_name,
        infra_type,
        delete_option,
        provider_config,
        provider_client_name,
    )


def ccf_network_update_cmd(
    cmd,
    network_name,
    infra_type,
    node_count,
    node_log_level,
    security_policy_creation_option,
    security_policy,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_update

    return ccf_network_update(
        cmd,
        network_name,
        infra_type,
        node_count,
        node_log_level,
        security_policy_creation_option,
        security_policy,
        provider_config,
        provider_client_name,
    )


def ccf_network_recover_public_network_cmd(
    cmd,
    network_name,
    target_network_name,
    previous_service_cert,
    infra_type,
    node_count,
    node_log_level,
    security_policy_creation_option,
    security_policy,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_recover_public_network

    return ccf_network_recover_public_network(
        cmd,
        network_name,
        target_network_name,
        previous_service_cert,
        infra_type,
        node_count,
        node_log_level,
        security_policy_creation_option,
        security_policy,
        provider_config,
        provider_client_name,
    )


def ccf_network_submit_recovery_share_cmd(
    cmd,
    network_name,
    infra_type,
    encryption_private_key,
    encryption_key_id,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_submit_recovery_share

    return ccf_network_submit_recovery_share(
        cmd,
        network_name,
        infra_type,
        encryption_private_key,
        encryption_key_id,
        provider_config,
        provider_client_name,
    )


def ccf_network_recover_cmd(
    cmd,
    network_name,
    previous_service_cert,
    encryption_private_key,
    encryption_key_id,
    recovery_service_name,
    member_name,
    infra_type,
    node_log_level,
    security_policy_creation_option,
    security_policy,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_recover

    return ccf_network_recover(
        cmd,
        network_name,
        previous_service_cert,
        encryption_private_key,
        encryption_key_id,
        recovery_service_name,
        member_name,
        infra_type,
        node_log_level,
        security_policy_creation_option,
        security_policy,
        provider_config,
        provider_client_name,
    )


def ccf_network_show_cmd(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf import ccf_network_show

    return ccf_network_show(
        cmd, network_name, infra_type, provider_config, provider_client_name
    )


def ccf_network_recovery_agent_show_cmd(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf import ccf_network_recovery_agent_show

    return ccf_network_recovery_agent_show(
        cmd, network_name, infra_type, provider_config, provider_client_name
    )


def ccf_network_recovery_agent_show_report_cmd(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf import ccf_network_recovery_agent_show_report

    return ccf_network_recovery_agent_show_report(
        cmd, network_name, infra_type, provider_config, provider_client_name
    )


def ccf_network_recovery_agent_show_network_report_cmd(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf import ccf_network_recovery_agent_show_network_report

    return ccf_network_recovery_agent_show_network_report(
        cmd, network_name, infra_type, provider_config, provider_client_name
    )


def ccf_network_recovery_agent_generate_member_cmd(
    cmd,
    network_name,
    member_name,
    infra_type,
    agent_config,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_recovery_agent_generate_member

    return ccf_network_recovery_agent_generate_member(
        cmd,
        network_name,
        member_name,
        infra_type,
        agent_config,
        provider_config,
        provider_client_name,
    )


def ccf_network_recovery_agent_activate_member_cmd(
    cmd,
    network_name,
    member_name,
    infra_type,
    agent_config,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_recovery_agent_activate_member

    return ccf_network_recovery_agent_activate_member(
        cmd,
        network_name,
        member_name,
        infra_type,
        agent_config,
        provider_config,
        provider_client_name,
    )


def ccf_network_recovery_agent_submit_recovery_share_cmd(
    cmd,
    network_name,
    member_name,
    infra_type,
    agent_config,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_recovery_agent_submit_recovery_share

    return ccf_network_recovery_agent_submit_recovery_share(
        cmd,
        network_name,
        member_name,
        infra_type,
        agent_config,
        provider_config,
        provider_client_name,
    )


def ccf_network_recovery_agent_set_network_join_policy_cmd(
    cmd,
    network_name,
    infra_type,
    agent_config,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_recovery_agent_set_network_join_policy

    return ccf_network_recovery_agent_set_network_join_policy(
        cmd,
        network_name,
        infra_type,
        agent_config,
        provider_config,
        provider_client_name,
    )


def ccf_network_show_health_cmd(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf import ccf_network_show_health

    return ccf_network_show_health(
        cmd, network_name, infra_type, provider_config, provider_client_name
    )


def ccf_network_show_report_cmd(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf import ccf_network_show_report

    return ccf_network_show_report(
        cmd, network_name, infra_type, provider_config, provider_client_name
    )


def ccf_network_trigger_snapshot_cmd(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf import ccf_network_trigger_snapshot

    return ccf_network_trigger_snapshot(
        cmd, network_name, infra_type, provider_config, provider_client_name
    )


def ccf_network_transition_to_open_cmd(
    cmd,
    network_name,
    infra_type,
    previous_service_cert,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_transition_to_open

    return ccf_network_transition_to_open(
        cmd,
        network_name,
        infra_type,
        previous_service_cert,
        provider_config,
        provider_client_name,
    )


def ccf_network_security_policy_generate_cmd(
    cmd,
    infra_type,
    security_policy_creation_option,
    provider_client_name,
):
    from .custom_ccf import ccf_network_security_policy_generate

    return ccf_network_security_policy_generate(
        cmd,
        infra_type,
        security_policy_creation_option,
        provider_client_name,
    )


def ccf_network_security_policy_generate_join_policy_cmd(
    cmd,
    infra_type,
    security_policy_creation_option,
    provider_client_name,
):
    from .custom_ccf import ccf_network_security_policy_generate_join_policy

    return ccf_network_security_policy_generate_join_policy(
        cmd,
        infra_type,
        security_policy_creation_option,
        provider_client_name,
    )


def ccf_network_security_policy_generate_join_policy_from_network_cmd(
    cmd,
    infra_type,
    network_name,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import (
        ccf_network_security_policy_generate_join_policy_from_network,
    )

    return ccf_network_security_policy_generate_join_policy_from_network(
        cmd,
        infra_type,
        network_name,
        provider_config,
        provider_client_name,
    )


def ccf_network_join_policy_add_snp_host_data_cmd(
    cmd,
    network_name,
    infra_type,
    host_data,
    security_policy,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_join_policy_add_snp_host_data

    return ccf_network_join_policy_add_snp_host_data(
        cmd,
        network_name,
        infra_type,
        host_data,
        security_policy,
        provider_config,
        provider_client_name,
    )


def ccf_network_join_policy_remove_snp_host_data_cmd(
    cmd,
    network_name,
    infra_type,
    host_data,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_join_policy_remove_snp_host_data

    return ccf_network_join_policy_remove_snp_host_data(
        cmd,
        network_name,
        infra_type,
        host_data,
        provider_config,
        provider_client_name,
    )


def ccf_network_join_policy_show_cmd(
    cmd,
    network_name,
    infra_type,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_join_policy_show

    return ccf_network_join_policy_show(
        cmd,
        network_name,
        infra_type,
        provider_config,
        provider_client_name,
    )


def ccf_network_set_recovery_threshold_cmd(
    cmd,
    network_name,
    infra_type,
    recovery_threshold: int,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_set_recovery_threshold

    return ccf_network_set_recovery_threshold(
        cmd,
        network_name,
        infra_type,
        recovery_threshold,
        provider_config,
        provider_client_name,
    )


def ccf_network_configure_confidential_recovery_cmd(
    cmd,
    network_name,
    recovery_service_name,
    recovery_member_name,
    infra_type,
    provider_config,
    provider_client_name,
):
    from .custom_ccf import ccf_network_configure_confidential_recovery

    return ccf_network_configure_confidential_recovery(
        cmd,
        network_name,
        recovery_service_name,
        recovery_member_name,
        infra_type,
        provider_config,
        provider_client_name,
    )


def ccf_recovery_service_create_cmd(
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
    from .custom_ccf_recovery_service import ccf_recovery_service_create

    return ccf_recovery_service_create(
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
    )


def ccf_recovery_service_delete_cmd(
    cmd, service_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf_recovery_service import ccf_recovery_service_delete

    return ccf_recovery_service_delete(
        cmd,
        service_name,
        infra_type,
        provider_config,
        provider_client_name,
    )


def ccf_recovery_service_show_cmd(
    cmd, service_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf_recovery_service import ccf_recovery_service_show

    return ccf_recovery_service_show(
        cmd, service_name, infra_type, provider_config, provider_client_name
    )


def ccf_recovery_service_security_policy_generate_cmd(
    cmd,
    infra_type,
    security_policy_creation_option,
    ccf_network_join_policy,
    provider_client_name,
):
    from .custom_ccf_recovery_service import (
        ccf_recovery_service_security_policy_generate,
    )

    return ccf_recovery_service_security_policy_generate(
        cmd,
        infra_type,
        security_policy_creation_option,
        ccf_network_join_policy,
        provider_client_name,
    )


def ccf_recovery_service_api_network_show_join_policy_cmd(
    cmd, service_config, provider_client_name
):
    from .custom_ccf_recovery_service import (
        ccf_recovery_service_api_network_show_join_policy,
    )

    return ccf_recovery_service_api_network_show_join_policy(
        cmd, service_config, provider_client_name
    )


def ccf_recovery_service_api_member_show_cmd(
    cmd, member_name, service_config, provider_client_name
):
    from .custom_ccf_recovery_service import ccf_recovery_service_api_member_show

    return ccf_recovery_service_api_member_show(
        cmd, member_name, service_config, provider_client_name
    )


def ccf_recovery_service_api_member_show_report_cmd(
    cmd, member_name, service_config, provider_client_name
):
    from .custom_ccf_recovery_service import ccf_recovery_service_api_member_show_report

    return ccf_recovery_service_api_member_show_report(
        cmd, member_name, service_config, provider_client_name
    )


def ccf_recovery_service_api_show_report_cmd(cmd, service_config, provider_client_name):
    from .custom_ccf_recovery_service import ccf_recovery_service_api_show_report

    return ccf_recovery_service_api_show_report(
        cmd, service_config, provider_client_name
    )


def ccf_consortium_manager_create_cmd(
    cmd,
    consortium_manager_name,
    infra_type,
    provider_config,
    provider_client_name,
):
    from .custom_ccf_consortium_manager import ccf_consortium_manager_create

    return ccf_consortium_manager_create(
        cmd,
        consortium_manager_name,
        infra_type,
        provider_config,
        provider_client_name,
    )


def ccf_consortium_manager_show_cmd(
    cmd, consortium_manager_name, infra_type, provider_config, provider_client_name
):
    from .custom_ccf_consortium_manager import ccf_consortium_manager_show

    return ccf_consortium_manager_show(
        cmd, consortium_manager_name, infra_type, provider_config, provider_client_name
    )


def get_default_cert_policy(member_name) -> str:
    policy = {
        "issuerParameters": {"name": "Self"},
        "keyProperties": {
            "curve": "P-384",
            "exportable": True,
            "keyType": "EC",
            "reuseKey": True,
        },
        "secretProperties": {"contentType": "application/x-pkcs12"},
        "x509CertificateProperties": {
            "keyUsage": ["digitalSignature"],
            "subject": f"CN={member_name}",
            "validityInMonths": 12,
        },
    }

    return json.dumps(policy, indent=2)


# TODO (gsinha): Need to register a new app in AAD and use that for authentication
# for the device code flow. The current client ID is used only for testing purposes.
MS_CLIENT_ID = "8a3849c1-81c5-4d62-b83e-3bb2bb11251a"
MS_TENANT_ID = "common"  # or "organizations" or specific tenant GUID
MS_AUTHORITY = f"https://login.microsoftonline.com/{MS_TENANT_ID}"
MS_SCOPES = ["User.Read"]


def load_cache(msal_token_cache_file):
    from msal import SerializableTokenCache

    cache = SerializableTokenCache()
    if os.path.exists(msal_token_cache_file):
        cache.deserialize(open(msal_token_cache_file, "r").read())
    return cache


def save_cache(cache, msal_token_cache_file):
    if cache.has_state_changed:
        with open(msal_token_cache_file, "w") as f:
            f.write(cache.serialize())


def ms_perform_device_code_flow(msal_token_cache_dir):
    import msal

    msal_token_cache_file = os.path.join(msal_token_cache_dir, "token_cache.json")
    token_cache = load_cache(msal_token_cache_file)
    app = msal.PublicClientApplication(
        MS_CLIENT_ID, authority=MS_AUTHORITY, token_cache=token_cache
    )
    account = None
    for x in app.get_accounts():
        if x["environment"] == "login.microsoftonline.com" and x["realm"] == "common":
            account = x
            break
    if account:
        result = app.acquire_token_silent(MS_SCOPES, account=account)
        if result and "access_token" in result:
            save_cache(token_cache, msal_token_cache_file)
            return

    # If no token is found, perform device code flow.
    flow = app.initiate_device_flow(scopes=MS_SCOPES)
    if "user_code" not in flow:
        raise Exception("Failed to create device flow. Error: {}".format(flow))

    print("Please go to", flow["verification_uri"])
    print("And enter the code:", flow["user_code"])
    result = app.acquire_token_by_device_flow(flow)
    if "access_token" in result:
        name = result["id_token_claims"].get("name", "")
        email = result["id_token_claims"].get("preferred_username", "")
        oid = result["id_token_claims"].get("oid", "")
        if email == "" or oid == "":
            raise Exception("Login failed: missing email or oid in token claims")
        print("User:", name)
        print("Email:", email)
        print("oid:", oid)
        save_cache(token_cache, msal_token_cache_file)
    else:
        err = result.get("error_description")
        raise Exception(f"Login failed: {err}")
