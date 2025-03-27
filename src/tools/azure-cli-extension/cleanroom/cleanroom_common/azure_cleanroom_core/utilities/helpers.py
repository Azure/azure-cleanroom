import ast
import base64
import json
import logging
from string import Template
from urllib.parse import urlparse
import os

import oras.client
import yaml

from ..exceptions.exception import (
    ErrorCode,
    CleanroomSpecificationError,
)

from ..models.model import *

DEFAULT_CLEANROOM_CONTAINER_REGISTRY_URL = "mcr.microsoft.com/azurecleanroom"
DEFAULT_CLEANROOM_CONTAINER_VERSION = "3.0.0"
DEFAULT_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = (
    "mcr.microsoft.com/azurecleanroom/sidecar-digests:"
    + DEFAULT_CLEANROOM_CONTAINER_VERSION
)
DEFAULT_SCRATCH_DIR = os.path.dirname(os.path.realpath(__file__))
CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_NAME = "sidecar-digests.yaml"

template_folder = (
    f"{os.path.dirname(__file__)}{os.path.sep}..{os.path.sep}templates{os.path.sep}"
)

REGO_FILE_PATH = template_folder + "/cleanroom-policy.rego"

VOLUMESTATUS_MOUNT_PATH = "/mnt/volumestatus"
TELEMETRY_MOUNT_PATH = "/mnt/telemetry"


class Sidecar:
    def __init__(self, template_json: dict, policy_json: dict, policy_rego: dict):
        self.template_json = template_json
        self.policy_json = policy_json
        self.policy_rego = policy_rego


def get_sidecar(
    sidecar_name: str,
    sidecar_replacement_vars: dict,
    debug_mode: bool,
    logger: logging.Logger,
):
    sidecar = [x for x in get_sidecars_version(logger) if x["image"] == sidecar_name][0]
    sidecar_replacement_vars["containerRegistryUrl"] = get_containers_registry_url()
    sidecar_replacement_vars["digest"] = sidecar["digest"]

    sidecar_policy_document = get_sidecar_policy_document(sidecar_name, logger)
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
    "ccr-init": lambda telemetry_mount_path, volumestatus_mount_path: {
        "telemetryMountPath": telemetry_mount_path,
        "volumeStatusMountPath": volumestatus_mount_path,
    },
    "skr": lambda telemetry_mount_path: {
        "telemetryMountPath": telemetry_mount_path,
    },
    "otel-collector": lambda telemetry_mount_path: {
        "telemetryMountPath": telemetry_mount_path
    },
    "ccr-governance": lambda ccf_endpoint, contract_id, service_cert_base64, telemetry_mount_path: {
        "cgsEndpoint": ccf_endpoint,
        "contractId": contract_id,
        "serviceCertBase64": service_cert_base64,
        "telemetryMountPath": telemetry_mount_path,
    },
    "ccr-secrets": lambda telemetry_mount_path, identity_port="8290", skr_port="8284": {
        "identityPort": identity_port,
        "skrPort": skr_port,
        "telemetryMountPath": telemetry_mount_path,
    },
    "ccr-attestation": lambda telemetry_mount_path: {
        "telemetryMountPath": telemetry_mount_path,
    },
    "ccr-proxy": lambda telemetry_mount_path: {
        "telemetryMountPath": telemetry_mount_path
    },
    "ccr-proxy-ext-processor": lambda name, policy_bundle_url, allow_all, port, telemetry_mount_path: {
        "name": name,
        "policyBundleUrl": policy_bundle_url,
        "allowAll": allow_all,
        "port": port,
        "telemetryMountPath": telemetry_mount_path,
    },
    "identity": lambda identities, subject, audience, telemetry_mount_path: get_identity_sidecar(
        identities, subject, audience, telemetry_mount_path
    ),
}


def get_network_sidecars(
    spec: CleanRoomSpecification,
    debug_mode: bool,
    logger: logging.Logger,
):
    allow_http_inbound_access = False
    allow_http_outbound_access = False
    allow_tcp_outbound_access = False
    sidecars = []
    if spec.network:
        if spec.network.http:
            if spec.network.http.inbound:
                allow_http_inbound_access = True

                allow_all = False
                policy_bundle_url = ""
                # Add a ccr-proxy-ext-processor sidecar for inbound policy.
                if spec.network.http.inbound.policy.privacyPolicy:
                    assert isinstance(
                        spec.network.http.inbound.policy.privacyPolicy.policy,
                        ExternalPolicy,
                    ), "Privacy policy must be of type ExternalPolicy."
                    policy_bundle_url = (
                        spec.network.http.inbound.policy.privacyPolicy.policy.backingResource.provider.url
                    )
                else:
                    allow_all = True

                sidecars.append(
                    get_sidecar(
                        "ccr-proxy-ext-processor",
                        sidecar_replacement_vars["ccr-proxy-ext-processor"](
                            "inbound",
                            policy_bundle_url,
                            str(allow_all).lower(),
                            8282,
                            TELEMETRY_MOUNT_PATH,
                        ),
                        debug_mode,
                        logger,
                    )
                )

            if spec.network.http.outbound:
                allow_http_outbound_access = True

                allow_all = False
                policy_bundle_url = ""
                if spec.network.http.outbound.policy.privacyPolicy:
                    assert isinstance(
                        spec.network.http.outbound.policy.privacyPolicy.policy,
                        ExternalPolicy,
                    ), "Privacy policy must be of type ExternalPolicy."
                    policy_bundle_url = (
                        spec.network.http.outbound.policy.privacyPolicy.policy.backingResource.provider.url
                    )
                else:
                    allow_all = True
                sidecars.append(
                    get_sidecar(
                        "ccr-proxy-ext-processor",
                        sidecar_replacement_vars["ccr-proxy-ext-processor"](
                            "outbound",
                            policy_bundle_url,
                            str(allow_all).lower(),
                            8283,
                            TELEMETRY_MOUNT_PATH,
                        ),
                        debug_mode,
                        logger,
                    )
                )
        if spec.network.tcp:
            allow_tcp_outbound_access = True

    ccr_proxy_sidecar_replacement_vars = {
        "telemetryMountPath": TELEMETRY_MOUNT_PATH,
        "allowHttpOutboundAccess": str(allow_http_outbound_access).lower(),
        "allowHttpInboundAccess": str(allow_http_inbound_access).lower(),
        "allowTcpOutboundAccess": str(allow_tcp_outbound_access).lower(),
    }
    sidecars.append(
        get_sidecar("ccr-proxy", ccr_proxy_sidecar_replacement_vars, debug_mode, logger)
    )

    return get_ccr_init(spec, debug_mode, logger), sidecars


def get_ccr_init(
    spec: CleanRoomSpecification, debug_mode: bool, logger: logging.Logger
):
    ccr_init_cmd = []
    allowed_ips = []
    if spec.network:
        if spec.network.tcp:
            for endpoint in spec.network.tcp.outbound.allowedIPs:
                allowed_ips.append({"address": endpoint.address, "port": endpoint.port})
        if spec.network.dns:
            ccr_init_cmd.append("--enable-dns")
            ccr_init_cmd.append("--dns-port")
            ccr_init_cmd.append(f"{spec.network.dns.port}")

    if len(allowed_ips) > 0:
        ccr_init_cmd.append("--allowed-ips")
        ccr_init_cmd.append(json.dumps(allowed_ips))

    ccr_init_sidecar_replacement_vars = {
        "telemetryMountPath": TELEMETRY_MOUNT_PATH,
        "volumeStatusMountPath": VOLUMESTATUS_MOUNT_PATH,
    }

    ccr_init = get_sidecar(
        "ccr-init", ccr_init_sidecar_replacement_vars, debug_mode, logger
    )

    ccr_init.template_json["properties"]["command"].extend(ccr_init_cmd)

    ccr_init.policy_json["command"].extend(ccr_init_cmd)

    # Check for command key presence before extending as the policy_rego might not be generated if
    # the policy document was created without pre-computed rego-policy.
    if "command" in ccr_init.policy_rego:
        ccr_init.policy_rego["command"].extend(ccr_init_cmd)

    return ccr_init


def pretty_print_func(x) -> str:
    return json.dumps(x, separators=(",", ":"), sort_keys=True)


def get_containers_registry_url():
    return os.environ.get(
        "AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL",
        DEFAULT_CLEANROOM_CONTAINER_REGISTRY_URL,
    )


def get_scratch_dir():
    return os.environ.get(
        "SCRATCH_DIR",
        DEFAULT_SCRATCH_DIR,
    )


def get_sidecars_version(logger: logging.Logger):
    # Download the sidecar versions document.
    dir_path = get_scratch_dir()

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

    dir_name = (
        versions_registry_url.replace("/", "_").replace(":", "_").replace(".", "_")
    )
    dir_path = os.path.join(bin_folder, dir_name)

    import threading

    lock = threading.Lock()
    if not os.path.exists(
        os.path.join(dir_path, CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_NAME)
    ):
        with lock:
            if not os.path.exists(
                os.path.join(dir_path, CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_NAME)
            ):
                os.makedirs(dir_path, exist_ok=True)
                insecure = False
                if urlparse("https://" + versions_registry_url).hostname == "localhost":
                    insecure = True

                client = oras.client.OrasClient(
                    hostname="https://" + versions_registry_url,
                    insecure=insecure,
                )
                client.pull(
                    target="https://" + versions_registry_url,
                    outdir=dir_path,
                )

    with open(os.path.join(dir_path, CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_NAME)) as f:
        sidecars_version = yaml.safe_load(f)
    return sidecars_version


def get_sidecar_policy_document(imageName: str, logger: logging.Logger):
    dir_path = get_scratch_dir()

    bin_folder = os.path.join(dir_path, "bin")
    if not os.path.exists(bin_folder):
        os.makedirs(bin_folder)

    policy_registry_url = get_containers_registry_url()

    sidecar = [x for x in get_sidecars_version(logger) if x["image"] == imageName][0]
    insecure = False
    if urlparse("https://" + policy_registry_url).hostname == "localhost":
        insecure = True
    policy_document_url = (
        f"{policy_registry_url}/policies/"
        + f"{sidecar['policyDocument']}@{sidecar['policyDocumentDigest']}"
    )

    dir_name = policy_registry_url.replace("/", "_").replace(":", "_").replace(".", "_")
    dir_path = os.path.join(bin_folder, dir_name)

    import threading

    lock = threading.Lock()
    if not os.path.exists(os.path.join(dir_path, sidecar["policyDocument"] + ".yaml")):
        with lock:
            if not os.path.exists(
                os.path.join(dir_path, sidecar["policyDocument"] + ".yaml")
            ):
                os.makedirs(dir_path, exist_ok=True)
                client = oras.client.OrasClient(
                    hostname="https://" + policy_registry_url,
                    insecure=insecure,
                )
                client.pull(
                    target=policy_document_url,
                    outdir=dir_path,
                )

    with open(os.path.join(dir_path, sidecar["policyDocument"] + ".yaml")) as f:
        return yaml.safe_load(f)


def replace_vars(content: str, vars: dict):
    spec = Template(content).substitute(vars)
    return json.loads(spec)


def get_code_launcher(
    application: Application,
    debug_mode: bool,
    logger: logging.Logger,
):
    application_name = application.name
    memoryInGb = application.runtimeSettings.resource.requests.memoryInGB
    cpu = application.runtimeSettings.resource.requests.cpu

    application_base64 = base64.b64encode(
        application.model_dump_json().encode()
    ).decode()
    code_launcher_cmd = []
    code_launcher_cmd.append("--application-base-64")
    code_launcher_cmd.append(application_base64)

    code_launcher_sidecar_template_vars = {
        "applicationName": application_name,
        "cpu": cpu,
        "memoryInGB": memoryInGb,
        "telemetryMountPath": TELEMETRY_MOUNT_PATH,
        "volumeStatusMountPath": VOLUMESTATUS_MOUNT_PATH,
    }

    code_launcher_sidecar = get_sidecar(
        "code-launcher",
        code_launcher_sidecar_template_vars,
        debug_mode,
        logger,
    )

    code_launcher_sidecar.template_json["properties"]["command"].extend(
        code_launcher_cmd
    )

    if len(application.runtimeSettings.ports) > 0:
        for port in application.runtimeSettings.ports:
            code_launcher_sidecar.template_json["properties"]["ports"].append(
                {"port": f"{port}", "protocol": "TCP"}
            )

    code_launcher_sidecar.policy_json["command"].extend(code_launcher_cmd)

    # Check for command key presence before extending as the policy_rego might not be generated if
    # the policy document was created without pre-computed rego-policy.
    if "command" in code_launcher_sidecar.policy_rego:
        code_launcher_sidecar.policy_rego["command"].extend(code_launcher_cmd)

    return code_launcher_sidecar


def get_identity_sidecar(
    identities: List[Identity], subject, audience, telemetry_mount_path
):
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
        "telemetryMountPath": telemetry_mount_path,
    }


def get_blobfuse_sidecar(
    access_point: AccessPoint,
    mount_path,
    encryption_mode,
    access_name,
    debug_mode: bool,
    logger: logging.Logger,
):
    assert (
        access_point.protection.encryptionSecrets
    ), f"Encryption secrets is null for {access_name}."
    kek_entry = access_point.protection.encryptionSecrets.kek
    dek_entry = access_point.protection.encryptionSecrets.dek

    kek_kv_url = urlparse(kek_entry.secret.backingResource.provider.url).hostname
    assert (
        kek_entry.secret.backingResource.provider.configuration
    ), f"KEK configuration is null for {access_name}."
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
        "telemetryMountPath": TELEMETRY_MOUNT_PATH,
        "volumeStatusMountPath": VOLUMESTATUS_MOUNT_PATH,
    }

    blobfuse_sidecar = get_sidecar(
        "blobfuse-launcher",
        blobfuse_sidecar_replacement_vars,
        debug_mode,
        logger,
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
    logger: logging.Logger,
):
    sidecars: list[Sidecar] = []
    if get_containers_registry_url() != DEFAULT_CLEANROOM_CONTAINER_REGISTRY_URL:
        logger.warning(
            f"Using cleanroom containers registry override: {get_containers_registry_url()}"
        )

    with open(template_folder + "aci-base-arm-template.json", "r") as fp:
        arm_template = json.load(fp)

    for application in cleanroom_spec.applications:
        code_launcher = get_code_launcher(application, debug_mode, logger)
        sidecars.append(code_launcher)

    sidecars.append(
        get_sidecar(
            "identity",
            sidecar_replacement_vars["identity"](
                cleanroom_spec.identities,
                contract_id,
                "api://AzureADTokenExchange",
                TELEMETRY_MOUNT_PATH,
            ),
            debug_mode,
            logger,
        )
    )

    ccr_init, network_sidecars = get_network_sidecars(
        cleanroom_spec, debug_mode, logger
    )
    sidecars.extend(network_sidecars)

    gov_opa_policy_digest = [
        x
        for x in get_sidecars_version(logger)
        if x["image"] == "ccr-governance-opa-policy"
    ][0]["digest"]
    sidecars.append(
        get_sidecar(
            "ccr-proxy-ext-processor",
            sidecar_replacement_vars["ccr-proxy-ext-processor"](
                "gov",
                f"{get_containers_registry_url()}/policies/ccr-governance-opa-policy@{gov_opa_policy_digest}",
                str(False).lower(),
                8281,
                TELEMETRY_MOUNT_PATH,
            ),
            debug_mode,
            logger,
        )
    )
    arm_template["resources"][0]["properties"]["ipAddress"]["type"] = "Public"

    # Add in the governance port for the cleanroom.
    arm_template["resources"][0]["properties"]["ipAddress"]["ports"] = [
        {"port": "8200", "protocol": "TCP"}
    ]

    # Adding the tag with the value of the contract id.
    arm_template["resources"][0]["tags"]["accr-contract-id"] = contract_id
    arm_template["resources"][0]["tags"][
        "accr-version"
    ] = DEFAULT_CLEANROOM_CONTAINER_VERSION

    if (
        cleanroom_spec.network
        and cleanroom_spec.network.http
        and cleanroom_spec.network.http.inbound
    ):
        for application in cleanroom_spec.applications:
            for port in application.runtimeSettings.ports:
                arm_template["resources"][0]["properties"]["ipAddress"]["ports"].append(
                    {"port": f"{port}", "protocol": "TCP"}
                )
    else:
        logger.warning(
            "Inbound traffic is not enabled. Not adding any application ports to the deployment template."
        )

    for access_point in cleanroom_spec.datasources + cleanroom_spec.datasinks:
        import ast

        assert (
            access_point.protection.configuration
        ), f"Protection configuration is null for {access_point.name}."

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
                    logger,
                )
            )
        sidecars.append(
            get_blobfuse_sidecar(
                access_point,
                "/mnt/remote",
                encryption_mode,
                access_point.name,
                debug_mode,
                logger,
            )
        )

    sidecars.append(
        get_sidecar(
            "otel-collector",
            sidecar_replacement_vars["otel-collector"](TELEMETRY_MOUNT_PATH),
            debug_mode,
            logger,
        )
    )

    sidecars.append(
        get_sidecar(
            "ccr-governance",
            sidecar_replacement_vars["ccr-governance"](
                ccf_endpoint, contract_id, sslServerCertBase64, TELEMETRY_MOUNT_PATH
            ),
            debug_mode,
            logger,
        )
    )
    sidecars.append(
        get_sidecar(
            "ccr-attestation",
            sidecar_replacement_vars["ccr-attestation"](TELEMETRY_MOUNT_PATH),
            debug_mode,
            logger,
        )
    )

    sidecars.append(
        get_sidecar(
            "skr",
            sidecar_replacement_vars["skr"](TELEMETRY_MOUNT_PATH),
            debug_mode,
            logger,
        )
    )
    sidecars.append(
        get_sidecar(
            "ccr-secrets",
            sidecar_replacement_vars["ccr-secrets"](TELEMETRY_MOUNT_PATH),
            debug_mode,
            logger,
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

    assert (
        cleanroom_spec.sandbox == None
        or cleanroom_spec.sandbox.sandboxType != SandBoxType.None_
    ), f"Unsupported sandbox type {cleanroom_spec.sandbox}."

    arm_template["resources"][0]["properties"]["initContainers"].append(
        ccr_init.template_json
    )
    policy_template["containers"].append(ccr_init.policy_json)
    container_rego_policies.append(ccr_init.policy_rego)

    rego_policy = get_rego_policy(container_rego_policies)

    rego_policy = get_rego_policy(container_rego_policies)
    return arm_template, policy_template, rego_policy


def validate_config(spec: CleanRoomSpecification, logger: logging.Logger):

    # TODO (HPrabh): Update the validate function to check the whole spec for anomalies.
    issues = []
    warnings = []
    seen = set()
    dupes = []
    for application in spec.applications:
        if application.datasources:
            for datasource in application.datasources.keys():
                index = next(
                    (i for i, x in enumerate(spec.datasources) if x.name == datasource),
                    None,
                )
                if index == None:
                    logger.error(
                        f"Datasource {datasource} not found in the cleanroom specification."
                    )
                    issues.append(
                        CleanroomSpecificationError(
                            ErrorCode.DatastoreNotFound,
                            f"Datasource {datasource} not found in the cleanroom specification.",
                        )
                    )

        if application.datasinks:
            for datasink in application.datasinks.keys():
                index = next(
                    (i for i, x in enumerate(spec.datasinks) if x.name == datasink),
                    None,
                )
                if index == None:
                    logger.error(
                        f"Datasink {datasink} not found in the cleanroom specification."
                    )
                    issues.append(
                        CleanroomSpecificationError(
                            ErrorCode.DatasinkNotFound,
                            f"Datasink {datasink} not found in the cleanroom specification.",
                        )
                    )

    if spec.network:
        if spec.network.http:
            if spec.network.http.inbound:
                if not spec.network.http.inbound.policy.privacyPolicy:
                    warnings.append(
                        {
                            "code": "InboundAllowAll",
                            "message": "Inbound traffic is allowed. Configure a network policy to restrict traffic.",
                        }
                    )
            else:
                if len(seen) > 0:
                    warnings.append(
                        {
                            "code": "InboundTrafficNotAllowed",
                            "message": "Application ports are defined but no inbound traffic is disabled. "
                            + "Please run `az cleanroom config network enable http` to enable inbound traffic.",
                        }
                    )

            if spec.network.http.outbound:
                if not spec.network.http.outbound.policy.privacyPolicy:
                    warnings.append(
                        {
                            "code": "OutboundAllowAll",
                            "message": "Outbound traffic is allowed. Configure a network policy to restrict traffic.",
                        }
                    )

    if len(spec.applications) > 1:
        warnings.append(
            {
                "code": "MultipleApplications",
                "message": "Multiple applications are defined in the specification. "
                + "Please verify that the associated network policies handle expected ingress / egress.",
            }
        )

    if len(dupes) > 0:
        issues.append(
            CleanroomSpecificationError(
                ErrorCode.DuplicatePort,
                f"Port {dupes} appear more than once in the application(s). "
                + "A port value can be used only once.",
            )
        )

    return issues, warnings
