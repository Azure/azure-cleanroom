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
import json
import os
import uuid
from time import sleep

import requests
from azure.cli.core.util import CLIError, get_file_json, shell_safe_json_parse
from knack.log import get_logger

from .custom import response_error_message
from .utilities._azcli_helpers import az_cli

logger = get_logger(__name__)

cluster_provider_compose_file: str = f"{os.path.dirname(__file__)}{os.path.sep}data{os.path.sep}cluster-provider{os.path.sep}docker-compose.yaml"


def cluster_provider_deploy(cmd, provider_client_name, env_file=None):
    from python_on_whales import DockerClient

    docker = DockerClient(
        compose_files=[cluster_provider_compose_file],
        compose_project_name=provider_client_name,
    )

    # Read and set environment variables from file if provided
    if env_file:
        if not os.path.exists(env_file):
            raise CLIError(f"Environment file not found: {env_file}")

        logger.warning(f"Loading environment variables from: {env_file}")
        with open(env_file, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#") and "=" in line:
                    key, value = line.split("=", 1)
                    os.environ[key.strip()] = value.strip()
                    logger.debug(f"Set environment variable: {key.strip()}")

    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE" in os.environ:
        image = os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE"]
        logger.warning(
            f"Using cleanroom-cluster-provider-client image from override url: {image}"
        )

    set_docker_compose_env_params()
    docker.compose.up(remove_orphans=True, detach=True)
    _, port = docker.compose.port(service="client", private_port=8080)

    cluster_provider_endpoint = f"http://localhost:{port}"
    while True:
        try:
            r = requests.get(f"{cluster_provider_endpoint}/ready")
            if r.status_code == 200:
                break
            else:
                logger.warning(
                    f"Waiting for cleanroom-cluster-provider-client endpoint to be up... (status code: {r.status_code})"
                )
                sleep(5)
        except:
            logger.warning(
                "Waiting for cleanroom-cluster-provider-client endpoint to be up..."
            )
            sleep(5)

    logger.warning(
        "cleanroom-cluster-provider-client container is listening on %s.", port
    )


def cluster_provider_remove(cmd, provider_client_name):
    from python_on_whales import DockerClient

    provider_client_name = get_provider_client_name(cmd.cli_ctx, provider_client_name)
    docker = DockerClient(
        compose_files=[cluster_provider_compose_file],
        compose_project_name=provider_client_name,
    )

    # Not setting the env variables fails the down command if variable used for volumes is not set.
    set_docker_compose_env_params()
    docker.compose.down()


def cluster_up(
    cmd,
    cluster_name,
    infra_type,
    resource_group,
    ws_folder,
    location,
    node_vm_size,
    provider_client_name,
    initial_node_count=None,
    ip_tags=None,
    env_file=None,
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
        ws_folder = os.path.join(home_path, cluster_name + ".crcworkspace")
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

    subscription_id = az_cli("account show --query id -o tsv")
    tenant_id = az_cli("account show --query tenantId -o tsv")

    provider_config = {
        "location": location,
        "subscriptionId": subscription_id,
        "resourceGroupName": resource_group,
        "tenantId": tenant_id,
    }
    if node_vm_size:
        provider_config["nodeVmSize"] = node_vm_size
    # Seed the AKS agent pool with an initial node count. When omitted the cluster
    # provider defaults the pool to its own minimum (2..5 autoscale).
    if initial_node_count:
        provider_config["initialNodeCount"] = initial_node_count
    # Only tag the AKS setup's public IPs when the caller supplies service tags via
    # --ip-tags. When omitted, the IPs are created untagged. Each --ip-tags entry uses the
    # Azure CLI 'IpTagType=Tag' syntax and is mapped to the Azure Public IP Address IpTag
    # schema (ipTagType + tag):
    # https://learn.microsoft.com/en-us/rest/api/virtualnetwork/public-ip-addresses/create-or-update#common.iptag
    if ip_tags:
        provider_config["ipTags"] = parse_ip_tags(ip_tags)
    provider_config_file = os.path.join(ws_folder, "providerConfig.json")
    with open(provider_config_file, "w") as f:
        f.write(json.dumps(provider_config, indent=2))

    # Pass env-file to cluster provider deploy if specified
    deploy_cmd = f"cleanroom cluster provider deploy --name {provider_client_name}"
    if env_file:
        if not os.path.exists(env_file):
            raise CLIError(f"Environment file not found: {env_file}")
        deploy_cmd += f" --env-file {env_file}"

    az_cli(deploy_cmd)

    logger.warning(
        f"Creating cluster {cluster_name} in resource group {resource_group} ... this might take 5 to 10 minutes."
    )
    az_cli(
        f"cleanroom cluster create "
        + f"--name {cluster_name} "
        + f"--enable-observability "
        + f"--provider-config {provider_config_file} "
        + f"--provider-client {provider_client_name}"
    )

    logger.warning(f"Cluster is up:\n  Workspace folder location: {ws_folder}")
    logger.warning(
        f"Query details via commands such as:\n  az cleanroom cluster show --name {cluster_name} --provider-config {provider_config_file}"
    )


def cluster_create(
    cmd,
    cluster_name,
    infra_type,
    provider_config,
    node_vm_size,
    enable_observability,
    enable_monitoring,
    enable_analytics_workload,
    analytics_workload_config_url,
    analytics_workload_config_url_ca_cert,
    analytics_workload_disable_telemetry,
    analytics_workload_security_policy_creation_option,
    analytics_workload_security_policy,
    analytics_workload_pool_node_count,
    enable_kserve_inferencing_workload,
    kserve_inferencing_workload_config_url,
    kserve_inferencing_workload_config_url_ca_cert,
    kserve_inferencing_workload_disable_telemetry_collection,
    kserve_inferencing_workload_security_policy_creation_option,
    kserve_inferencing_workload_security_policy,
    flex_node_profile,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    from .custom_ccf import to_security_policy_config

    analytics_security_policy_config = to_security_policy_config(
        analytics_workload_security_policy_creation_option,
        analytics_workload_security_policy,
    )

    kserve_inferencing_security_policy_config = to_security_policy_config(
        kserve_inferencing_workload_security_policy_creation_option,
        kserve_inferencing_workload_security_policy,
    )

    provider_config = parse_provider_config(provider_config, infra_type)
    if node_vm_size:
        provider_config["nodeVmSize"] = node_vm_size
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }

    if enable_observability:
        content["observabilityProfile"] = {"enabled": True}

    if enable_monitoring:
        content["monitoringProfile"] = {"enabled": True}

    if enable_analytics_workload:
        content["analyticsWorkloadProfile"] = {
            "enabled": True,
            "telemetryProfile": {
                "collectionEnabled": not analytics_workload_disable_telemetry,
            },
            "configurationUrl": analytics_workload_config_url,
            "configurationUrlCaCert": analytics_workload_config_url_ca_cert,
            "securityPolicy": analytics_security_policy_config,
        }
        if analytics_workload_pool_node_count:
            content["analyticsWorkloadProfile"]["poolProfile"] = {
                "nodeCount": analytics_workload_pool_node_count
            }

    if enable_kserve_inferencing_workload:
        content["inferencingWorkloadProfile"] = {
            "kserveProfile": {
                "enabled": True,
                "telemetryProfile": {
                    "collectionEnabled": not kserve_inferencing_workload_disable_telemetry_collection,
                },
                "configurationUrl": kserve_inferencing_workload_config_url,
                "configurationUrlCaCert": kserve_inferencing_workload_config_url_ca_cert,
                "securityPolicy": kserve_inferencing_security_policy_config,
            }
        }

    if flex_node_profile:
        content["flexNodeProfile"] = build_flex_node_profile(flex_node_profile)

    logger.warning(
        f"Run `docker compose -p {provider_client_name} logs -f` to monitor cluster creation progress."
    )
    r = requests.post(
        f"{provider_endpoint}/clusters/{cluster_name}/create?async=true", json=content
    )
    if r.status_code != 202:
        raise CLIError(response_error_message(r))

    from .custom_ccf import track_operation

    return track_operation(provider_endpoint, r)


def cluster_update(
    cmd,
    cluster_name,
    infra_type,
    provider_config,
    enable_observability,
    enable_monitoring,
    enable_analytics_workload,
    analytics_workload_config_url,
    analytics_workload_config_url_ca_cert,
    analytics_workload_disable_telemetry_collection,
    analytics_workload_security_policy_creation_option,
    analytics_workload_security_policy,
    analytics_workload_pool_node_count,
    enable_kserve_inferencing_workload,
    kserve_inferencing_workload_config_url,
    kserve_inferencing_workload_config_url_ca_cert,
    kserve_inferencing_workload_disable_telemetry_collection,
    kserve_inferencing_workload_security_policy_creation_option,
    kserve_inferencing_workload_security_policy,
    flex_node_profile,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    from .custom_ccf import to_security_policy_config

    analytics_security_policy_config = to_security_policy_config(
        analytics_workload_security_policy_creation_option,
        analytics_workload_security_policy,
    )

    kserve_inferencing_security_policy_config = to_security_policy_config(
        kserve_inferencing_workload_security_policy_creation_option,
        kserve_inferencing_workload_security_policy,
    )

    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }

    if enable_observability:
        content["observabilityProfile"] = {"enabled": True}

    if enable_monitoring:
        content["monitoringProfile"] = {"enabled": True}

    if enable_analytics_workload:
        content["analyticsWorkloadProfile"] = {
            "enabled": True,
            "telemetryProfile": {
                "collectionEnabled": not analytics_workload_disable_telemetry_collection,
            },
            "configurationUrl": analytics_workload_config_url,
            "configurationUrlCaCert": analytics_workload_config_url_ca_cert,
            "securityPolicy": analytics_security_policy_config,
            "poolProfile": {"nodeCount": analytics_workload_pool_node_count},
        }

    if enable_kserve_inferencing_workload:
        content["inferencingWorkloadProfile"] = {
            "kserveProfile": {
                "enabled": True,
                "telemetryProfile": {
                    "collectionEnabled": not kserve_inferencing_workload_disable_telemetry_collection,
                },
                "configurationUrl": kserve_inferencing_workload_config_url,
                "configurationUrlCaCert": kserve_inferencing_workload_config_url_ca_cert,
                "securityPolicy": kserve_inferencing_security_policy_config,
            }
        }

    if flex_node_profile:
        content["flexNodeProfile"] = build_flex_node_profile(flex_node_profile)

    logger.warning(
        f"Run `docker compose -p {provider_client_name} logs -f` to monitor cluster update progress."
    )
    r = requests.post(
        f"{provider_endpoint}/clusters/{cluster_name}/update?async=true", json=content
    )
    if r.status_code != 202:
        raise CLIError(response_error_message(r))

    from .custom_ccf import track_operation

    return track_operation(provider_endpoint, r)


def cluster_show(cmd, cluster_name, infra_type, provider_config, provider_client_name):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(f"{provider_endpoint}/clusters/{cluster_name}/get", json=content)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def cluster_show_health(
    cmd, cluster_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/clusters/{cluster_name}/health", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def cluster_delete(
    cmd, cluster_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/clusters/{cluster_name}/delete", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))


def cluster_get_kubeconfig(
    cmd,
    cluster_name,
    infra_type,
    file,
    provider_config,
    provider_client_name,
    access_role,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
        "accessRole": access_role,
    }
    r = requests.post(
        f"{provider_endpoint}/clusters/{cluster_name}/getkubeconfig", json=content
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    kubeconfig = r.json()["kubeconfig"]
    with open(file, "w") as f:
        f.write(base64.b64decode(kubeconfig).decode())
    logger.warning(f"kubeconfig written out to {file}.")


def cluster_analytics_workload_deployment_generate(
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
    if not os.path.exists(output_dir):
        raise CLIError(f"Output folder location {output_dir} does not exist.")

    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    from .custom import get_cgs_client_port

    cgs_port = get_cgs_client_port(cmd, gov_client_name)
    contract_url = f"http://host.docker.internal:{cgs_port}/contracts/{contract_id}"
    from .custom_ccf import to_security_policy_config

    security_policy_config = to_security_policy_config(
        security_policy_creation_option, None
    )

    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
        "contractUrl": contract_url,
        "telemetryProfile": {
            "collectionEnabled": not disable_telemetry_collection,
        },
        "contractUrlCaCert": None,
        "securityPolicy": security_policy_config,
    }
    r = requests.post(
        f"{provider_endpoint}/clusters/analyticsWorkload/generateDeployment",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    with open(
        output_dir + f"{os.path.sep}analytics-workload.deployment-template.json", "w"
    ) as f:
        f.write(json.dumps(r.json()["deploymentTemplate"], indent=2))
    with open(
        output_dir + f"{os.path.sep}analytics-workload.governance-policy.json", "w"
    ) as f:
        f.write(json.dumps(r.json()["governancePolicy"], indent=2))
    with open(
        output_dir + f"{os.path.sep}analytics-workload.cce-policy.rego", "w"
    ) as f:
        f.write(base64.b64decode(r.json()["ccePolicy"]["value"]).decode())
    with open(
        output_dir + f"{os.path.sep}analytics-workload.cce-policy.json", "w"
    ) as f:
        f.write((json.dumps(r.json()["ccePolicy"], indent=2)))


def cluster_kserve_inferencing_workload_deployment_generate(
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
    if not os.path.exists(output_dir):
        raise CLIError(f"Output folder location {output_dir} does not exist.")

    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    from .custom import get_cgs_client_port

    cgs_port = get_cgs_client_port(cmd, gov_client_name)
    contract_url = f"http://host.docker.internal:{cgs_port}/contracts/{contract_id}"
    from .custom_ccf import to_security_policy_config

    security_policy_config = to_security_policy_config(
        security_policy_creation_option, None
    )

    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
        "contractUrl": contract_url,
        "telemetryProfile": {
            "collectionEnabled": not disable_telemetry_collection,
        },
        "contractUrlCaCert": None,
        "securityPolicy": security_policy_config,
    }
    r = requests.post(
        f"{provider_endpoint}/clusters/kserveInferencingWorkload/generateDeployment",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    with open(
        output_dir
        + f"{os.path.sep}kserve-inferencing-workload.deployment-template.json",
        "w",
    ) as f:
        f.write(json.dumps(r.json()["deploymentTemplate"], indent=2))
    with open(
        output_dir + f"{os.path.sep}kserve-inferencing-workload.governance-policy.json",
        "w",
    ) as f:
        f.write(json.dumps(r.json()["governancePolicy"], indent=2))
    with open(
        output_dir + f"{os.path.sep}kserve-inferencing-workload.cce-policy.rego", "w"
    ) as f:
        f.write(base64.b64decode(r.json()["ccePolicy"]["value"]).decode())
    with open(
        output_dir + f"{os.path.sep}kserve-inferencing-workload.cce-policy.json", "w"
    ) as f:
        f.write((json.dumps(r.json()["ccePolicy"], indent=2)))


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
        "cleanroom", "cluster.provider.client_name", ""
    )

    if provider_client_name == "":
        raise CLIError(
            "--provider-client=<value> parameter must be specified or set a default "
            + "value via `az config set cleanroom cluster.provider.client_name=<value>`"
        )

    logger.debug('Current value of "provider_client_name": %s.', provider_client_name)
    return provider_client_name


def set_docker_compose_env_params():
    uid = os.getuid()
    gid = os.getgid()
    os.environ["AZCLI_CLEANROOM_CLUSTER_UID"] = str(uid)
    os.environ["AZCLI_CLEANROOM_CLUSTER_GID"] = str(gid)

    # To suppress warning below during docker compose execution set the env. variable if not set:
    # WARN[0000] The "GITHUB_ACTIONS" variable is not set. Defaulting to a blank string.
    if "GITHUB_ACTIONS" not in os.environ:
        os.environ["GITHUB_ACTIONS"] = "false"
    if "CODESPACES" not in os.environ:
        os.environ["CODESPACES"] = "false"

    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE"] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE"] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE"] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_VIRTUAL_IMAGE" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_VIRTUAL_IMAGE"] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE"] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_LOCAL_SKR_IMAGE" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_LOCAL_SKR_IMAGE"] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL"] = ""
    if "AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL" not in os.environ:
        os.environ["AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL"] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_USE_HTTP" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_USE_HTTP"] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE"] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL"
        ] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL"
        ] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE"] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"
        ] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL"] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_IMAGE"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_IMAGE"
        ] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_SECURITY_POLICY_DOCUMENT_URL"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_SECURITY_POLICY_DOCUMENT_URL"
        ] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_CHART_URL"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_CHART_URL"
        ] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_OHTTP_GATEWAY_IMAGE" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_OHTTP_GATEWAY_IMAGE"] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_IMAGE"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_IMAGE"
        ] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"
        ] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_CHART_URL"
        not in os.environ
    ):
        os.environ[
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_CHART_URL"
        ] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_FLEX_NODE_IMAGE_DIGESTS_URL" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_FLEX_NODE_IMAGE_DIGESTS_URL"] = ""
    if (
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_API_SERVER_PROXY_PACKAGE_URL"
        not in os.environ
    ):
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_API_SERVER_PROXY_PACKAGE_URL"] = ""
    if "AZCLI_CLEANROOM_CLUSTER_PROVIDER_KUBELET_PROXY_PACKAGE_URL" not in os.environ:
        os.environ["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KUBELET_PROXY_PACKAGE_URL"] = ""


def parse_ip_tags(ip_tags):
    # Parses Azure CLI 'IpTagType=Tag' pairs (e.g. 'RoutingPreference=Internet') into the
    # Azure Public IP Address IpTag schema entries ({"ipTagType": ..., "tag": ...}).
    parsed = []
    for item in ip_tags:
        if "=" not in item:
            raise CLIError(
                f"Invalid --ip-tags value '{item}'. Expected 'IpTagType=Tag' format, "
                "e.g. 'RoutingPreference=Internet'."
            )
        tag_type, tag_value = item.split("=", 1)
        tag_type = tag_type.strip()
        tag_value = tag_value.strip()
        if not tag_type or not tag_value:
            raise CLIError(
                f"Invalid --ip-tags value '{item}'. Both the IP tag type and tag value "
                "must be non-empty, e.g. 'RoutingPreference=Internet'."
            )
        parsed.append({"ipTagType": tag_type, "tag": tag_value})
    return parsed


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


def requires_provider_config(infra_type):
    return infra_type == "aks"


def _read_file_content(path, label):
    """Read file content with a user-facing error on missing file."""
    if not os.path.exists(path):
        raise CLIError(f"{label} file not found: {path}")
    with open(path, "r") as f:
        return f.read()


def build_flex_node_profile(flex_node_profile):
    """Build the flexNodeProfile API body from a profile JSON file or string.

    The profile JSON uses file paths for SSH keys and the policy signing
    cert. This function resolves those paths to file content for the
    provider API.
    """
    if os.path.exists(flex_node_profile):
        profile = get_file_json(flex_node_profile)
    else:
        profile = shell_safe_json_parse(flex_node_profile)

    ssh_priv_path = profile.get("sshPrivateKey")
    ssh_private_key_pem = None
    if ssh_priv_path:
        ssh_private_key_pem = _read_file_content(ssh_priv_path, "SSH private key")

    ssh_pub_path = profile.get("sshPublicKey")
    ssh_public_key = None
    if ssh_pub_path:
        ssh_public_key = _read_file_content(ssh_pub_path, "SSH public key").strip()

    cert_path = profile.get("policySigningCert")
    policy_signing_cert_pem = None
    if cert_path:
        policy_signing_cert_pem = _read_file_content(cert_path, "Policy signing cert")

    flex_profile = {
        "enabled": True,
        "sshPrivateKeyPem": ssh_private_key_pem,
        "sshPublicKey": ssh_public_key,
        "policySigningCertPem": policy_signing_cert_pem,
        "vmSize": profile.get("vmSize"),
        "nodeCount": profile.get("nodeCount", 1),
    }
    max_pods = profile.get("maxPodsPerNode")
    if max_pods is not None:
        flex_profile["maxPodsPerNode"] = max_pods
    if profile.get("insecure", False):
        flex_profile["insecure"] = True
    if profile.get("provisionUsingSSH", False):
        flex_profile["provisionUsingSSH"] = True
    if profile.get("requirePreProvisionedKindNodes", False):
        flex_profile["requirePreProvisionedKindNodes"] = True
    gpu_config = profile.get("gpu")
    if gpu_config is not None:
        flex_profile["gpu"] = gpu_config

    return flex_profile
