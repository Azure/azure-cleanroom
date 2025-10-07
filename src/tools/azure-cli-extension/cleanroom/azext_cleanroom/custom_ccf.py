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

# Note (gsinha): Various imports are also mentioned inline in the code at the point of usage.
# This is done to speed up command execution as having all the imports listed at top level is making
# execution slow for every command even if the top level imported packaged will not be used by that
# command.
import hashlib
import json
import os
import tempfile
import time
import uuid
from time import sleep

import requests
from azure.cli.core.util import CLIError, get_file_json, shell_safe_json_parse
from knack.log import get_logger

from .custom import response_error_message
from .utilities._azcli_helpers import az_cli

logger = get_logger(__name__)

ccf_provider_compose_file: str = (
    f"{os.path.dirname(__file__)}{os.path.sep}data{os.path.sep}ccf-provider{os.path.sep}docker-compose.yaml"
)
ccf_provider_workspace_dir: str = tempfile.gettempdir() + os.path.sep + "ccf-provider"


def ccf_provider_deploy(cmd, provider_client_name):
    from python_on_whales import DockerClient

    docker = DockerClient(
        compose_files=[ccf_provider_compose_file],
        compose_project_name=provider_client_name,
    )

    if not os.path.exists(ccf_provider_workspace_dir):
        os.makedirs(ccf_provider_workspace_dir)

    if "AZCLI_CCF_PROVIDER_CLIENT_IMAGE" in os.environ:
        image = os.environ["AZCLI_CCF_PROVIDER_CLIENT_IMAGE"]
        logger.warning(f"Using ccf-provider-client image from override url: {image}")

    set_docker_compose_env_params()
    docker.compose.up(remove_orphans=True, detach=True)
    (_, port) = docker.compose.port(service="client", private_port=8080)

    ccf_provider_endpoint = f"http://localhost:{port}"
    while True:
        try:
            r = requests.get(f"{ccf_provider_endpoint}/ready")
            if r.status_code == 200:
                break
            else:
                logger.warning(
                    f"Waiting for ccf-provider-client endpoint to be up... (status code: {r.status_code})"
                )
                sleep(5)
        except:
            logger.warning("Waiting for ccf-provider-client endpoint to be up...")
            sleep(5)

    logger.warning("ccf-provider-client container is listening on %s.", port)


def ccf_provider_configure(
    cmd, signing_cert_id, signing_cert, signing_key, provider_client_name
):
    if not signing_cert and not signing_key and not signing_cert_id:
        raise CLIError(
            "Either (signing-cert,signing-key) or signing-cert-id must be specified."
        )
    if signing_cert_id:
        if signing_cert or signing_key:
            raise CLIError(
                "signing-cert/signing-key cannot be specified along with signing-cert-id."
            )
        if os.path.exists(signing_cert_id):
            with open(signing_cert_id, "r") as f:
                signing_cert_id = f.read()
    else:
        if not signing_cert or not signing_key:
            raise CLIError("Both signing-cert and signing-key must be specified.")

        if not os.path.exists(signing_cert):
            raise CLIError(f"File {signing_cert} does not exist.")

        if not os.path.exists(signing_key):
            raise CLIError(f"File {signing_key} does not exist.")

    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)

    data = {}
    files = []
    if signing_cert_id:
        data["signingCertId"] = signing_cert_id
    else:
        files.append(
            ("SigningCertPemFile", ("SigningCertPemFile", open(signing_cert, "rb")))
        )
        files.append(
            ("SigningKeyPemFile", ("SigningKeyPemFile", open(signing_key, "rb")))
        )

    r = requests.post(f"{provider_endpoint}/configure", data=data, files=files)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def ccf_provider_show(cmd, provider_client_name):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)

    r = requests.get(f"{provider_endpoint}/show")
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_provider_remove(cmd, provider_client_name):
    from python_on_whales import DockerClient

    provider_client_name = get_provider_client_name(cmd.cli_ctx, provider_client_name)
    docker = DockerClient(
        compose_files=[ccf_provider_compose_file],
        compose_project_name=provider_client_name,
    )

    # Not setting the env variables fails the down command if variable used for volumes is not set.
    set_docker_compose_env_params()
    docker.compose.down()


def ccf_network_up(
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
    if not location:
        location = az_cli(
            f"group show --name {resource_group} --query location --output tsv"
        )

    from pathlib import Path

    # Create a workspace location and a unique string to name various azure resources.
    home_path = Path.home()
    if ws_folder:
        if not os.path.exists(ws_folder):
            raise CLIError(f"{ws_folder} does not exist")
    else:
        ws_folder = os.path.join(home_path, network_name + ".ccfworkspace")
        if not os.path.exists(ws_folder):
            os.makedirs(ws_folder)

    logger.warning(f"Using workspace folder location '{ws_folder}'.")

    unique_string_file = os.path.join(ws_folder, "unique_string.txt")
    if not os.path.exists(unique_string_file):
        value = str(uuid.uuid4())[:8]
        with open(unique_string_file, "w") as f:
            f.write(value)
    with open(unique_string_file, "r") as f:
        unique_string = f.read()

    recovery_mode_file = os.path.join(ws_folder, "recovery_mode.txt")
    if not os.path.exists(recovery_mode_file):
        value = recovery_mode
        with open(recovery_mode_file, "w") as f:
            f.write(value)
    with open(recovery_mode_file, "r") as f:
        value = f.read()
        if recovery_mode != value:
            raise CLIError(f"Cannot change recovery mode to {recovery_mode} once set.")

    sa_name = f"ccf{unique_string}sa"
    logger.warning(f"Creating storage account {sa_name}.")
    az_cli(
        f"storage account create --name {sa_name} --resource-group {resource_group} --allow-shared-key-access true --allow-blob-public-access false"
    )

    sa_id = az_cli(
        f"storage account show --name {sa_name} --resource-group {resource_group} --query id --output tsv"
    )
    subscription_id = az_cli("account show --query id -o tsv")
    logger.warning(f"Creating operator member cert and key.")
    operator_name = "ccf-operator"
    operator_cert_pem_file = os.path.join(ws_folder, f"{operator_name}_cert.pem")
    operator_enc_pubk_file = os.path.join(ws_folder, f"{operator_name}_enc_pubk.pem")
    if not os.path.exists(operator_cert_pem_file):
        from .custom import keygenerator_sh

        keygen_cmd = [
            "bash",
            keygenerator_sh,
            "--name",
            operator_name,
            "--out",
            ws_folder,
        ]

        if recovery_mode == "operator-recovery":
            keygen_cmd.append("--gen-enc-key")

        import subprocess

        result: subprocess.CompletedProcess
        result = subprocess.run(
            keygen_cmd,
            capture_output=True,
        )

        if not os.path.exists(operator_cert_pem_file):
            logger.warning(result)
            raise CLIError(f"Failed to generate {operator_cert_pem_file}.")
    else:
        logger.warning(f"operator member cert/key already exists.")

    operator_member = {
        "certificate": operator_cert_pem_file,
        "memberData": {"identifier": operator_name, "isOperator": True},
    }

    if recovery_mode == "operator-recovery":
        operator_member["encryptionPublicKey"] = operator_enc_pubk_file

    members = [operator_member]
    members_file = os.path.join(ws_folder, "members.json")
    with open(members_file, "w") as f:
        f.write(json.dumps(members, indent=2))

    provider_config = {
        "location": location,
        "subscriptionId": subscription_id,
        "resourceGroupName": resource_group,
        "azureFiles": {"storageAccountId": sa_id},
    }
    provider_config_file = os.path.join(ws_folder, "providerConfig.json")
    with open(provider_config_file, "w") as f:
        f.write(json.dumps(provider_config, indent=2))

    az_cli(f"cleanroom ccf provider deploy --name {provider_client_name}")

    recovery_service_name = ""
    if recovery_mode == "confidential-recovery":
        prepare_confidential_recovery_resources(
            unique_string, resource_group, ws_folder
        )
        recovery_resources_file = os.path.join(ws_folder, "recoveryResources.json")
        recovery_resources = get_file_json(recovery_resources_file)

        nw_join_policy = az_cli(
            "cleanroom ccf network security-policy generate-join-policy "
            + f"--security-policy-creation-option {security_policy_creation_option} "
        )
        nw_join_policy_file = os.path.join(ws_folder, "networkJoinPolicy.json")
        with open(nw_join_policy_file, "w") as f:
            f.write(json.dumps(nw_join_policy, indent=2))

        recovery_service_name = network_name + "-recovery"
        logger.warning(
            f"Creating confidential recovery service {recovery_service_name}."
        )
        az_cli(
            f"cleanroom ccf recovery-service create "
            + f"--name {recovery_service_name} "
            + f"--key-vault "
            + recovery_resources["kvId"]
            + " "
            + f"--maa-endpoint "
            + recovery_resources["maaEndpoint"]
            + " "
            + f"--identity "
            + recovery_resources["miId"]
            + " "
            + f"--ccf-network-join-policy {nw_join_policy_file} "
            + f"--provider-config {provider_config_file}"
        )

    logger.warning(
        f"Creating ccf network {network_name}... this might take 5 to 7 minutes."
    )
    ccf_endpoint = az_cli(
        f"cleanroom ccf network create "
        + f"--name {network_name} "
        + f"--members {members_file} "
        + f"--provider-config {provider_config_file} "
        + f"--security-policy-creation-option {security_policy_creation_option} "
        + f"--query endpoint "
        + f"--output tsv "
        + f"--provider-client {provider_client_name}"
    )

    r = requests.get(f"{ccf_endpoint}/node/network", verify=False)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    service_cert = r.json()["service_certificate"]
    service_cert_file = os.path.join(ws_folder, "service_cert.pem")
    with open(service_cert_file, "w") as f:
        f.write(service_cert)

    operator_cert_key_file = os.path.join(ws_folder, f"{operator_name}_privk.pem")
    cgs_client_name = f"{network_name}-operator-governance"

    az_cli(
        f"cleanroom governance client deploy "
        + f"--ccf-endpoint {ccf_endpoint} "
        + f"--signing-key {operator_cert_key_file} "
        + f"--signing-cert {operator_cert_pem_file} "
        + f"--service-cert {service_cert_file} "
        + f"--name {cgs_client_name}"
    )

    az_cli(
        f"cleanroom governance member activate --governance-client {cgs_client_name}"
    )

    az_cli(
        f"cleanroom ccf provider configure "
        + f"--signing-key {operator_cert_key_file} "
        + f"--signing-cert {operator_cert_pem_file} "
        + f"--name {provider_client_name}"
    )

    if recovery_mode == "confidential-recovery":
        logger.warning(
            f"Configuring recovery service {recovery_service_name} for recovering ccf network {network_name}."
        )
        cm_name = recovery_resources["confidentialRecovererMemberName"]
        az_cli(
            "cleanroom ccf network configure-confidential-recovery "
            + f"--name {network_name} "
            + f"--recovery-service-name {recovery_service_name} "
            + f"--recovery-member-name {cm_name} "
            + f"--provider-config {provider_config_file} "
        )
        az_cli(
            f"cleanroom ccf network set-recovery-threshold "
            + f"--name {network_name} "
            + f"--recovery-threshold 1 "
            + f"--provider-config {provider_config_file} "
            + f"--provider-client {provider_client_name}"
        )

    az_cli(
        f"cleanroom ccf network transition-to-open "
        + f"--name {network_name} "
        + f"--provider-config {provider_config_file} "
        + f"--provider-client {provider_client_name}"
    )

    logger.warning(
        f"CCF network is up:\n  Endpoint: {ccf_endpoint}\n  Workspace folder location: {ws_folder}"
    )
    logger.warning(
        f"Query details via commands such as:\n  az cleanroom ccf network show --name {network_name} --provider-config {provider_config_file}"
    )


def ccf_network_create(
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
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    security_policy_config = to_security_policy_config(
        security_policy_creation_option, security_policy
    )
    if os.path.exists(members):
        members = get_file_json(members)
    else:
        members = shell_safe_json_parse(members)

    # Expand the PEM input if file paths were specified.
    for index, member in enumerate(members):
        if os.path.exists(member["certificate"]):
            with open(member["certificate"], "r") as f:
                member["certificate"] = f.read()
        if "encryptionPublicKey" in member and os.path.exists(
            member["encryptionPublicKey"]
        ):
            with open(member["encryptionPublicKey"], "r") as f:
                member["encryptionPublicKey"] = f.read()

    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "nodeCount": node_count,
        "nodeLogLevel": node_log_level,
        "securityPolicy": security_policy_config,
        "members": members,
        "providerConfig": provider_config,
    }
    logger.warning(
        f"Run `docker compose -p {provider_client_name} logs -f` to monitor network creation progress."
    )
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/create?async=true", json=content
    )
    if r.status_code != 202:
        raise CLIError(response_error_message(r))

    return track_operation(provider_endpoint, r)


def ccf_network_delete(
    cmd, network_name, infra_type, delete_option, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {"infraType": infra_type, "providerConfig": provider_config}

    if delete_option:
        if delete_option == "delete-storage":
            content["deleteOption"] = "DeleteStorage"
        elif delete_option == "retain-storage":
            content["deleteOption"] = "RetainStorage"
        else:
            raise CLIError(f"Unmapped option value {delete_option}. Fix this.")

    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/delete", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def ccf_network_update(
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
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    security_policy_config = to_security_policy_config(
        security_policy_creation_option, security_policy
    )

    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "nodeCount": node_count,
        "nodeLogLevel": node_log_level,
        "securityPolicy": security_policy_config,
        "providerConfig": provider_config,
    }
    logger.warning(
        f"Run `docker compose -p {provider_client_name} logs -f` to monitor network update progress."
    )
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/update", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_recover_public_network(
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

    if not os.path.exists(previous_service_cert):
        raise CLIError(f"File {previous_service_cert} does not exist.")

    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    security_policy_config = to_security_policy_config(
        security_policy_creation_option, security_policy
    )

    provider_config = parse_provider_config(provider_config, infra_type)

    with open(previous_service_cert, "r") as f:
        previous_service_cert = f.read()

    content = {
        "infraType": infra_type,
        "targetNetworkName": target_network_name,
        "previousServiceCertificate": previous_service_cert,
        "nodeCount": node_count,
        "nodeLogLevel": node_log_level,
        "securityPolicy": security_policy_config,
        "providerConfig": provider_config,
    }
    logger.warning(
        f"Run `docker compose -p {provider_client_name} logs -f` to monitor network recovery progress."
    )
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/recoverPublicNetwork",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_submit_recovery_share(
    cmd,
    network_name,
    infra_type,
    encryption_private_key,
    encryption_key_id,
    provider_config,
    provider_client_name,
):
    if not encryption_private_key and not encryption_key_id:
        raise CLIError(
            "Either encryption-private-key or encryption-key-id must be specified."
        )

    if encryption_private_key and encryption_key_id:
        raise CLIError(
            "Both encryption-private-key and encryption-key-id cannot be specified."
        )

    if encryption_private_key:
        if not os.path.exists(encryption_private_key):
            raise CLIError(f"File {encryption_private_key} does not exist.")
        with open(encryption_private_key, "r") as f:
            encryption_private_key = f.read()

    if encryption_key_id and os.path.exists(encryption_key_id):
        with open(encryption_key_id, "r") as f:
            encryption_key_id = f.read()

    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)

    provider_config = parse_provider_config(provider_config, infra_type)

    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    if encryption_private_key:
        content["encryptionPrivateKey"] = encryption_private_key
    else:
        content["encryptionKeyId"] = encryption_key_id

    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/submitRecoveryShare",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_recover(
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
    if not os.path.exists(previous_service_cert):
        raise CLIError(f"File {previous_service_cert} does not exist.")

    if encryption_private_key or encryption_key_id:
        if encryption_private_key and encryption_key_id:
            raise CLIError(
                "Both operator-recovery-encryption-private-key and operator-recovery-encryption-key-id cannot be specified."
            )

        if recovery_service_name or member_name:
            raise CLIError(
                "Either (--operator-recovery-encryption-private-key/operator-recovery-encryption-key-id) or "
                + "(--confidential-recovery-member-name and --confidential-recovery-service-name) "
                + "must be specified."
            )

        if encryption_private_key and not os.path.exists(encryption_private_key):
            raise CLIError(f"File {encryption_private_key} does not exist.")
        with open(encryption_private_key, "r") as f:
            encryption_private_key = f.read()

        if encryption_key_id and os.path.exists(encryption_key_id):
            with open(encryption_key_id, "r") as f:
                encryption_key_id = f.read()
    else:
        if not recovery_service_name or not member_name:
            raise CLIError(
                "Either (--operator-recovery-encryption-private-key/operator-recovery-encryption-key-id) or "
                + "(--confidential-recovery-member-name and --confidential-recovery-service-name) "
                + "must be specified."
            )

    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    security_policy_config = to_security_policy_config(
        security_policy_creation_option, security_policy
    )

    provider_config = parse_provider_config(provider_config, infra_type)

    with open(previous_service_cert, "r") as f:
        previous_service_cert = f.read()

    content = {
        "infraType": infra_type,
        "previousServiceCertificate": previous_service_cert,
        "nodeLogLevel": node_log_level,
        "securityPolicy": security_policy_config,
        "providerConfig": provider_config,
    }

    if encryption_private_key:
        content["operatorRecovery"] = {
            "encryptionPrivateKey": encryption_private_key,
        }
    elif encryption_key_id:
        content["operatorRecovery"] = {
            "encryptionKeyId": encryption_key_id,
        }
    else:
        content["confidentialRecovery"] = {
            "memberName": member_name,
            "recoveryServiceName": recovery_service_name,
        }

    logger.warning(
        f"Run `docker compose -p {provider_client_name} logs -f` to monitor network recovery progress."
    )
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/recover?async=true", json=content
    )
    if r.status_code != 202:
        raise CLIError(response_error_message(r))

    return track_operation(provider_endpoint, r)


def ccf_network_show(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(f"{provider_endpoint}/networks/{network_name}/get", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_recovery_agent_show(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/recoveryagents/get",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_recovery_agent_show_report(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/recoveryagents/report",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_recovery_agent_show_network_report(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/recoveryagents/network/report",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_recovery_agent_generate_member(
    cmd,
    network_name,
    member_name,
    infra_type,
    agent_config,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    agent_config = parse_agent_config(agent_config)
    content = {
        "infraType": infra_type,
        "memberName": member_name,
        "providerConfig": provider_config,
        "agentConfig": agent_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/recoveryagents/recoverymembers/generate",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_recovery_agent_activate_member(
    cmd,
    network_name,
    member_name,
    infra_type,
    agent_config,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    agent_config = parse_agent_config(agent_config)
    content = {
        "infraType": infra_type,
        "memberName": member_name,
        "providerConfig": provider_config,
        "agentConfig": agent_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/recoveryagents/recoverymembers/activate",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def ccf_network_recovery_agent_submit_recovery_share(
    cmd,
    network_name,
    member_name,
    infra_type,
    agent_config,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    agent_config = parse_agent_config(agent_config)
    content = {
        "infraType": infra_type,
        "memberName": member_name,
        "providerConfig": provider_config,
        "agentConfig": agent_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/recoveryagents/recoverymembers/submitrecoveryshare",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_recovery_agent_set_network_join_policy(
    cmd,
    network_name,
    infra_type,
    agent_config,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    agent_config = parse_agent_config(agent_config)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
        "agentConfig": agent_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/recoveryagents/network/joinpolicy/set",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def ccf_network_show_health(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/health", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_show_report(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/report", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_trigger_snapshot(
    cmd, network_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/snapshots/trigger", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_transition_to_open(
    cmd,
    network_name,
    infra_type,
    previous_service_cert,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }

    if previous_service_cert and os.path.exists(previous_service_cert):
        with open(previous_service_cert, "r") as f:
            previous_service_cert = f.read()

    if previous_service_cert:
        content["previousServiceCertificate"] = previous_service_cert

    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/transitionToOpen", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_security_policy_generate(
    cmd,
    infra_type,
    security_policy_creation_option,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    policy_option = to_security_policy_option(security_policy_creation_option)

    content = {"infraType": infra_type, "securityPolicyCreationOption": policy_option}

    r = requests.post(
        f"{provider_endpoint}/networks/generateSecurityPolicy", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_security_policy_generate_join_policy(
    cmd,
    infra_type,
    security_policy_creation_option,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    policy_option = to_security_policy_option(security_policy_creation_option)

    content = {"infraType": infra_type, "securityPolicyCreationOption": policy_option}

    r = requests.post(f"{provider_endpoint}/networks/generateJoinPolicy", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_security_policy_generate_join_policy_from_network(
    cmd,
    infra_type,
    network_name,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }

    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/generateJoinPolicy", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_join_policy_add_snp_host_data(
    cmd,
    network_name,
    infra_type,
    host_data,
    security_policy,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    security_policy_base64 = ""
    if security_policy:
        if not os.path.exists(security_policy):
            raise CLIError(f"File {security_policy} does not exist.")
        with open(security_policy, "r") as f:
            security_policy = f.read()
            security_policy_base64 = base64.b64encode(
                bytes(security_policy, "utf-8")
            ).decode("utf-8")
            security_policy_hash = hashlib.sha256(
                bytes(security_policy, "utf-8")
            ).hexdigest()
            if security_policy_hash != host_data:
                raise CLIError(
                    f"Security policy hash {security_policy_hash} does not match supplied host data {host_data}."
                )

    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
        "hostData": host_data,
        "securityPolicy": security_policy_base64,
    }

    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/addSnpHostData", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_join_policy_remove_snp_host_data(
    cmd,
    network_name,
    infra_type,
    host_data,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)

    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
        "hostData": host_data,
    }

    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/removeSnpHostData", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_join_policy_show(
    cmd,
    network_name,
    infra_type,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }

    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/getJoinPolicy", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_set_recovery_threshold(
    cmd,
    network_name,
    infra_type,
    recovery_threshold: int,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
        "recoveryThreshold": recovery_threshold,
    }

    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/setRecoveryThreshold",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_network_configure_confidential_recovery(
    cmd,
    network_name,
    recovery_service_name,
    recovery_member_name,
    infra_type,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
        "recoveryServiceName": recovery_service_name,
        "recoveryMemberName": recovery_member_name,
    }

    r = requests.post(
        f"{provider_endpoint}/networks/{network_name}/configureConfidentialRecovery",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def get_provider_client_endpoint(cmd, provider_client_name: str):
    port = get_provider_client_port(cmd, provider_client_name)
    return f"http://localhost:{port}"


def get_provider_client_port(cmd, provider_client_name: str):
    provider_client_name = get_provider_client_name(cmd.cli_ctx, provider_client_name)

    # Note (gsinha): Not using python_on_whales here as its load time is found to be slow and this
    # method gets invoked frequently to determin the client port. using the docker package instead.
    # from python_on_whales import DockerClient, exceptions

    import docker

    client = docker.from_env()
    try:
        container_name = f"{provider_client_name}-client-1"
        container = client.containers.get(container_name)
        port = container.ports["8080/tcp"][0]["HostPort"]
        # docker = DockerClient(
        #     compose_files=[compose_file], compose_project_name=provider_client_name
        # )
        # (_, port) = docker.compose.port(service="client", private_port=8080)
        return port
    # except exceptions.DockerException as e:
    except Exception as e:
        # Perhaps the client was started without docker compose and if so the container name might
        # be directly supplied as input.
        try:
            container_name = f"{provider_client_name}"
            container = client.containers.get(container_name)
            port = container.ports["8080/tcp"][0]["HostPort"]
            return port
        except Exception as e:
            raise CLIError(
                f"Not finding a client instance running with name '{provider_client_name}'. Check "
                + "the --provider-client parameter value."
            ) from e


def get_provider_client_name(cli_ctx, provider_client_name):
    if provider_client_name != "":
        return provider_client_name

    provider_client_name = cli_ctx.config.get(
        "cleanroom", "ccf.provider.client_name", ""
    )

    if provider_client_name == "":
        raise CLIError(
            "--provider-client=<value> parameter must be specified or set a default "
            + "value via `az config set cleanroom ccf.provider.client_name=<value>`"
        )

    logger.debug('Current value of "provider_client_name": %s.', provider_client_name)
    return provider_client_name


def set_docker_compose_env_params():
    os.environ["AZCLI_CCF_PROVIDER_CLIENT_WORKSPACE_DIR"] = ccf_provider_workspace_dir
    uid = os.getuid()
    gid = os.getgid()
    os.environ["AZCLI_CCF_PROVIDER_UID"] = str(uid)
    os.environ["AZCLI_CCF_PROVIDER_GID"] = str(gid)

    # To suppress warning below during docker compose execution set the env. variable if not set:
    # WARN[0000] The "GITHUB_ACTIONS" variable is not set. Defaulting to a blank string.
    if "GITHUB_ACTIONS" not in os.environ:
        os.environ["GITHUB_ACTIONS"] = "false"
    if "CODESPACES" not in os.environ:
        os.environ["CODESPACES"] = "false"
    if "AZCLI_CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE"] = ""
    if "AZCLI_CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE"] = ""
    if "AZCLI_CCF_PROVIDER_RECOVERY_AGENT_IMAGE" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_RECOVERY_AGENT_IMAGE"] = ""
    if "AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_IMAGE" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_IMAGE"] = ""
    if "AZCLI_CCF_PROVIDER_CONSORTIUM_MANAGER_IMAGE" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_CONSORTIUM_MANAGER_IMAGE"] = ""
    if "AZCLI_CCF_PROVIDER_PROXY_IMAGE" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_PROXY_IMAGE"] = ""
    if "AZCLI_CCF_PROVIDER_ATTESTATION_IMAGE" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_ATTESTATION_IMAGE"] = ""
    if "AZCLI_CCF_PROVIDER_SKR_IMAGE" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_SKR_IMAGE"] = ""
    if "AZCLI_CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL"] = ""
    if (
        "AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL"
        ] = ""
    if (
        "AZCLI_CCF_PROVIDER_CONSORTIUM_MANAGER_SECURITY_POLICY_DOCUMENT_URL"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CCF_PROVIDER_CONSORTIUM_MANAGER_SECURITY_POLICY_DOCUMENT_URL"
        ] = ""
    if "AZCLI_CCF_PROVIDER_CONTAINER_REGISTRY_URL" not in os.environ:
        os.environ["AZCLI_CCF_PROVIDER_CONTAINER_REGISTRY_URL"] = ""


def requires_provider_config(infra_type):
    return infra_type == "caci"


def parse_provider_config(provider_config, infra_type):
    if provider_config:
        if os.path.exists(provider_config):
            provider_config = get_file_json(provider_config)
        else:
            provider_config = shell_safe_json_parse(provider_config)

    if not provider_config and requires_provider_config(infra_type):
        raise CLIError(
            f"--provider-config parameter must be specified for infra type {infra_type}"
        )

    return provider_config


def parse_agent_config(agent_config):
    if agent_config:
        if os.path.exists(agent_config):
            agent_config = get_file_json(agent_config)
        else:
            agent_config = shell_safe_json_parse(agent_config)

    if not agent_config:
        raise CLIError(f"--agent-config parameter must be specified")

    return agent_config


def to_security_policy_config(security_policy_creation_option, security_policy):
    policy_option = to_security_policy_option(security_policy_creation_option)
    if policy_option == "userSupplied" and not security_policy:
        raise CLIError(
            f"No --security-policy specified with user-supplied creation option."
        )

    if policy_option != "userSupplied" and security_policy:
        raise CLIError(
            f"--security-policy-creation-option must be user-supplied when passing --security-policy."
        )

    if security_policy:
        if os.path.exists(security_policy):
            with open(security_policy, "r") as f:
                content = f.read()
                security_policy = base64.b64encode(bytes(content, "utf-8")).decode(
                    "utf-8"
                )
        security_policy_config = {
            "policy": security_policy,
            "policyCreationOption": "userSupplied",
        }
        return security_policy_config

    security_policy_config = {"policyCreationOption": policy_option}
    return security_policy_config


def to_security_policy_option(security_policy_creation_option):
    if security_policy_creation_option == "cached":
        policy_option = "cached"
    elif security_policy_creation_option == "cached-debug":
        policy_option = "cachedDebug"
    elif security_policy_creation_option == "allow-all":
        policy_option = "allowAll"
    elif security_policy_creation_option == "user-supplied":
        policy_option = "userSupplied"
    else:
        raise CLIError(
            f"Option {security_policy_creation_option} not handled. Fix this."
        )
    return policy_option


def prepare_confidential_recovery_resources(unique_string, resource_group, ws_folder):
    kv_name = f"ccf{unique_string}akv"
    mi_name = f"ccf{unique_string}mi"

    logger.warning("Preparing resources for confidential recovery service...")

    kv_id = az_cli(f"keyvault list --resource-group {resource_group} --query [].name")

    if not kv_id or kv_name not in kv_id:
        logger.warning(f"  Creating key vault {kv_name}.")
        az_cli(
            f"keyvault create --resource-group {resource_group} --name {kv_name} --sku premium --enable-rbac-authorization true --enable-purge-protection true"
        )
    else:
        logger.warning(f"  Key vault {kv_name} already exists.")

    kv_id = az_cli(
        f"keyvault show --resource-group {resource_group} --name {kv_name} --query id --output tsv"
    )

    logger.warning(f"  Creating managed identity {mi_name}.")
    az_cli(f"identity create --name {mi_name} --resource-group {resource_group}")

    mi_id = az_cli(
        f"identity show --name {mi_name} --resource-group {resource_group} --query id --output tsv"
    )

    mi_app_id = az_cli(
        f"identity show --name {mi_name} --resource-group {resource_group} --query principalId --output tsv"
    )

    principal_type = "ServicePrincipal"
    # https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles
    role_name_to_id = {
        "Key Vault Crypto Officer": "14b46e9e-c2b7-41b4-b07b-48a6ebf60603",
        "Key Vault Secrets Officer": "b86a8fe4-44ce-4948-aee5-eccb2c155cd7",
    }
    for role in ["Key Vault Crypto Officer", "Key Vault Secrets Officer"]:
        role_id = role_name_to_id[role]
        logger.warning(
            f"  Assigning role {role} on key vault {kv_name} for identity {mi_name}."
        )
        max_retries = 10
        delay = 5
        attempt = 0
        while attempt < max_retries:
            try:
                az_cli(
                    f"role assignment create --role {role_id} --scope {kv_id} --assignee-object-id {mi_app_id} --assignee-principal-type {principal_type}"
                )
                break
            except:
                logger.warning(
                    f"Hit failure during setting rbac permissions. Retrying in {delay} seconds..."
                )
                attempt += 1
                time.sleep(delay)
        if attempt == max_retries:
            raise CLIError(
                f"Failed setting {role} permissions on key vualt {kv_name} for managed identity {mi_name}."
            )

    cm_name = f"conf-recoverer-{unique_string}"
    resources = {
        "kvId": kv_id,
        "confidentialRecovererMemberName": cm_name,
        "miId": mi_id,
        "maaEndpoint": "sharedneu.neu.attest.azure.net",
    }

    prepare_recovery_resources_file = os.path.join(ws_folder, "recoveryResources.json")
    with open(prepare_recovery_resources_file, "w") as f:
        f.write(json.dumps(resources, indent=2))


def track_operation(provider_endpoint, response: requests.Response):
    progress_till_now = []
    request_url = response.request.url
    request_method = response.request.method
    while True:
        operation_location = response.headers["Operation-Location"]
        if not operation_location:
            raise CLIError(
                f"Operation-Location header not found in response for {request_method} {request_url}."
            )
        response = requests.get(f"{provider_endpoint}{operation_location}")
        if response.status_code != 200:
            raise CLIError(response_error_message(response))
        operation = response.json()
        progress_till_now = display_progress(progress_till_now, operation)
        if operation["status"] == "Succeeded":
            return operation["resource"]
        if operation["status"] == "Failed":
            status_code = operation["statusCode"]
            raise CLIError(
                f"{request_method} {request_url} failed with status: {status_code} response: {json.dumps(operation, indent=2)}"
            )
        time.sleep(2)


def display_progress(progress_till_now, operation):
    if operation["progress"] is not None:
        to_display = [
            item for item in operation["progress"] if item not in progress_till_now
        ]
        progress_till_now = operation["progress"]
    for item in to_display:
        logger.warning(item)
    return progress_till_now
