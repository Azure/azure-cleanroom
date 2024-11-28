import ast
import base64
import json
from string import Template
from urllib.parse import urlparse
import os

import oras.client
import yaml
from knack.log import get_logger
from azure.cli.core.util import CLIError

from ..models.model import *

DEFAULT_CLEANROOM_CONTAINER_REGISTRY_URL = "mcr.microsoft.com/cleanroom"
DEFAULT_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = (
    "mcr.microsoft.com/cleanroom/sidecar-digests:2.0.0"
)
CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_NAME = "sidecar-digests.yaml"

template_folder = (
    f"{os.path.dirname(__file__)}{os.path.sep}..{os.path.sep}templates{os.path.sep}"
)

REGO_FILE_PATH = template_folder + "/cleanroom-policy.rego"
g_sidecars_version = None

logger = get_logger(__name__)


class Sidecar:
    def __init__(self, template_json: dict, policy_json: dict, policy_rego: dict):
        self.template_json = template_json
        self.policy_json = policy_json
        self.policy_rego = policy_rego


def get_sidecar(sidecar_name: str, sidecar_replacement_vars: dict, debug_mode: bool):
    sidecar = [x for x in get_sidecars_version() if x["image"] == sidecar_name][0]
    sidecar_replacement_vars["containerRegistryUrl"] = get_containers_registry_url()
    sidecar_replacement_vars["digest"] = sidecar["digest"]

    sidecar_policy_document = get_sidecar_policy_document(sidecar_name)
    sidecar_template_json = replace_vars(
        json.dumps(sidecar_policy_document["templateJson"]),
        sidecar_replacement_vars,
    )
    sidecar_policy_json = replace_vars(
        json.dumps(sidecar_policy_document["policy"]["json"]),
        sidecar_replacement_vars,
    )
    node = "rego"
    if debug_mode:
        logger.warning(
            f"Using debug policy for sidecar {sidecar_name}. This should only be used for development purposes."
        )
        node = "rego_debug"
    sidecar_policy_rego = replace_vars(
        json.dumps(sidecar_policy_document["policy"][node]),
        sidecar_replacement_vars,
    )
    return Sidecar(sidecar_template_json, sidecar_policy_json, sidecar_policy_rego)


sidecar_replacement_vars = {
    "ccr-init": lambda mode: {"mode": mode},
    "skr": lambda: {},
    "otel-collector": lambda: {},
    "ccr-governance": lambda ccf_endpoint, contract_id, service_cert_base64: {
        "cgsEndpoint": ccf_endpoint,
        "contractId": contract_id,
        "serviceCertBase64": service_cert_base64,
    },
    "ccr-secrets": lambda identity_port="8290", skr_port="8284": {
        "identityPort": identity_port,
        "skrPort": skr_port,
    },
    "ccr-attestation": lambda: {},
    "ccr-proxy": lambda: {},
    "ccr-proxy-ext-processor": lambda policy_bundle_url, allow_all: {
        "policyBundleUrl": policy_bundle_url,
        "allowAll": allow_all,
    },
    "identity": lambda identities, subject, audience: get_identity_sidecar(
        identities, subject, audience
    ),
}


def pretty_print_func(x) -> str:
    return json.dumps(x, separators=(",", ":"), sort_keys=True)


def get_containers_registry_url():
    return os.environ.get(
        "AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL",
        DEFAULT_CLEANROOM_CONTAINER_REGISTRY_URL,
    )


def get_sidecars_version():
    global g_sidecars_version
    if g_sidecars_version is not None:
        return g_sidecars_version

    # Download the sidecar versions document.
    dir_path = os.path.dirname(os.path.realpath(__file__))
    bin_folder = os.path.join(dir_path, "bin")
    if not os.path.exists(bin_folder):
        os.makedirs(bin_folder)

    versions_registry_url = os.environ.get(
        "AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL",
        DEFAULT_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL,
    )

    if versions_registry_url != DEFAULT_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL:
        logger.warning(
            f"Using cleanroom containers versions registry override: {versions_registry_url}"
        )

    insecure = False
    if urlparse("https://" + versions_registry_url).hostname == "localhost":
        insecure = True

    client = oras.client.OrasClient(
        hostname="https://" + versions_registry_url,
        insecure=insecure,
    )
    client.pull(
        target="https://" + versions_registry_url,
        outdir=bin_folder,
    )

    with open(os.path.join(bin_folder, CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_NAME)) as f:
        g_sidecars_version = yaml.safe_load(f)
    return g_sidecars_version


def get_sidecar_policy_document(imageName: str):
    dir_path = os.path.dirname(os.path.realpath(__file__))
    bin_folder = os.path.join(dir_path, "bin")
    if not os.path.exists(bin_folder):
        os.makedirs(bin_folder)

    policy_registry_url = get_containers_registry_url()

    sidecar = [x for x in get_sidecars_version() if x["image"] == imageName][0]
    insecure = False
    if urlparse("https://" + policy_registry_url).hostname == "localhost":
        insecure = True
    policy_document_url = (
        f"{policy_registry_url}/policies/"
        + f"{sidecar['policyDocument']}@{sidecar['policyDocumentDigest']}"
    )

    client = oras.client.OrasClient(
        hostname="https://" + policy_registry_url,
        insecure=insecure,
    )
    client.pull(
        target=policy_document_url,
        outdir=bin_folder,
    )

    with open(os.path.join(bin_folder, sidecar["policyDocument"] + ".yaml")) as f:
        return yaml.safe_load(f)


def replace_vars(content: str, vars: dict):
    spec = Template(content).substitute(vars)
    return json.loads(spec)


def get_code_launcher(application: Application, debug_mode: bool):
    application_name = application.name
    container_url = application.image.executable.backingResource.id
    memoryInGb = application.runtimeSettings.resource.requests.memoryInGB
    cpu = application.runtimeSettings.resource.requests.cpu
    mounts = application.runtimeSettings.mounts
    code_launcher_cmd = []

    code_launcher_mount_points = []
    application_mount_points = []
    acr_url = application.image.executable.backingResource.provider.url
    identity = application.image.executable.identity

    if identity is not None:
        code_launcher_cmd.append("--private-acr-fqdn")
        code_launcher_cmd.append(acr_url)
        code_launcher_cmd.append("--client-id")
        code_launcher_cmd.append(identity.clientId)
        code_launcher_cmd.append("--tenant-id")
        code_launcher_cmd.append(identity.tenantId)

    for key, value in mounts.items():
        datastore = key.split("=")[1]
        mount_path = value.split("=")[1]
        code_launcher_mount_points.append(f"/mnt/remote/{datastore}")
        application_mount_points.append(f"-v=/mnt/remote/{datastore}:{mount_path}")

    if len(code_launcher_mount_points) > 0:
        code_launcher_cmd.append("--wait-on-application-mounts")
        code_launcher_cmd.extend(code_launcher_mount_points)

    code_launcher_cmd.extend(["--", "--name", application_name])
    code_launcher_cmd.extend(application_mount_points)
    for key, value in application.environmentVariables.items():
        code_launcher_cmd.append(f"-e {key}={value}")

    code_launcher_cmd.append(container_url)
    if application.command != None:
        code_launcher_cmd.extend(application.command)

    code_launcher_sidecar_template_vars = {
        "applicationName": application_name,
        "cpu": cpu,
        "memoryInGB": memoryInGb,
    }

    code_launcher_sidecar = get_sidecar(
        "code-launcher", code_launcher_sidecar_template_vars, debug_mode
    )

    code_launcher_sidecar.template_json["properties"]["command"].extend(
        code_launcher_cmd
    )

    if len(application.runtimeSettings.ports) > 0:
        portProperties = []
        for port in application.runtimeSettings.ports:
            portProperties.append({"port": f"{port}", "protocol": "TCP"})
        code_launcher_sidecar.template_json["properties"]["ports"] = portProperties

    code_launcher_sidecar.policy_json["command"].extend(code_launcher_cmd)

    # Check for command key presence before extending as the policy_rego might not be generated if
    # the policy document was created without pre-computed rego-policy.
    if "command" in code_launcher_sidecar.policy_rego:
        code_launcher_sidecar.policy_rego["command"].extend(code_launcher_cmd)

    return code_launcher_sidecar


def get_identity_sidecar(identities: List[Identity], subject, audience):
    identity_args = {
        "Identities": {"ManagedIdentities": [], "ApplicationIdentities": []}
    }

    # TODO (HPrabh): Cleanup this logic to convert the class into IdentityConfiguration.
    for identity in identities:
        if identity.tokenIssuer.issuerType == "AttestationBasedTokenIssuer":
            if (
                identity.tokenIssuer.issuer.protocol
                == ProtocolType.AzureAD_ManagedIdentity
            ):
                identity_args["Identities"]["ManagedIdentities"].append(
                    {"ClientId": identity.clientId}
                )
        elif identity.tokenIssuer.issuerType == "FederatedIdentityBasedTokenIssuer":
            identity_args["Identities"]["ApplicationIdentities"].append(
                {
                    "ClientId": identity.clientId,
                    "Credential": {
                        "CredentialType": "FederatedCredential",
                        "FederationConfiguration": {
                            "IdTokenEndpoint": "http://localhost:8300",
                            "Subject": f"{subject}",
                            "Audience": f"{audience}",
                        },
                    },
                }
            )

    identity_args_base64 = base64.b64encode(
        bytes(json.dumps(identity_args), "utf-8")
    ).decode("utf-8")

    return {
        "IdentitySidecarArgsBase64": identity_args_base64,
        "OtelMetricExportInterval": 5000,
    }


def get_blobfuse_sidecar(
    access_point: AccessPoint,
    mount_path,
    encryption_mode,
    access_name,
    debug_mode: bool,
):
    assert access_point.protection.encryptionSecrets
    kek_entry = access_point.protection.encryptionSecrets.kek
    dek_entry = access_point.protection.encryptionSecrets.dek

    kek_kv_url = urlparse(kek_entry.secret.backingResource.provider.url).hostname
    assert kek_entry.secret.backingResource.provider.configuration
    maa_url = urlparse(
        ast.literal_eval(kek_entry.secret.backingResource.provider.configuration)[
            "authority"
        ]
    ).hostname
    storage_account_name = urlparse(access_point.store.provider.url).hostname.split(
        "."
    )[0]
    subdirectory = ""
    storageBlobEndpoint = access_point.store.provider.url
    storageContainerName = access_point.store.name
    is_onelake = False
    if access_point.store.provider.protocol == ProtocolType.Azure_OneLake:
        is_onelake = True
        storage_account_name = "onelake"
        parsed_onelake_url = urlparse(access_point.store.provider.url)
        storageBlobEndpoint = parsed_onelake_url.hostname
        storageContainerName = parsed_onelake_url.path.split("/")[1]
        subdirectory = "/".join(parsed_onelake_url.path.split("/")[2:])

    # TODO (HPrabh): Change the plain volume access name to "access_name" and cipher one to "access_name-cipher".
    blobfuse_sidecar_replacement_vars = {
        "datasetName": (
            (access_name + "-plain") if encryption_mode == "None" else access_name
        ),
        "storageContainerName": storageContainerName,
        "mountPath": mount_path,
        "maaUrl": maa_url,
        "storageAccountName": storage_account_name,
        "storageBlobEndpoint": storageBlobEndpoint,
        "readOnly": (
            "--read-only"
            if access_point.type == AccessPointType.Volume_ReadOnly
            else "--no-read-only"
        ),
        "kekVaultUrl": kek_kv_url,
        "kekKid": kek_entry.secret.backingResource.name,
        "dekSecretName": dek_entry.secret.backingResource.name,
        "dekVaultUrl": dek_entry.secret.backingResource.provider.url,
        "clientId": access_point.identity.clientId,
        "tenantId": access_point.identity.tenantId,
        "encryptionMode": encryption_mode,
        "useAdls": ("--use-adls" if is_onelake else "--no-use-adls"),
    }

    blobfuse_sidecar = get_sidecar(
        "blobfuse-launcher", blobfuse_sidecar_replacement_vars, debug_mode
    )
    blobfuse_launcher_cmd = []
    if subdirectory != "":
        blobfuse_launcher_cmd.append("--sub-directory")
        blobfuse_launcher_cmd.append(subdirectory)

    blobfuse_sidecar.template_json["properties"]["command"].extend(
        blobfuse_launcher_cmd
    )
    blobfuse_sidecar.policy_json["command"].extend(blobfuse_launcher_cmd)
    if "command" in blobfuse_sidecar.policy_rego:
        blobfuse_sidecar.policy_rego["command"].extend(blobfuse_launcher_cmd)

    return blobfuse_sidecar


def get_rego_policy(container_policy_rego: list) -> str:
    placeholder_rego_str = ""
    with open(REGO_FILE_PATH, "r", encoding="utf-8") as file:
        placeholder_rego_str = file.read()
    container_regos = []
    for container_rego in container_policy_rego:
        container_regos.append(pretty_print_func(container_rego))
    container_regos = ",".join(container_regos)
    return placeholder_rego_str % (container_regos)


def get_deployment_template(
    cleanroom_spec: CleanRoomSpecification,
    contract_id: str,
    ccf_endpoint: str,
    sslServerCertBase64: str,
    debug_mode: bool,
):
    sidecars: list[Sidecar] = []
    if get_containers_registry_url() != DEFAULT_CLEANROOM_CONTAINER_REGISTRY_URL:
        logger.warning(
            f"Using cleanroom containers registry override: {get_containers_registry_url()}"
        )

    with open(template_folder + "aci-base-arm-template.json", "r") as fp:
        arm_template = json.load(fp)

    for application in cleanroom_spec.applications:
        code_launcher = get_code_launcher(application, debug_mode)
        sidecars.append(code_launcher)

    sidecars.append(
        get_sidecar(
            "identity",
            sidecar_replacement_vars["identity"](
                cleanroom_spec.identities, contract_id, "api://AzureADTokenExchange"
            ),
            debug_mode,
        )
    )

    network_firewall_mode = "no-network-access"
    if len(cleanroom_spec.applicationEndpoints) > 1:
        # TODO (HPrabh): Fail the deployment generate because we cannot handle multiple policy
        # bundles for now.
        raise CLIError(
            "Deployment generate can work only if a single application endpoint is specified"
        )

    # Adding the tag with the value of the contract id.
    arm_template["resources"][0]["tags"]["accr-contract-id"] = contract_id

    for application_endpoint in cleanroom_spec.applicationEndpoints:
        network_firewall_mode = "proxy"
        if "ipAddress" not in arm_template["resources"][0]["properties"]:
            arm_template["resources"][0]["properties"]["ipAddress"] = {
                "type": "Public",
                "ports": [],
            }

            arm_template["resources"][0]["properties"]["ipAddress"]["ports"].append(
                {"port": f"{application_endpoint.port}", "protocol": "TCP"}
            )
            sidecars.append(
                get_sidecar(
                    "ccr-proxy", sidecar_replacement_vars["ccr-proxy"](), debug_mode
                )
            )

            policy_bundle_url = ""
            allow_all = False
            if application_endpoint.protection.privacyPolicy:
                assert isinstance(
                    application_endpoint.protection.privacyPolicy.policy, ExternalPolicy
                )
                policy_bundle_url = (
                    application_endpoint.protection.privacyPolicy.policy.backingResource.provider.url
                )
            else:
                allow_all = True

            sidecars.append(
                get_sidecar(
                    "ccr-proxy-ext-processor",
                    sidecar_replacement_vars["ccr-proxy-ext-processor"](
                        policy_bundle_url, str(allow_all).lower()
                    ),
                    debug_mode,
                )
            )

    for access_point in cleanroom_spec.datasources + cleanroom_spec.datasinks:
        import ast

        assert access_point.protection.configuration

        encryption_config = ast.literal_eval(access_point.protection.configuration)
        encryption_mode = encryption_config["EncryptionMode"]

        if encryption_mode == "CSE":
            sidecars.append(
                get_blobfuse_sidecar(
                    access_point,
                    "/mnt/remote",
                    "None",
                    access_point.name,
                    debug_mode,
                )
            )
        sidecars.append(
            get_blobfuse_sidecar(
                access_point,
                "/mnt/remote",
                encryption_mode,
                access_point.name,
                debug_mode,
            )
        )

    sidecars.append(
        get_sidecar(
            "otel-collector", sidecar_replacement_vars["otel-collector"](), debug_mode
        )
    )

    sidecars.append(
        get_sidecar(
            "ccr-governance",
            sidecar_replacement_vars["ccr-governance"](
                ccf_endpoint, contract_id, sslServerCertBase64
            ),
            debug_mode,
        )
    )
    sidecars.append(
        get_sidecar(
            "ccr-attestation", sidecar_replacement_vars["ccr-attestation"](), debug_mode
        )
    )

    sidecars.append(get_sidecar("skr", sidecar_replacement_vars["skr"](), debug_mode))
    sidecars.append(
        get_sidecar(
            "ccr-secrets", sidecar_replacement_vars["ccr-secrets"](), debug_mode
        )
    )

    with open(template_folder + "cleanroom-template-policy.json", "r") as fp:
        policy_template = json.load(fp)

    container_rego_policies = []
    for sidecar in sidecars:
        arm_template["resources"][0]["properties"]["containers"].append(
            sidecar.template_json
        )

        policy_template["containers"].append(sidecar.policy_json)
        container_rego_policies.append(sidecar.policy_rego)

    if (
        cleanroom_spec.sandbox == None
        or cleanroom_spec.sandbox.sandboxType != SandBoxType.None_
    ):
        network_firewall = get_sidecar(
            "ccr-init",
            sidecar_replacement_vars["ccr-init"](network_firewall_mode),
            debug_mode,
        )
        arm_template["resources"][0]["properties"]["initContainers"].append(
            network_firewall.template_json
        )
        policy_template["containers"].append(network_firewall.policy_json)
        container_rego_policies.append(network_firewall.policy_rego)

    rego_policy = get_rego_policy(container_rego_policies)
    return arm_template, policy_template, rego_policy
